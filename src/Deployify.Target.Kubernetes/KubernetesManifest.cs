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
    public List<KubernetesPort> Ports { get; set; } = [];
    public List<string> Dependencies { get; set; } = [];

    // Resource requests/limits
    public string CpuRequest { get; set; } = "100m";
    public string CpuLimit { get; set; } = "500m";
    public string MemoryRequest { get; set; } = "128Mi";
    public string MemoryLimit { get; set; } = "512Mi";

    // Which optional resources to generate
    public bool HasConfigMap => Env.Count > 0;
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
}

public sealed class KubernetesPort
{
    public string Name { get; set; } = string.Empty;
    public int ContainerPort { get; set; }
    public string Protocol { get; set; } = "TCP";
    public bool IsHttp => Name.Contains("http", StringComparison.OrdinalIgnoreCase) || ContainerPort is 80 or 443 or 8080 or 8443;
}
