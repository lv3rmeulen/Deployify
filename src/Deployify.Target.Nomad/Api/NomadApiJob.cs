using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Deployify.Target.Nomad.Api;

// The subset of Nomad's job API (https://developer.hashicorp.com/nomad/api-docs/json-jobs) that
// Deployify writes. Property names match the API; null properties are left out of the JSON.
// Durations are nanoseconds (see NomadDuration). Driver config keeps the driver's own
// snake_case keys, the same as in an HCL config block.

public sealed class NomadApiJob
{
    [JsonPropertyName("ID")] public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Namespace { get; init; }
    public string Type => "service";
    public required List<string> Datacenters { get; init; }
    public required Dictionary<string, string> Meta { get; init; }
    public required List<NomadTaskGroup> TaskGroups { get; init; }
}

public sealed class NomadTaskGroup
{
    public required string Name { get; init; }
    public required int Count { get; init; }
    public NomadUpdateStrategy? Update { get; init; }
    public List<NomadNetwork>? Networks { get; init; }
    public List<NomadService>? Services { get; init; }
    /// <summary>Null for an ingress gateway group, whose only task is the one Nomad injects.</summary>
    public List<NomadTask>? Tasks { get; init; }
}

public sealed class NomadUpdateStrategy
{
    public required long MinHealthyTime { get; init; }
    public required long HealthyDeadline { get; init; }
    public required long ProgressDeadline { get; init; }
    public required bool AutoRevert { get; init; }

    /// <summary>Allocations of a new version started next to the old ones; set for canary and blue-green.</summary>
    public int? Canary { get; init; }

    /// <summary>Promote canaries without being asked. Always false when <see cref="Canary"/> is set: Deployify promotes.</summary>
    public bool? AutoPromote { get; init; }
}

public sealed class NomadNetwork
{
    public required string Mode { get; init; }
    public List<NomadNetworkPort>? ReservedPorts { get; init; }
    public List<NomadNetworkPort>? DynamicPorts { get; init; }
}

/// <summary>A static (reserved) port has a Value; a dynamic one only maps To a container port.</summary>
public sealed class NomadNetworkPort
{
    public required string Label { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Value { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int To { get; init; }
}

public sealed class NomadService
{
    public required string Name { get; init; }
    public string? PortLabel { get; init; }
    public string? Provider { get; init; }
    public List<NomadServiceCheck>? Checks { get; init; }
    public NomadConsulConnect? Connect { get; init; }

    /// <summary>Tags of the service while its allocation is an unpromoted canary, so routers can leave it out.</summary>
    public List<string>? CanaryTags { get; init; }
}

public sealed class NomadServiceCheck
{
    public required string Type { get; init; }
    public string? Path { get; init; }
    public string? PortLabel { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Expose { get; init; }
    public required long Interval { get; init; }
    public required long Timeout { get; init; }
    public NomadCheckRestart? CheckRestart { get; init; }
}

public sealed class NomadCheckRestart
{
    public required int Limit { get; init; }
    public required long Grace { get; init; }
}

public sealed class NomadTask
{
    public required string Name { get; init; }
    public required string Driver { get; init; }
    public List<NomadTaskArtifact>? Artifacts { get; init; }
    public required Dictionary<string, object> Config { get; init; }
    public Dictionary<string, string>? Env { get; init; }
    public List<NomadTemplate>? Templates { get; init; }
    public required NomadResources Resources { get; init; }
    public NomadTaskLifecycle? Lifecycle { get; init; }
}

public sealed class NomadTaskArtifact
{
    public required string GetterSource { get; init; }
    public Dictionary<string, string>? GetterOptions { get; init; }
    public string? RelativeDest { get; init; }
}

public sealed class NomadTemplate
{
    public required string EmbeddedTmpl { get; init; }
    public required string DestPath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Envvars { get; init; }
    public string? ChangeMode { get; init; }
}

public sealed class NomadResources
{
    [JsonPropertyName("CPU")] public required int Cpu { get; init; }
    public required int MemoryMB { get; init; }
}

public sealed class NomadTaskLifecycle
{
    public required string Hook { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Sidecar { get; init; }
}

// ── Consul Connect ────────────────────────────────────────────────────────────

public sealed class NomadConsulConnect
{
    public NomadSidecarService? SidecarService { get; init; }
    public NomadConsulGateway? Gateway { get; init; }
}

public sealed class NomadSidecarService
{
    public NomadConsulProxy? Proxy { get; init; }
}

public sealed class NomadConsulProxy
{
    public List<NomadConsulUpstream>? Upstreams { get; init; }
}

public sealed class NomadConsulUpstream
{
    public required string DestinationName { get; init; }
    public required int LocalBindPort { get; init; }
}

public sealed class NomadConsulGateway
{
    public required NomadConsulGatewayProxy Proxy { get; init; }
    public required NomadConsulIngressConfig Ingress { get; init; }
}

/// <summary>Gateway proxy settings; Deployify uses Consul's defaults, so this is empty.</summary>
public sealed class NomadConsulGatewayProxy;

public sealed class NomadConsulIngressConfig
{
    public required List<NomadConsulIngressListener> Listeners { get; init; }
}

public sealed class NomadConsulIngressListener
{
    public required int Port { get; init; }
    public required string Protocol { get; init; }
    public required List<NomadConsulIngressService> Services { get; init; }
}

public sealed class NomadConsulIngressService
{
    public required string Name { get; init; }
}
