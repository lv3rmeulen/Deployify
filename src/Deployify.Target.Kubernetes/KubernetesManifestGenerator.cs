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
using Deployify.Target.Kubernetes.Manifests;

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
                Image = ServiceImage.For(name, resource, config)
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
    /// and the GitOps tree use the same models, so kubectl, Helm and GitOps deploy identical objects.
    /// </summary>
    internal static IReadOnlyList<(string FileName, string Content)> Render(KubernetesServiceManifest svc)
        => [.. KubernetesManifestBuilder.Build(svc).Select(f => (f.FileName, KubernetesManifestBuilder.ToText(f.Model)))];

    /// <summary>Makes a file that holds secret values readable and writable by its owner only.</summary>
    internal static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    internal static string SecretName(KubernetesServiceManifest svc) => $"{svc.ServiceName}-secrets";

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a file with LF line endings, so the output is identical however the repository
    /// is checked out (other writers build their text from raw string literals).
    /// </summary>
    internal static async Task<string> WriteAsync(string dir, string filename, string content, CancellationToken ct)
    {
        var path = Path.Combine(dir, filename);
        await File.WriteAllTextAsync(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(false), ct);
        return path;
    }

    /// <summary>
    /// Always emits a double-quoted YAML scalar, for the writers that still build YAML as text
    /// (Helm values, GitOps registrations). Unquoted values such as <c>true</c>, <c>8080</c> or an
    /// empty string would be typed as bool/int/null. A JSON string literal is a valid YAML
    /// double-quoted scalar.
    /// </summary>
    internal static string QuoteYaml(string? value)
        => System.Text.Json.JsonSerializer.Serialize(value ?? string.Empty);
}
