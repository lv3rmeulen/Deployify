using System.Collections.Generic;
using System.Text.Json.Serialization;
using Deployify.Core.Expressions;

namespace Deployify.Target.Host;

/// <summary>
/// One Aspire project as an operating-system service: what generate writes to service.json
/// and apply sends to each host.
/// </summary>
public sealed class HostService
{
    /// <summary>The Aspire resource name.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>The service's folder name under the root: prefix and resource name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The systemd unit (without .service) or Windows service name: <c>deployify-{Name}</c>.</summary>
    public string UnitName { get; set; } = string.Empty;

    /// <summary>The environment, recorded in the host-side marker so destroy only takes its own services.</summary>
    public string? Environment { get; set; }

    /// <summary>The .csproj to publish (absolute when it could be resolved).</summary>
    public string? ProjectPath { get; set; }

    public string ImageTag { get; set; } = "latest";

    /// <summary>The http ports the service listens on; the first is the one the health gate requests.</summary>
    public List<int> Ports { get; set; } = [];

    /// <summary>Environment variables without secrets.</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Variables holding a secret; their values never go into service.json.</summary>
    [JsonIgnore]
    public Dictionary<string, ResolvedValue> SecretEnv { get; set; } = [];

    /// <summary>Names of <see cref="SecretEnv"/>, so service.json shows what is set without the values.</summary>
    public List<string> SecretKeys { get; set; } = [];

    public List<string> DependsOn { get; set; } = [];

    public int KeepReleases { get; set; } = 3;
}
