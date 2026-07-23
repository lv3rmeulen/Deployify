using System.Text;
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
        KubernetesTargetConfig k8SConfig)
    {
        var results = new List<KubernetesServiceManifest>();

        foreach (var (name, resource) in manifest.Resources)
        {
            if (resource.Type is "value.v0" or "parameter.v0" or "dapr.v0" or "dapr.component.v0")
            {
	            continue;
            }

            config.ServiceOverrides.TryGetValue(name, out var overrides);

            var ns = $"{k8SConfig.NamespacePrefix}-{name}";

            var svc = new KubernetesServiceManifest
            {
                ServiceName = name,
                Namespace = ns,
                ImageTag = overrides?.ImageTag ?? config.ImageTag,
                Replicas = overrides?.Count ?? k8SConfig.DefaultReplicas,
                CpuRequest = k8SConfig.CpuRequest,
                CpuLimit = k8SConfig.CpuLimit,
                MemoryRequest = k8SConfig.MemoryRequest,
                MemoryLimit = k8SConfig.MemoryLimit,
                IngressClassName = k8SConfig.IngressClassName,
                Dependencies = resource.References?.Select(r => r.Name).ToList() ?? []
            };

            // Resolve image
            if (resource.Type.StartsWith("container.v"))
            {
	            svc.Image = resource.Image;
            }
            else if (resource.Type.StartsWith("project.v"))
            {
	            svc.Image = config.ContainerRegistry is not null
		            ? $"{config.ContainerRegistry.TrimEnd('/')}/{name}"
		            : name;
            }

            // Env vars
            if (resource.Env is not null)
            {
	            foreach (var (k, v) in resource.Env)
	            {
		            svc.Env[k] = v;
	            }
            }

            if (overrides?.Env is not null)
            {
	            foreach (var (k, v) in overrides.Env)
	            {
		            svc.Env[k] = v;
	            }
            }

            // Ports
            if (resource.Bindings is not null)
            {
                foreach (var (label, binding) in resource.Bindings)
                {
                    svc.Ports.Add(new KubernetesPort
                    {
                        Name = label,
                        ContainerPort = binding.ContainerPort ?? 80,
                        Protocol = binding.Protocol?.ToUpperInvariant() == "UDP" ? "UDP" : "TCP"
                    });
                }
            }

            // Ingress — only if there's an HTTP port and a base domain is configured
            var httpPort = svc.Ports.FirstOrDefault(p => p.IsHttp);
            if (k8SConfig.GenerateIngress && httpPort is not null && k8SConfig.IngressBaseDomain is not null)
            {
                svc.HasIngress = true;
                svc.IngressHost = $"{name}.{k8SConfig.IngressBaseDomain}";
            }

            // Dapr — read annotations from resource (injected by WithDaprIfEnabled or set by Aspire)
            if (resource.HasDaprSidecar)
            {
                svc.DaprEnabled = true;
                svc.DaprAppId = resource.DaprAppId ?? name;
                svc.DaprAppPort = resource.DaprAppPort;
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

        return results;
    }

    public async Task<List<string>> WriteYamlAsync(
        KubernetesServiceManifest svc,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(outputDirectory, svc.ServiceName);
        Directory.CreateDirectory(dir);

        var files = new List<string> { await Write(dir, "namespace.yaml", RenderNamespace(svc), ct) };

        if (svc.HasConfigMap)
        {
	        files.Add(await Write(dir, "configmap.yaml", RenderConfigMap(svc), ct));
        }

        files.Add(await Write(dir, "deployment.yaml", RenderDeployment(svc), ct));
        if (svc.HasService)
        {
	        files.Add(await Write(dir, "service.yaml", RenderService(svc), ct));
        }

        if (svc.HasIngress)
        {
	        files.Add(await Write(dir, "ingress.yaml", RenderIngress(svc), ct));
        }

        if (svc.HasPvc)
        {
	        files.Add(await Write(dir, "pvc.yaml", RenderPvc(svc), ct));
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
            sb.AppendLine("    annotations:");
            sb.AppendLine($"      {DaprConstants.EnabledAnnotation}: \"true\"");
            sb.AppendLine($"      {DaprConstants.AppIdAnnotation}: \"{svc.DaprAppId ?? svc.ServiceName}\"");
            sb.AppendLine($"      {DaprConstants.HttpPortAnnotation}: \"{svc.DaprHttpPort}\"");
            sb.AppendLine($"      {DaprConstants.GrpcPortAnnotation}: \"{svc.DaprGrpcPort}\"");
            if (svc.DaprAppPort.HasValue)
            {
	            sb.AppendLine($"      {DaprConstants.AppPortAnnotation}: \"{svc.DaprAppPort}\"");
            }
        }

        sb.AppendLine($"""
    spec:
      containers:
        - name: {svc.ServiceName}
          image: {svc.Image}:{svc.ImageTag}
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

        if (svc.HasConfigMap)
        {
            sb.AppendLine("          envFrom:");
            sb.AppendLine("            - configMapRef:");
            sb.AppendLine($"                name: {svc.ServiceName}-config");
        }

        sb.AppendLine($"""
          resources:
            requests:
              cpu: {svc.CpuRequest}
              memory: {svc.MemoryRequest}
            limits:
              cpu: {svc.CpuLimit}
              memory: {svc.MemoryLimit}
          livenessProbe:
            httpGet:
              path: /health
              port: {(svc.Ports.FirstOrDefault(p => p.IsHttp)?.Name ?? svc.Ports.FirstOrDefault()?.Name ?? "http")}
            initialDelaySeconds: 10
            periodSeconds: 15
          readinessProbe:
            httpGet:
              path: /health/ready
              port: {(svc.Ports.FirstOrDefault(p => p.IsHttp)?.Name ?? svc.Ports.FirstOrDefault()?.Name ?? "http")}
            initialDelaySeconds: 5
            periodSeconds: 10
""");

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

    private static Task<string> Write(string dir, string filename, string content, CancellationToken ct)
    {
        var path = Path.Combine(dir, filename);
        return File.WriteAllTextAsync(path, content, ct).ContinueWith(_ => path, ct);
    }

    private static string QuoteYaml(string value)
    {
        if (value.Contains(':') || value.Contains('#') || value.Contains('"') || value.StartsWith('{'))
        {
	        return $"\"{value.Replace("\"", "\\\"")}\"";
        }

        return value;
    }
}
