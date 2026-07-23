using Deployify.Core.Models;

namespace Deployify.Target.Nomad;

public sealed class NomadJob
{
    public string Name { get; set; } = string.Empty;
    public string? Image { get; set; }
    public string? ImageTag { get; set; }
    public int Count { get; set; } = 1;
    public DeploymentMode Mode { get; set; } = DeploymentMode.Docker;
    public Dictionary<string, string> Env { get; set; } = [];
    public List<NomadPort> Ports { get; set; } = [];
    public List<string> Dependencies { get; set; } = [];

    /// <summary>
    /// When true the task uses host networking (network_mode = "host") so ports
    /// are bound directly on all interfaces without NAT. Used for services that
    /// need to be reachable from outside the host, such as the Aspire Dashboard.
    /// </summary>
    public bool UseHostNetwork { get; set; }

    // ── Dapr ──────────────────────────────────────────────────────────────────
    public bool DaprEnabled { get; set; }
    public string? DaprAppId { get; set; }
    public int? DaprAppPort { get; set; }
    public int DaprHttpPort { get; set; } = 3500;
    public int DaprGrpcPort { get; set; } = 50001;
    public string DaprVersion { get; set; } = "latest";

    // ── Raw exec artifact ─────────────────────────────────────────────────────
    /// <summary>
    /// Download URL populated after <c>deployify package</c> uploads the zip.
    /// When set, the HCL emits an <c>artifact { source = "..." }</c> block.
    /// </summary>
    public string? ArtifactUrl { get; set; }

    /// <summary>
    /// The binary path inside the unzipped archive that raw_exec should invoke.
    /// Defaults to <c>local/{ServiceName}</c>.
    /// </summary>
    public string? ArtifactBinaryPath { get; set; }
}

public sealed class NomadPort
{
    public string Label { get; set; } = string.Empty;
    public int? Static { get; set; }
    public int? To { get; set; }

    /// <summary>
    /// When true the port is bound to all interfaces (0.0.0.0) so it is
    /// reachable from outside the host. When false Nomad binds to 127.0.0.1.
    /// </summary>
    public bool IsExternal { get; set; }
}
