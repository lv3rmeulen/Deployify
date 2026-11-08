using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Deployify.Core.SecretBackends;

/// <summary>Where secret parameter values are stored and how generated artifacts refer to them.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SecretBackendType>))]
public enum SecretBackendType
{
    /// <summary>
    /// The original behavior: each target writes secret values into its own artifacts
    /// (secret.yaml, .env) and generated values are kept in deployify-parameters.json.
    /// </summary>
    Inline,

    /// <summary>
    /// Kubernetes Secrets created out of band by <c>apply</c>. Generated manifests only hold
    /// <c>secretKeyRef</c> references; values never touch the output folder.
    /// </summary>
    KubernetesSecret,

    /// <summary>AWS SSM Parameter Store SecureString parameters referenced by ARN or name.</summary>
    Ssm,

    /// <summary>
    /// Azure Key Vault. On Azure Container Apps, app secrets are Key Vault references read
    /// with a managed identity; the app spec only holds the secret's URL.
    /// </summary>
    KeyVault,

    /// <summary>
    /// HashiCorp Vault KV v2. On Kubernetes, the Vault Agent Injector renders the secrets into
    /// the pod from a template; manifests only hold the template, never the values.
    /// </summary>
    Vault
}

/// <summary>
/// <c>secrets</c> section of an environment in deployify-state.json. Holds settings and
/// references (location and version) to secrets, never their values.
/// </summary>
public sealed class SecretsConfig
{
    public SecretBackendType Backend { get; set; } = SecretBackendType.Inline;

    /// <summary>Settings for <see cref="SecretBackendType.Vault"/>.</summary>
    public VaultConfig Vault { get; set; } = new();

    /// <summary>Settings for <see cref="SecretBackendType.KeyVault"/>.</summary>
    public KeyVaultConfig KeyVault { get; set; } = new();

    /// <summary>
    /// Where each secret was last written, keyed by <c>{service}/{key}</c>. Updated by apply.
    /// Holds locations and backend version numbers only.
    /// </summary>
    public Dictionary<string, SecretReference> References { get; set; } = new(StringComparer.Ordinal);

    /// <summary>True when secret values are kept out of generated artifacts and the parameters file.</summary>
    [JsonIgnore]
    public bool IsExternal => Backend != SecretBackendType.Inline;
}

/// <summary>Settings for the Vault backend.</summary>
public sealed class VaultConfig
{
    /// <summary>Vault address. Defaults to the <c>VAULT_ADDR</c> environment variable.</summary>
    public string? Address { get; set; }

    /// <summary>
    /// Token Deployify uses to write secrets (and policies, when <see cref="ManageAuth"/> is on).
    /// Use an <c>env:NAME</c> reference; defaults to <c>VAULT_TOKEN</c>. Pods never see it.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>Enterprise namespace, sent as <c>X-Vault-Namespace</c>.</summary>
    public string? Namespace { get; set; }

    /// <summary>Mount path of the KV version 2 engine.</summary>
    public string Mount { get; set; } = "secret";

    /// <summary>Path under the mount. Secrets go to <c>{PathPrefix}/{app}/{service}</c>.</summary>
    public string PathPrefix { get; set; } = "deployify";

    /// <summary>Mount path of the Kubernetes auth method that pods log in with.</summary>
    public string AuthMount { get; set; } = "kubernetes";

    /// <summary>
    /// Write a read-only policy and a Kubernetes auth role per service, so each pod can read
    /// only its own path. Needs a token allowed to manage policies and auth roles. When off,
    /// set up the roles yourself; the generated vault-policy.hcl files show what's needed.
    /// </summary>
    public bool ManageAuth { get; set; } = true;

    /// <summary>File the agent renders inside the pod, under <c>/vault/secrets</c>.</summary>
    public string SecretsFileName { get; set; } = "appsettings.secrets.json";

