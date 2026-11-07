using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;

namespace Deployify.Target.Kubernetes;

/// <summary>
/// Generates Kubernetes YAML manifests from Aspire resources.
/// One namespace per service. Generates: Namespace, ConfigMap, Deployment, Service, Ingress, PVC.
/// </summary>
public sealed class KubernetesManifestGenerator
{
    public IReadOnlyList<KubernetesServiceManifest> Generate(
        AspireManifest manifest,
        EnvironmentConfig config,
        KubernetesTargetConfig kubernetesConfig)
    {
        var results = new List<KubernetesServiceManifest>();
        var ports = ServicePorts.Assign(manifest, kubernetesConfig.BasePort);

        // Every service lives in its own namespace, so peers are addressed as
        // <service>.<namespace> — a bare service name only resolves inside one namespace.
        var resolver = new PlaceholderResolver(manifest, ports, new EndpointStrategy
        {
            Host = res => $"{res}.{NamespaceFor(kubernetesConfig, res)}"
        }, ParameterResolution.For(config));

        foreach (var (name, resource) in manifest.Resources)
        {
            if (!resource.IsDeployable)
            {
                continue;
            }

            config.ServiceOverrides.TryGetValue(name, out var overrides);

            var svc = new KubernetesServiceManifest
            {
                ServiceName = name,
                Namespace = NamespaceFor(kubernetesConfig, name),
                ImageTag = overrides?.ImageTag ?? config.ImageTag,
                Replicas = overrides?.Count ?? kubernetesConfig.DefaultReplicas,
                CpuRequest = kubernetesConfig.CpuRequest,
                CpuLimit = kubernetesConfig.CpuLimit,
                MemoryRequest = kubernetesConfig.MemoryRequest,
                MemoryLimit = kubernetesConfig.MemoryLimit,
                IngressClassName = kubernetesConfig.IngressClassName,
                Dependencies = resource.References?.Select(r => r.Name).ToList() ?? [],
                HealthCheckType = kubernetesConfig.HealthCheckType.ToLowerInvariant(),
                HealthCheckPath = kubernetesConfig.HealthCheckPath,
                Image = resource.Type.StartsWith("project.", StringComparison.Ordinal)
                    ? config.ContainerRegistry is not null
                        ? $"{config.ContainerRegistry.TrimEnd('/')}/{name}"
                        : name
                    : resource.Image
            };

            // Values that contain a secret parameter are kept apart and stored the target's own way.
            var env = resolver.ResolveEnvironment(resource.Env, overrides?.Env);
            svc.Env = env.Plain;
            svc.SecretEnv = env.Secrets;

            if (resource.Bindings is not null)
            {
                foreach (var (label, binding) in resource.Bindings)
                {
                    svc.Ports.Add(new KubernetesPort
                    {
                        Name = PortName(label),
                        ContainerPort = ports[(name, label)],
                        Protocol = binding.Protocol?.ToUpperInvariant() == "UDP" ? "UDP" : "TCP",
                        IsHttp = binding.IsHttp
                    });
                }
            }

            // Ingress — only for an external HTTP binding when a base domain is configured
            var externalHttp = resource.Bindings?.Any(b => b.Value.IsHttp && b.Value.IsExternal) == true;
            if (kubernetesConfig.GenerateIngress && externalHttp && kubernetesConfig.IngressBaseDomain is not null)
            {
                svc.HasIngress = true;
                svc.IngressHost = $"{name}.{kubernetesConfig.IngressBaseDomain}";
            }

            if (resource.HasDaprSidecar)
            {
                svc.DaprEnabled = true;
                svc.DaprAppId = resource.DaprAppId ?? name;
                svc.DaprAppPort = resource.HttpBinding is { } http ? ports[(name, http.Key)] : resource.DaprAppPort;
                if (resource.Annotations!.TryGetValue(DaprConstants.HttpPortAnnotation, out var hp)
                    && int.TryParse(hp, out var httpP))
                {
                    svc.DaprHttpPort = httpP;
                }

                if (resource.Annotations!.TryGetValue(DaprConstants.GrpcPortAnnotation, out var gp)
                    && int.TryParse(gp, out var grpcP))
                {
                    svc.DaprGrpcPort = grpcP;
                }
            }

            results.Add(svc);
        }

        Unresolved = [.. resolver.Unresolved];
        return results;
    }

    /// <summary>Placeholders from the last <see cref="Generate"/> call that could not be resolved.</summary>
    public IReadOnlyList<string> Unresolved { get; private set; } = [];

    private static string NamespaceFor(KubernetesTargetConfig cfg, string service)
        => $"{cfg.NamespacePrefix}-{service}";

