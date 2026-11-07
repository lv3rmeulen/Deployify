using System.Collections.Generic;

namespace Deployify.Target.Compose;

/// <summary>
/// Internal model representing a single Docker Compose service,
/// derived from an Aspire resource.
/// </summary>
public sealed class ComposeService
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Full image reference, e.g. registry.example.com/myapp/api</summary>
    public string? Image { get; set; }

    public string ImageTag { get; set; } = "latest";

    public int Replicas { get; set; } = 1;

    public Dictionary<string, string> Env { get; set; } = [];

    public List<ComposePort> Ports { get; set; } = [];

    public List<string> DependsOn { get; set; } = [];

    public List<string> NamedVolumes { get; set; } = [];

    public List<string> BindMounts { get; set; } = [];

    public List<string> Profiles { get; set; } = [];

    public string RestartPolicy { get; set; } = "unless-stopped";

    public bool IsProjectService { get; set; }

    /// <summary>Path of the generated per-service compose file.</summary>
    public string? ComposeFilePath { get; set; }

    /// <summary>Path of the generated .env file (if applicable).</summary>
    public string? EnvFilePath { get; set; }

    // ── Dapr ──────────────────────────────────────────────────────────────────
    public bool DaprEnabled { get; set; }
    public string? DaprAppId { get; set; }
    public int? DaprAppPort { get; set; }
    public int DaprHttpPort { get; set; } = 3500;
    public int DaprGrpcPort { get; set; } = 50001;
    public string DaprVersion { get; set; } = "latest";
}

public sealed class ComposePort
{
    public int HostPort { get; set; }
    public int ContainerPort { get; set; }
    public string Protocol { get; set; } = "tcp";

    /// <summary>
    /// True when the port serves HTTP. Set from the Aspire binding's scheme; when not set
    /// explicitly, falls back to guessing from the port number.
    /// </summary>
    public bool IsHttp
    {
        get => _isHttp ?? ContainerPort is 80 or 443 or 8080 or 8443;
        set => _isHttp = value;
    }

    private bool? _isHttp;
}