    /// <summary>Optional extra annotations for the agent, e.g. <c>vault.hashicorp.com/agent-pre-populate-only</c>.</summary>
    public Dictionary<string, string> AgentAnnotations { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Settings for the Azure Key Vault backend.</summary>
public sealed class KeyVaultConfig
{
    /// <summary>Key Vault name (the <c>name</c> in <c>https://name.vault.azure.net</c>). Required.</summary>
    public string? VaultName { get; set; }

    /// <summary>
    /// Vault URI, for clouds other than Azure public (e.g. <c>https://name.vault.azure.cn</c>).
    /// Defaults to <c>https://{VaultName}.vault.azure.net</c>.
    /// </summary>
    public string? VaultUri { get; set; }

    /// <summary>
    /// Resource ID of a user-assigned managed identity with the <c>Key Vault Secrets User</c>
    /// role on the vault, which apps use to read their secrets. Recommended: a system-assigned
    /// identity only exists once the app does, so it can't be granted access before the
    /// app's first revision tries to read its secrets.
    /// </summary>
    public string? IdentityResourceId { get; set; }

    /// <summary>
    /// Prefix for secret names, so several environments can share a vault. Defaults to
    /// <c>deployify-{environment name}</c> on Container Apps.
    /// </summary>
    public string? SecretPrefix { get; set; }

    /// <summary>Subscription of the vault, for Azure CLI calls. Container Apps uses its own subscriptionId.</summary>
    public string? SubscriptionId { get; set; }

    // ── Kubernetes (Secrets Store CSI driver, Azure provider) ─────────────────

    /// <summary>Entra ID tenant of the vault. Required on Kubernetes.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Kubernetes: client ID of the managed identity pods read secrets with. With
    /// <see cref="KeyVaultKubernetesIdentity.WorkloadIdentity"/> it needs a federated credential
    /// for each service's ServiceAccount; with <see cref="KeyVaultKubernetesIdentity.VmManagedIdentity"/>
    /// it is the identity of the AKS Key Vault add-on (or kubelet).
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>Kubernetes: how pods authenticate to the vault.</summary>
    public KeyVaultKubernetesIdentity KubernetesIdentity { get; set; } = KeyVaultKubernetesIdentity.WorkloadIdentity;

    /// <summary>Kubernetes: the provider's <c>cloudName</c>, e.g. <c>AzureChinaCloud</c>. Empty = public cloud.</summary>
    public string? CloudName { get; set; }

    /// <summary>The vault's base URI, without a trailing slash.</summary>
    public string ResolveVaultUri()
    {
        if (!string.IsNullOrWhiteSpace(VaultUri))
        {
            return VaultUri.TrimEnd('/');
        }

        if (string.IsNullOrWhiteSpace(VaultName))
        {
            throw new InvalidOperationException("Set secrets.keyVault.vaultName to use the KeyVault secret backend.");
        }

        return $"https://{VaultName}.vault.azure.net";
    }
}

/// <summary>How Kubernetes pods authenticate to Key Vault through the CSI driver.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KeyVaultKubernetesIdentity>))]
public enum KeyVaultKubernetesIdentity
{
    /// <summary>
    /// Microsoft Entra Workload ID: each service gets a ServiceAccount annotated with the client ID,
    /// and pods are labelled <c>azure.workload.identity/use</c>. Recommended.
    /// </summary>
    WorkloadIdentity,

    /// <summary>A user-assigned identity on the node pool, typically the AKS Key Vault add-on's identity.</summary>
    VmManagedIdentity
}

/// <summary>Where a secret lives in its backend. Never holds the value.</summary>
public sealed class SecretReference
{
    public SecretBackendType Backend { get; set; }

    /// <summary>Backend location, e.g. <c>ns/name</c>, an SSM parameter name or a Vault path.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>Key inside the location, when the backend stores several values per location.</summary>
    public string? Key { get; set; }

    /// <summary>Backend version: a Kubernetes resourceVersion, an SSM or a Vault KV version.</summary>
    public string? Version { get; set; }
}