    /// <summary>Kubernetes port names: max 15 chars, lowercase alphanumerics and '-'.</summary>
    private static string PortName(string label)
    {
        var name = new string(label.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (name.Length > 15)
        {
            name = name[..15].TrimEnd('-');
        }

        return name.Length == 0 ? "port" : name;
    }

    public async Task<List<string>> WriteYamlAsync(
        KubernetesServiceManifest svc,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(outputDirectory, svc.ServiceName);
        Directory.CreateDirectory(dir);

        var files = new List<string> { await WriteAsync(dir, "namespace.yaml", RenderNamespace(svc), ct) };

        if (svc.HasConfigMap)
        {
            files.Add(await WriteAsync(dir, "configmap.yaml", RenderConfigMap(svc), ct));
        }

        var secretPath = Path.Combine(dir, "secret.yaml");
        if (svc.HasSecret)
        {
            files.Add(await WriteAsync(dir, "secret.yaml", RenderSecret(svc), ct));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(secretPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        else if (File.Exists(secretPath))
        {
            // apply reads every file in the folder; don't leave a secret behind that no longer exists.
            File.Delete(secretPath);
        }

        files.Add(await WriteAsync(dir, "deployment.yaml", RenderDeployment(svc), ct));
        if (svc.HasService)
        {
            files.Add(await WriteAsync(dir, "service.yaml", RenderService(svc), ct));
        }

        if (svc.HasIngress)
        {
            files.Add(await WriteAsync(dir, "ingress.yaml", RenderIngress(svc), ct));
        }

        if (svc.HasPvc)
        {
            files.Add(await WriteAsync(dir, "pvc.yaml", RenderPvc(svc), ct));
        }

        svc.GeneratedFiles = files;
        return files;
    }

    // ── YAML renderers ────────────────────────────────────────────────────────

    private static string RenderNamespace(KubernetesServiceManifest svc) => $"""
apiVersion: v1
kind: Namespace
metadata:
  name: {svc.Namespace}
  labels:
    app.kubernetes.io/managed-by: deployify
    deployify/service: {svc.ServiceName}
""";

    private static string RenderConfigMap(KubernetesServiceManifest svc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"""
apiVersion: v1
kind: ConfigMap
metadata:
  name: {svc.ServiceName}-config
  namespace: {svc.Namespace}
  labels:
    app: {svc.ServiceName}
    app.kubernetes.io/managed-by: deployify
data:
""");
        foreach (var (k, v) in svc.Env)
        {
            sb.AppendLine($"  {k}: {QuoteYaml(v)}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Env vars that contain a secret parameter go in an Opaque Secret instead of the
    /// ConfigMap, so they are stored and access-controlled as secrets in the cluster.
    /// </summary>
    private static string RenderSecret(KubernetesServiceManifest svc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"""
apiVersion: v1
kind: Secret
metadata:
  name: {SecretName(svc)}
  namespace: {svc.Namespace}
  labels:
    app: {svc.ServiceName}
    app.kubernetes.io/managed-by: deployify
type: Opaque
stringData:
""");
        foreach (var (k, v) in svc.SecretEnv)
        {
            sb.AppendLine($"  {k}: {QuoteYaml(v.ToString())}");
        }

        return sb.ToString();
    }

    internal static string SecretName(KubernetesServiceManifest svc) => $"{svc.ServiceName}-secrets";

    private static string RenderDeployment(KubernetesServiceManifest svc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"""
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {svc.ServiceName}
  namespace: {svc.Namespace}
  labels:
    app: {svc.ServiceName}
    app.kubernetes.io/managed-by: deployify
spec:
  replicas: {svc.Replicas}
  selector:
    matchLabels:
      app: {svc.ServiceName}
  template:
    metadata:
      labels:
        app: {svc.ServiceName}
""");

        if (svc.DaprEnabled)
        {
            // The Dapr sidecar injector only reads dapr.io/* annotations. The sidecar's own
            // HTTP/gRPC ports are fixed by Dapr on Kubernetes (3500/50001).
            sb.AppendLine("    annotations:");
            sb.AppendLine("      dapr.io/enabled: \"true\"");
            sb.AppendLine($"      dapr.io/app-id: {QuoteYaml(svc.DaprAppId ?? svc.ServiceName)}");
            if (svc.DaprAppPort.HasValue)
            {
                sb.AppendLine($"      dapr.io/app-port: \"{svc.DaprAppPort}\"");
            }
        }

        sb.AppendLine($"""
    spec:
      containers:
        - name: {svc.ServiceName}
          image: {QuoteYaml(ImageReference.Format(svc.Image!, svc.ImageTag))}
          imagePullPolicy: Always
""");

        if (svc.Ports.Count > 0)
        {
            sb.AppendLine("          ports:");
            foreach (var p in svc.Ports)
            {
                sb.AppendLine($"""
            - name: {p.Name}
              containerPort: {p.ContainerPort}
              protocol: {p.Protocol}
""");
            }
        }

        if (svc.HasConfigMap || svc.HasSecret)
        {
            sb.AppendLine("          envFrom:");
            if (svc.HasConfigMap)
            {
                sb.AppendLine("            - configMapRef:");
                sb.AppendLine($"                name: {svc.ServiceName}-config");
            }

            if (svc.HasSecret)
            {
                sb.AppendLine("            - secretRef:");
                sb.AppendLine($"                name: {SecretName(svc)}");
            }
        }

        sb.AppendLine($"""
          resources:
            requests:
              cpu: {svc.CpuRequest}
              memory: {svc.MemoryRequest}
            limits:
              cpu: {svc.CpuLimit}
              memory: {svc.MemoryLimit}
""");

        // Probes: TCP by default; HTTP only on an HTTP port when explicitly configured.
        var probePort = svc.Ports.FirstOrDefault(p => p.IsHttp) ?? svc.Ports.FirstOrDefault();
        if (probePort is not null && svc.HealthCheckType != "none")
        {
            var useHttp = svc.HealthCheckType == "http" && probePort.IsHttp;
            foreach (var (probe, delay, period) in new[] { ("livenessProbe", 15, 20), ("readinessProbe", 5, 10) })
            {
                sb.AppendLine($"          {probe}:");
                if (useHttp)
                {
                    sb.AppendLine("            httpGet:");
                    sb.AppendLine($"              path: {QuoteYaml(svc.HealthCheckPath)}");
                    sb.AppendLine($"              port: {probePort.Name}");
                }
                else
                {
                    sb.AppendLine("            tcpSocket:");
                    sb.AppendLine($"              port: {probePort.Name}");
                }

                sb.AppendLine($"            initialDelaySeconds: {delay}");
                sb.AppendLine($"            periodSeconds: {period}");
            }
        }

        return sb.ToString();
    }

    private static string RenderService(KubernetesServiceManifest svc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"""
apiVersion: v1
kind: Service
metadata:
  name: {svc.ServiceName}
  namespace: {svc.Namespace}
  labels:
    app: {svc.ServiceName}
    app.kubernetes.io/managed-by: deployify
spec:
  selector:
    app: {svc.ServiceName}
  ports:
""");
        foreach (var p in svc.Ports)
        {
            sb.AppendLine($"""
    - name: {p.Name}
      port: {p.ContainerPort}
      targetPort: {p.Name}
      protocol: {p.Protocol}
""");
        }

        sb.AppendLine("  type: ClusterIP");
        return sb.ToString();
    }

    private static string RenderIngress(KubernetesServiceManifest svc)
    {
        var httpPort = svc.Ports.First(p => p.IsHttp);
        return $"""
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {svc.ServiceName}
  namespace: {svc.Namespace}
  labels:
    app: {svc.ServiceName}
    app.kubernetes.io/managed-by: deployify
  annotations:
    nginx.ingress.kubernetes.io/rewrite-target: /
spec:
  ingressClassName: {svc.IngressClassName}
  rules:
    - host: {svc.IngressHost}
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: {svc.ServiceName}
                port:
                  name: {httpPort.Name}
""";
    }

    private static string RenderPvc(KubernetesServiceManifest svc) => $"""
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: {svc.ServiceName}-pvc
  namespace: {svc.Namespace}
  labels:
    app: {svc.ServiceName}
    app.kubernetes.io/managed-by: deployify
spec:
  accessModes:
    - ReadWriteOnce
  resources:
    requests:
      storage: {svc.PvcStorageSize ?? "1Gi"}
""";

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a manifest with LF line endings. The YAML templates are raw string literals,
    /// which take their line endings from the source file, so normalizing here keeps the
    /// output identical however the repository is checked out.
    /// </summary>
    private static async Task<string> WriteAsync(string dir, string filename, string content, CancellationToken ct)
    {
        var path = Path.Combine(dir, filename);
        await File.WriteAllTextAsync(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(false), ct);
        return path;
    }

    /// <summary>
    /// Always emits a double-quoted YAML scalar. Unquoted values such as <c>true</c>,
    /// <c>8080</c> or an empty string would be typed as bool/int/null, which kubectl
    /// rejects in a ConfigMap. A JSON string literal is a valid YAML double-quoted scalar.
    /// </summary>
    internal static string QuoteYaml(string? value)
        => System.Text.Json.JsonSerializer.Serialize(value ?? string.Empty);
}
