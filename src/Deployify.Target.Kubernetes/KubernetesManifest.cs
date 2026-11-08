using System;
using System.Collections.Generic;
using System.Linq;
using Deployify.Core.Expressions;
using Deployify.Core.SecretBackends.Vault;
using Deployify.Core.SecretBackends;
using Deployify.Core.SecretBackends.KeyVault;

namespace Deployify.Target.Kubernetes;

/// <summary>
/// Represents all Kubernetes resources generated for a single Aspire service.
/// Each service gets its own namespace.
/// </summary>
public sealed class KubernetesServiceManifest
{
    public string ServiceName { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string? Image { get; set; }
    public string ImageTag { get; set; } = "latest";
    public int Replicas { get; set; } = 1;
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>
    /// Env vars whose value contains a secret parameter. They are kept out of <see cref="Env"/>
    /// and written the target's own secret way rather than into plain-text artifacts.
    /// </summary>
    public Dictionary<string, ResolvedValue> SecretEnv { get; set; } = [];
    public List<KubernetesPort> Ports { get; set; } = [];
    public List<string> Dependencies { get; set; } = [];

    // Resource requests/limits
    public string CpuRequest { get; set; } = "100m";
    public string CpuLimit { get; set; } = "500m";
    public string MemoryRequest { get; set; } = "128Mi";
    public string MemoryLimit { get; set; } = "512Mi";

    // Which optional resources to generate
    public bool HasConfigMap => Env.Count > 0;
    public bool HasSecret => SecretEnv.Count > 0;

    // ── Secrets ───────────────────────────────────────────────────────────────
    /// <summary>Where secret values are kept (secrets.backend).</summary>
    public SecretBackendType SecretBackend { get; set; } = SecretBackendType.Inline;

    /// <summary>True for project resources, which can read a configuration file rendered by the Vault agent.</summary>
    public bool IsProject { get; set; }

    /// <summary>Inline backend: secret values are written to secret.yaml.</summary>
    public bool WritesSecretManifest => HasSecret && SecretBackend == SecretBackendType.Inline;

    /// <summary>
    /// Values come from a Secret applied out of band and referenced with <c>secretKeyRef</c>.
    /// Also used for containers under the Vault backend, since they can't read the agent's
    /// .NET configuration file.
    /// </summary>
    public bool UsesSecretRefs => HasSecret &&
        (SecretBackend is SecretBackendType.KubernetesSecret or SecretBackendType.KeyVault ||
         (SecretBackend == SecretBackendType.Vault && !IsProject));

    /// <summary>
    /// Key Vault: the Secrets Store CSI driver mounts the service's secrets and syncs them into
    /// the <c>{service}-secrets</c> Secret that the env vars reference.
    /// </summary>
    public bool UsesKeyVaultCsi => HasSecret && SecretBackend == SecretBackendType.KeyVault;

    /// <summary>The service needs a ServiceAccount of its own (Vault agent or workload identity).</summary>
    public bool NeedsServiceAccount => UsesVaultAgent ||
        (UsesKeyVaultCsi && KeyVault?.KubernetesIdentity == KeyVaultKubernetesIdentity.WorkloadIdentity);

    /// <summary>Key Vault: settings from secrets.keyVault.</summary>
    public KeyVaultConfig? KeyVault { get; set; }

    /// <summary>Key Vault: name prefix, <c>secretPrefix</c> or <c>deployify-{namespacePrefix}</c>.</summary>
    public string? KeyVaultPrefix { get; set; }

    /// <summary>Key Vault: the vault secret holding each secret parameter, keyed by parameter id.</summary>
    public IReadOnlyDictionary<string, string> KeyVaultObjectNames => KeyVaultPrefix is null
        ? new Dictionary<string, string>()
        : SecretKeys.SecretParameters(SecretEnv.Values).Select(SecretKeys.Id).Distinct()
            .ToDictionary(id => id, id => KeyVaultSecrets.SecretName(KeyVaultPrefix, ServiceName, id), StringComparer.Ordinal);

    /// <summary>Values are rendered into the pod by the Vault Agent Injector.</summary>
    public bool UsesVaultAgent => HasSecret && SecretBackend == SecretBackendType.Vault && IsProject;

    /// <summary>The secret parameter values this service uses, keyed by id. Kept in memory only.</summary>
    public IReadOnlyDictionary<string, SensitiveValue> SecretData => SecretKeys.SecretValues(SecretEnv.Values);

    /// <summary>Vault: KV mount.</summary>
    public string VaultMount { get; set; } = "secret";

    /// <summary>Vault: path of this service's entry under the mount.</summary>
    public string? VaultPath { get; set; }

    /// <summary>Vault: API path used in templates and policies, e.g. <c>secret/data/deployify/shop/api</c>.</summary>
    public string? VaultKvApiPath => VaultPath is null ? null : VaultClient.KvApiPath(VaultMount, VaultPath);

    /// <summary>Vault: name of the Kubernetes auth role and the policy.</summary>
    public string? VaultRole { get; set; }

    /// <summary>Vault: file the agent renders under /vault/secrets.</summary>
    public string VaultSecretsFileName { get; set; } = "appsettings.secrets.json";

    /// <summary>Vault: extra agent annotations from secrets.vault.agentAnnotations.</summary>
    public Dictionary<string, string> VaultAgentAnnotations { get; set; } = [];
    public bool HasService => Ports.Count > 0;
    public bool HasIngress { get; set; }
    public string? IngressHost { get; set; }
    public string IngressClassName { get; set; } = "nginx";
    public bool HasPvc { get; set; }
    public string? PvcStorageSize { get; set; }

    /// <summary>Paths of all generated YAML files for this service.</summary>
    public List<string> GeneratedFiles { get; set; } = [];

    // ── Dapr ──────────────────────────────────────────────────────────────────
    public bool DaprEnabled { get; set; }
    public string? DaprAppId { get; set; }
    public int? DaprAppPort { get; set; }
    public int DaprHttpPort { get; set; } = 3500;
    public int DaprGrpcPort { get; set; } = 50001;

    // ── Health probes ─────────────────────────────────────────────────────────
    /// <summary>"tcp" (default), "http" or "none". See KubernetesTargetConfig.HealthCheckType.</summary>
    public string HealthCheckType { get; set; } = "tcp";

    public string HealthCheckPath { get; set; } = "/health";
}

public sealed class KubernetesPort
{
    public string Name { get; set; } = string.Empty;
    public int ContainerPort { get; set; }
    public string Protocol { get; set; } = "TCP";

    /// <summary>
    /// True when the port serves HTTP. Set from the Aspire binding's scheme; when not set
    /// explicitly, falls back to guessing from the port name and number.
    /// </summary>
    public bool IsHttp
    {
        get => _isHttp ?? (Name.Contains("http", StringComparison.OrdinalIgnoreCase)
                           || ContainerPort is 80 or 443 or 8080 or 8443);
        set => _isHttp = value;
    }

    private bool? _isHttp;
}
