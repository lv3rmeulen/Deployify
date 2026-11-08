using System.Text.Json.Serialization;

namespace Deployify.Core.State;

/// <summary>Where an environment's deployment state (inventory, generated parameters, rollout journals, lock) is kept.</summary>
public enum StateBackendType
{
    /// <summary>Files under <c>.deployify/</c> next to deployify-state.json. One machine only.</summary>
    Local,

    /// <summary>An Azure Storage blob container. Locks and writes use blob ETags.</summary>
    AzureBlob,

    /// <summary>An S3 bucket. Locks and writes use S3 conditional writes (If-Match / If-None-Match).</summary>
    S3,

    /// <summary>Consul KV. Locks and writes use check-and-set on the modify index.</summary>
    Consul,

    /// <summary>Kubernetes Secrets in one namespace. Locks and writes use resourceVersion.</summary>
    Kubernetes
}

/// <summary>
/// The <c>state</c> section of an environment. It lives in deployify-state.json (which is
/// committed) because the location of remote state can't itself be stored in remote state.
/// Leave it out to keep everything local, as before.
/// </summary>
public sealed class StateBackendConfig
{
    public StateBackendType Backend { get; set; } = StateBackendType.Local;

    /// <summary>
    /// Key prefix inside the backend. Every environment's keys go under <c>{prefix}/{environment}/</c>,
    /// so several apps or environments can share one bucket, container, Consul or namespace.
    /// </summary>
    public string Prefix { get; set; } = "deployify";

    /// <summary>
    /// How long a lock lasts without being renewed. The holder renews it every third of this,
    /// so a crashed run frees its lock after at most this long. Machines that share a lock need
    /// clocks within a fraction of it of each other (NTP is plenty).
    /// </summary>
    public int LockTtlSeconds { get; set; } = 180;

    /// <summary>How long to wait for a lock someone else holds before giving up. 0 fails at once.</summary>
    public int LockWaitSeconds { get; set; }

    public AzureBlobStateConfig? AzureBlob { get; set; }
    public S3StateConfig? S3 { get; set; }
    public ConsulStateConfig? Consul { get; set; }
    public KubernetesStateConfig? Kubernetes { get; set; }

    [JsonIgnore]
    public bool IsRemote => Backend != StateBackendType.Local;
}

public sealed class AzureBlobStateConfig
{
    /// <summary>Storage account name, e.g. <c>mycompanydeploy</c>.</summary>
    public string AccountName { get; set; } = string.Empty;

    public string Container { get; set; } = "deployify-state";

    /// <summary>Blob endpoint, for sovereign clouds or Azurite. Defaults to <c>https://{account}.blob.core.windows.net</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// A SAS token (use <c>env:NAME</c>). Without one, a token for the signed-in Azure CLI user
    /// is used (<c>az account get-access-token</c>), which needs the Storage Blob Data Contributor role.
    /// Falls back to DEPLOYIFY_STATE_SAS_TOKEN.
    /// </summary>
    public string? SasToken { get; set; }
}

public sealed class S3StateConfig
{
    public string Bucket { get; set; } = string.Empty;

    /// <summary>Bucket region. Defaults to the AWS CLI's configured region.</summary>
    public string? Region { get; set; }

    /// <summary>AWS CLI profile. Defaults to the standard credential chain.</summary>
    public string? Profile { get; set; }
}

public sealed class ConsulStateConfig
{
    public string Address { get; set; } = "http://localhost:8500";

    /// <summary>ACL token (use <c>env:NAME</c>). Falls back to CONSUL_HTTP_TOKEN.</summary>
    public string? Token { get; set; }
}

public sealed class KubernetesStateConfig
{
    /// <summary>Namespace the state Secrets are kept in. It must exist.</summary>
    public string Namespace { get; set; } = "deployify-state";

    public string? Context { get; set; }
    public string? KubeConfigPath { get; set; }
}
