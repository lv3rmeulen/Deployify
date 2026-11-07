using System.Collections.Generic;
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

    /// <summary>Container arguments from the manifest (placeholders already resolved).</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>True for Aspire project resources (built by deployify publish).</summary>
    public bool IsProject { get; set; }

    /// <summary>
    /// When true the task uses host networking (network_mode = "host"): ports are bound
    /// directly on the host and services reach each other on localhost.
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

    /// <summary>Aspire marked the binding external (meant to be reached from outside).</summary>
    public bool IsExternal { get; set; }

    /// <summary>The binding carries HTTP traffic (decides the health check type).</summary>
    public bool IsHttp { get; set; }
}
