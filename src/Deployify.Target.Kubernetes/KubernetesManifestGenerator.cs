using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.SecretBackends.KeyVault;
using Deployify.Core.SecretBackends.Vault;

namespace Deployify.Target.Kubernetes;

/// <summary>
/// Generates Kubernetes YAML manifests from Aspire resources.
/// One namespace per service. Generates: Namespace, ConfigMap, Deployment, Service, Ingress, PVC.
/// </summary>
public sealed class KubernetesManifestGenerator
{
    /// <summary>Env var pointing at the file the Vault agent renders.</summary>
    public const string SecretsFileVariable = "DEPLOYIFY_SECRETS_FILE";

    public async Task<IReadOnlyList<KubernetesServiceManifest>> GenerateAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        KubernetesTargetConfig kubernetesConfig, CancellationToken ct = default)
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
                Image = resource.IsProject
                    ? config.ContainerRegistry is not null
                        ? $"{config.ContainerRegistry.TrimEnd('/')}/{name}"
                        : name
                    : resource.Image
            };

            ApplyImagePullSecrets(svc, config, kubernetesConfig);

            // Values that contain a secret parameter are kept apart and stored the target's own way.
            var env = await resolver.ResolveEnvironmentAsync(resource.Env, overrides?.Env, ct);
            svc.Env = env.Plain;
            svc.SecretEnv = env.Secrets;
            ApplySecretSettings(svc, config, kubernetesConfig, resource);

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

    /// <summary>
    /// The pod's <c>imagePullSecrets</c>: the configured ones, plus the registry Secret Deployify
    /// maintains when registryCredentials is set.
    /// </summary>
    private static void ApplyImagePullSecrets(KubernetesServiceManifest svc, EnvironmentConfig config, KubernetesTargetConfig kubernetesConfig)
    {
        var names = new List<string>();
        foreach (var name in kubernetesConfig.ImagePullSecrets)
        {
            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name.Trim(), StringComparer.Ordinal))
            {
                names.Add(name.Trim());
            }
        }

        if (kubernetesConfig.RegistryCredentials is { } registry)
        {
            var server = string.IsNullOrWhiteSpace(registry.Server) ? RegistryHost(config.ContainerRegistry) : registry.Server.Trim();
            if (server is null)
            {
                throw new InvalidOperationException(
                    "kubernetes.registryCredentials needs a server: set registryCredentials.server, or containerRegistry.");
            }

            var secretName = string.IsNullOrWhiteSpace(registry.SecretName) ? "deployify-registry" : registry.SecretName.Trim();
            svc.RegistrySecret = new RegistryPullSecret(secretName, server, registry.Username, registry.Password);
            if (!names.Contains(secretName, StringComparer.Ordinal))
            {
                names.Add(secretName);
            }
        }

        svc.ImagePullSecrets = names;
    }

    /// <summary>The host part of a registry reference such as <c>registry.example.com:5000/team/app</c>.</summary>
    internal static string? RegistryHost(string? containerRegistry)
    {
        if (string.IsNullOrWhiteSpace(containerRegistry))
        {
            return null;
        }

        var value = containerRegistry.Trim();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            value = value[(scheme + 3)..];
        }

        var slash = value.IndexOf('/');
        return slash >= 0 ? value[..slash] : value;
    }

    private static void ApplySecretSettings(
        KubernetesServiceManifest svc, EnvironmentConfig config, KubernetesTargetConfig kubernetesConfig, AspireResource resource)
    {
        svc.SecretBackend = config.Secrets.Backend;
        svc.IsProject = resource.IsProject;
        if (svc.SecretBackend == SecretBackendType.KeyVault)
        {
            var keyVault = config.Secrets.KeyVault;
            svc.KeyVault = keyVault;
            svc.KeyVaultPrefix = KeyVaultPrefix(keyVault, kubernetesConfig);
            if (svc.HasSecret)
            {
                ValidateKeyVault(keyVault);
            }

            return;
        }

        if (svc.SecretBackend != SecretBackendType.Vault)
        {
            return;
        }

        var vault = config.Secrets.Vault;
        svc.VaultMount = vault.Mount;
        svc.VaultPath = VaultServicePath(vault, kubernetesConfig, svc.ServiceName);
        svc.VaultRole = $"{kubernetesConfig.NamespacePrefix}-{svc.ServiceName}";
        svc.VaultSecretsFileName = vault.SecretsFileName;
        svc.VaultAgentAnnotations = new Dictionary<string, string>(vault.AgentAnnotations, StringComparer.Ordinal);
    }

    /// <summary>Key Vault secret name prefix: <c>secretPrefix</c>, or <c>deployify-{namespacePrefix}</c>.</summary>
    public static string KeyVaultPrefix(KeyVaultConfig keyVault, KubernetesTargetConfig kubernetesConfig)
        => KeyVaultSecrets.SecretName(string.IsNullOrWhiteSpace(keyVault.SecretPrefix)
            ? $"deployify-{kubernetesConfig.NamespacePrefix}"
            : keyVault.SecretPrefix);

    private static void ValidateKeyVault(KeyVaultConfig keyVault)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(keyVault.VaultName) && string.IsNullOrWhiteSpace(keyVault.VaultUri))
        {
            missing.Add("vaultName");
        }

        if (string.IsNullOrWhiteSpace(keyVault.TenantId))
        {
            missing.Add("tenantId");
        }

        if (string.IsNullOrWhiteSpace(keyVault.ClientId))
        {
            missing.Add("clientId");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"The KeyVault secret backend on Kubernetes needs secrets.keyVault.{string.Join(", secrets.keyVault.", missing)}.");
        }
    }

    /// <summary>Name of the SecretProviderClass of a service.</summary>
    internal static string SecretProviderClassName(KubernetesServiceManifest svc) => $"{svc.ServiceName}-keyvault";

    /// <summary>Where the CSI driver mounts Key Vault secrets in the container.</summary>
    public const string KeyVaultMountPath = "/mnt/secrets-store";

    /// <summary>Vault KV path of one service's secrets: <c>{pathPrefix}/{namespacePrefix}/{service}</c>.</summary>
    public static string VaultServicePath(VaultConfig vault, KubernetesTargetConfig kubernetesConfig, string service)
        => $"{vault.PathPrefix.Trim('/')}/{kubernetesConfig.NamespacePrefix}/{service}";

    /// <summary>Vault KV path where generated parameter values are kept.</summary>
    public static string VaultParametersPath(VaultConfig vault, KubernetesTargetConfig kubernetesConfig)
        => VaultServicePath(vault, kubernetesConfig, "_parameters");

    /// <summary>Path inside the pod where the Vault agent writes the secrets file.</summary>
    public static string VaultSecretsFilePath(KubernetesServiceManifest svc) => $"/vault/secrets/{svc.VaultSecretsFileName}";

    /// <summary>Placeholders from the last <see cref="Generate"/> call that could not be resolved.</summary>
    public IReadOnlyList<string> Unresolved { get; private set; } = [];

    internal static string NamespaceFor(KubernetesTargetConfig cfg, string service)
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

    /// <summary>File holding the service's inline secret values; written readable by the owner only.</summary>
    internal const string SecretFileName = "secret.yaml";

    /// <summary>
    /// Files a service has only with some settings. apply reads every file in the folder, so one
    /// the service no longer has (above all a secret) is deleted rather than left behind.
    /// </summary>
    private static readonly string[] s_optionalFiles =
        [SecretFileName, "serviceaccount.yaml", "vault-policy.hcl", "secretproviderclass.yaml"];

    public async Task<List<string>> WriteYamlAsync(
        KubernetesServiceManifest svc,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(outputDirectory, svc.ServiceName);
        Directory.CreateDirectory(dir);

        var rendered = Render(svc);
        var files = new List<string>();
        foreach (var (name, content) in rendered)
        {
            files.Add(await WriteAsync(dir, name, content, ct));
            if (name == SecretFileName)
            {
                RestrictToOwner(files[^1]);
            }
        }

        foreach (var stale in s_optionalFiles.Except(rendered.Select(r => r.FileName)))
        {
            File.Delete(Path.Combine(dir, stale));
        }

        // Whatever the renderers do, an external backend means no secret value on disk.
        if (svc.SecretBackend != SecretBackendType.Inline && svc.HasSecret)
        {
            SecretLeakScanner.EnsureClean(files, svc.SecretData);
        }

        svc.GeneratedFiles = files;
        return files;
    }

    /// <summary>
    /// Every file of one service, in apply order, as (file name, content). The Helm chart writer
    /// templates these same manifests, so kubectl and Helm deploy identical objects.
    /// </summary>
    internal static IReadOnlyList<(string FileName, string Content)> Render(KubernetesServiceManifest svc)
    {
        var files = new List<(string, string)> { ("namespace.yaml", RenderNamespace(svc)) };

        if (svc.HasConfigMap)
        {
            files.Add(("configmap.yaml", RenderConfigMap(svc)));
        }

        if (svc.WritesSecretManifest)
        {
            files.Add((SecretFileName, RenderSecret(svc)));
        }

        // Vault: the pod logs in with its own service account, bound to a read-only policy.
        if (svc.NeedsServiceAccount)
        {
            files.Add(("serviceaccount.yaml", RenderServiceAccount(svc)));
        }

        if (svc.UsesVaultAgent)
        {
            // For reference (and for setting up roles by hand when manageAuth is off); kubectl ignores .hcl files.
            files.Add(("vault-policy.hcl", VaultPolicy.ReadOnly(svc.VaultKvApiPath!)));
        }

        // Key Vault: the CSI driver reads the secrets with the pod's identity and syncs them into a Secret.
        if (svc.UsesKeyVaultCsi)
        {
            files.Add(("secretproviderclass.yaml", RenderSecretProviderClass(svc)));
        }

        files.Add(("deployment.yaml", RenderDeployment(svc)));
        if (svc.HasService)
        {
            files.Add(("service.yaml", RenderService(svc)));
        }

        if (svc.HasIngress)
        {
            files.Add(("ingress.yaml", RenderIngress(svc)));
        }

        if (svc.HasPvc)
        {
            files.Add(("pvc.yaml", RenderPvc(svc)));
        }

        return files;
    }

    /// <summary>Makes a file that holds secret values readable and writable by its owner only.</summary>
    internal static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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

    private static string RenderServiceAccount(KubernetesServiceManifest svc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"""
apiVersion: v1
kind: ServiceAccount
metadata:
  name: {svc.ServiceName}
  namespace: {svc.Namespace}
  labels:
    app: {svc.ServiceName}
    app.kubernetes.io/managed-by: deployify
""");
        if (UsesWorkloadIdentity(svc))
        {
            // Workload ID exchanges this ServiceAccount's token for one of the managed identity.
            sb.AppendLine("  annotations:");
            sb.AppendLine($"    azure.workload.identity/client-id: {QuoteYaml(svc.KeyVault!.ClientId)}");
            sb.AppendLine($"    azure.workload.identity/tenant-id: {QuoteYaml(svc.KeyVault.TenantId)}");
        }

        return sb.ToString();
    }

    private static bool UsesWorkloadIdentity(KubernetesServiceManifest svc)
        => svc.UsesKeyVaultCsi && svc.KeyVault!.KubernetesIdentity == KeyVaultKubernetesIdentity.WorkloadIdentity;

    /// <summary>
    /// A SecretProviderClass for the Azure provider: which vault secrets to mount (under their
    /// parameter id) and the Secret to sync them into, which the env vars reference.
    /// </summary>
    private static string RenderSecretProviderClass(KubernetesServiceManifest svc)
    {
        var keyVault = svc.KeyVault!;
        var objects = svc.KeyVaultObjectNames.OrderBy(o => o.Key, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"""
apiVersion: secrets-store.csi.x-k8s.io/v1
kind: SecretProviderClass
metadata:
  name: {SecretProviderClassName(svc)}
  namespace: {svc.Namespace}
  labels:
    app: {svc.ServiceName}
    app.kubernetes.io/managed-by: deployify
spec:
  provider: azure
  secretObjects:
    - secretName: {SecretName(svc)}
      type: Opaque
      labels:
        app: {svc.ServiceName}
      data:
""");
        foreach (var (id, _) in objects)
        {
            sb.AppendLine($"        - objectName: {QuoteYaml(id)}");
            sb.AppendLine($"          key: {QuoteYaml(id)}");
        }

        sb.AppendLine("  parameters:");
        sb.AppendLine("    usePodIdentity: \"false\"");
        if (keyVault.KubernetesIdentity == KeyVaultKubernetesIdentity.VmManagedIdentity)
        {
            sb.AppendLine("    useVMManagedIdentity: \"true\"");
            sb.AppendLine($"    userAssignedIdentityID: {QuoteYaml(keyVault.ClientId)}");
        }
        else
        {
            sb.AppendLine($"    clientID: {QuoteYaml(keyVault.ClientId)}");
        }

        sb.AppendLine($"    keyvaultName: {QuoteYaml(KeyVaultSecrets.VaultName(keyVault))}");
        sb.AppendLine($"    tenantId: {QuoteYaml(keyVault.TenantId)}");
        if (!string.IsNullOrWhiteSpace(keyVault.CloudName))
        {
            sb.AppendLine($"    cloudName: {QuoteYaml(keyVault.CloudName)}");
        }

        // The provider reads "objects" as a YAML document of its own.
        var list = new StringBuilder("array:\n");
        foreach (var (id, name) in objects)
        {
            list.Append("  - |\n")
                .Append($"    objectName: {name}\n")
                .Append("    objectType: secret\n")
                .Append($"    objectAlias: {id}\n");
        }

        AppendYamlEntry(sb, 4, "objects", list.ToString());
        return sb.ToString();
    }

    /// <summary>Pod-template annotations: Dapr sidecar and Vault agent.</summary>
    private static List<(string Key, string Value)> PodAnnotations(KubernetesServiceManifest svc)
    {
        var annotations = new List<(string, string)>();
        if (svc.DaprEnabled)
        {
            // The Dapr sidecar injector only reads dapr.io/* annotations. The sidecar's own
            // HTTP/gRPC ports are fixed by Dapr on Kubernetes (3500/50001).
            annotations.Add(("dapr.io/enabled", "true"));
            annotations.Add(("dapr.io/app-id", svc.DaprAppId ?? svc.ServiceName));
            if (svc.DaprAppPort.HasValue)
            {
                annotations.Add(("dapr.io/app-port", svc.DaprAppPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        if (svc.UsesVaultAgent)
        {
            var file = svc.VaultSecretsFileName;
            annotations.Add(("vault.hashicorp.com/agent-inject", "true"));
            annotations.Add(("vault.hashicorp.com/role", svc.VaultRole!));
            annotations.Add(($"vault.hashicorp.com/agent-inject-secret-{file}", svc.VaultKvApiPath!));
            annotations.Add(($"vault.hashicorp.com/agent-inject-template-{file}",
                VaultTemplate.JsonConfigFile(svc.VaultKvApiPath!, svc.SecretEnv)));
            foreach (var (k, v) in svc.VaultAgentAnnotations.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                annotations.Add((k, v));
            }
        }

        return annotations;
    }

    /// <summary>Writes a YAML mapping entry; multi-line values become literal block scalars.</summary>
    private static void AppendYamlEntry(StringBuilder sb, int indent, string key, string value)
    {
        var pad = new string(' ', indent);
        if (!value.Contains('\n'))
        {
            sb.AppendLine($"{pad}{key}: {QuoteYaml(value)}");
            return;
        }

        // |- keeps the text exactly, minus the final newline. Templates have no tabs or
        // leading spaces on their first line, so a block scalar holds them safely.
        sb.AppendLine($"{pad}{key}: |-");
        foreach (var line in value.TrimEnd('\n').Split('\n'))
        {
            sb.AppendLine(line.Length == 0 ? string.Empty : $"{pad}  {line}");
        }
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
        if (UsesWorkloadIdentity(svc))
        {
            sb.AppendLine("        azure.workload.identity/use: \"true\"");
        }

        // Pod annotations belong under template.metadata, next to its labels.
        var annotations = PodAnnotations(svc);
        if (annotations.Count > 0)
        {
            sb.AppendLine("      annotations:");
            foreach (var (key, value) in annotations)
            {
                AppendYamlEntry(sb, 8, key, value);
            }
        }

        sb.AppendLine("    spec:");
        if (svc.NeedsServiceAccount)
        {
            sb.AppendLine($"      serviceAccountName: {svc.ServiceName}");
        }

        if (svc.ImagePullSecrets.Count > 0)
        {
            sb.AppendLine("      imagePullSecrets:");
            foreach (var name in svc.ImagePullSecrets)
            {
                sb.AppendLine($"        - name: {QuoteYaml(name)}");
            }
        }

        sb.AppendLine($"""
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

        if (svc.HasConfigMap || svc.WritesSecretManifest)
        {
            sb.AppendLine("          envFrom:");
            if (svc.HasConfigMap)
            {
                sb.AppendLine("            - configMapRef:");
                sb.AppendLine($"                name: {svc.ServiceName}-config");
            }

            if (svc.WritesSecretManifest)
            {
                sb.AppendLine("            - secretRef:");
                sb.AppendLine($"                name: {SecretName(svc)}");
            }
        }

        var env = new List<KubernetesEnvVar>();
        if (svc.UsesSecretRefs)
        {
            env.AddRange(KubernetesSecretEnv.Render(SecretName(svc), svc.SecretEnv, svc.Env.Keys));
        }

        if (svc.UsesVaultAgent)
        {
            // Lets the app find the file: builder.Configuration.AddJsonFile(
            //   Environment.GetEnvironmentVariable("DEPLOYIFY_SECRETS_FILE")!, optional: true, reloadOnChange: true)
            env.Add(new KubernetesEnvVar(SecretsFileVariable, VaultSecretsFilePath(svc)));
        }

        if (env.Count > 0)
        {
            sb.AppendLine("          env:");
            foreach (var e in env)
            {
                sb.AppendLine($"            - name: {e.Name}");
                if (e.IsSecretRef)
                {
                    sb.AppendLine("              valueFrom:");
                    sb.AppendLine("                secretKeyRef:");
                    sb.AppendLine($"                  name: {e.SecretName}");
                    sb.AppendLine($"                  key: {QuoteYaml(e.SecretKey)}");
                }
                else
                {
                    sb.AppendLine($"              value: {QuoteYaml(e.Value)}");
                }
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

        if (svc.UsesKeyVaultCsi)
        {
            // Mounting the volume is what makes the driver fetch the secrets and sync the
            // Secret the env vars read, so it's needed even though the app reads env vars.
            sb.AppendLine("          volumeMounts:");
            sb.AppendLine("            - name: keyvault-secrets");
            sb.AppendLine($"              mountPath: {KeyVaultMountPath}");
            sb.AppendLine("              readOnly: true");
            sb.AppendLine("      volumes:");
            sb.AppendLine("        - name: keyvault-secrets");
            sb.AppendLine("          csi:");
            sb.AppendLine("            driver: secrets-store.csi.k8s.io");
            sb.AppendLine("            readOnly: true");
            sb.AppendLine("            volumeAttributes:");
            sb.AppendLine($"              secretProviderClass: {SecretProviderClassName(svc)}");
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
    internal static async Task<string> WriteAsync(string dir, string filename, string content, CancellationToken ct)
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
