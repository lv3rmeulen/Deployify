using System.Collections.Generic;
using System.Text.Json;

namespace Deployify.Plugin.Contracts;

// ── Session ──────────────────────────────────────────────────────────────────

/// <summary>Sent first. The plugin answers with what it serves.</summary>
public sealed record InitializeRequest
{
    /// <summary>The protocol version Deployify speaks, e.g. "1.0".</summary>
    public required string ProtocolVersion { get; init; }

    /// <summary>Deployify's own version, for messages.</summary>
    public string? HostVersion { get; init; }
}

public sealed record InitializeResult
{
    /// <summary>The protocol version the plugin speaks. The major version must match Deployify's.</summary>
    public required string ProtocolVersion { get; init; }

    public required PluginInfo Plugin { get; init; }

    /// <summary>The targets this plugin serves; at least one.</summary>
    public required IReadOnlyList<PluginTargetInfo> Targets { get; init; }
}

public sealed record PluginInfo
{
    public required string Name { get; init; }

    public string? Version { get; init; }
}

public sealed record PluginTargetInfo
{
    /// <summary>The ID users put in config: <c>"Target": "example"</c>. Letters, digits, '-', '_', '.'.</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Values from <see cref="PluginProtocol.Capabilities"/>.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>
    /// JSON Schema for the target's section of the environment's <c>TargetConfig</c>, keyed by
    /// <see cref="ConfigKey"/>. Deployify checks the section against it before calling the plugin.
    /// </summary>
    public JsonElement? ConfigSchema { get; init; }

    /// <summary>The <c>TargetConfig</c> key the target reads. Defaults to <see cref="Id"/>.</summary>
    public string? ConfigKey { get; init; }
}

// ── Requests ─────────────────────────────────────────────────────────────────

/// <summary>
/// The context every target call carries. <see cref="Manifest"/> is the Aspire manifest as
/// Aspire writes it (<c>manifest.json</c>). <see cref="Environment"/> is the environment's entry
/// in <c>deployify-state.json</c>, with explicit parameter values removed: a plugin asks for
/// the parameter values it needs through <see cref="PluginProtocol.Methods.ResolveParameter"/>.
/// </summary>
public record TargetRequest
{
    public required string TargetId { get; init; }

    public required JsonElement Environment { get; init; }

    public JsonElement? Manifest { get; init; }

    /// <summary>Where generated artifacts are read from and written to.</summary>
    public string? OutputDirectory { get; init; }
}

/// <summary><c>target/publish</c>.</summary>
public sealed record PublishRequest : TargetRequest
{
    public required string AppHostDirectory { get; init; }
}

/// <summary>A rollout or lifecycle call about one service.</summary>
public sealed record ServiceRequest : TargetRequest
{
    public required string Service { get; init; }

    /// <summary>The revision to observe (<c>rollout/observe</c>).</summary>
    public string? Revision { get; init; }
}

/// <summary><c>rollout/restore</c>.</summary>
public sealed record RestoreRequest : TargetRequest
{
    public required ServiceSnapshotInfo Snapshot { get; init; }
}

/// <summary><c>lifecycle/deleteService</c>, <c>lifecycle/destroyEnvironment</c>, <c>lifecycle/describeKept</c>.</summary>
public sealed record DeleteRequest : TargetRequest
{
    public string? Service { get; init; }

    public bool KeepSecrets { get; init; }

    public bool RemoveVolumes { get; init; }
}

// ── Results ──────────────────────────────────────────────────────────────────

public sealed record PingResult(bool Ok, string? Error = null);

public sealed record GenerateResult(IReadOnlyList<string> Files);

public sealed record ArtifactValidation(string ArtifactName, IReadOnlyList<string> Errors);

public sealed record PlanInfo
{
    public IReadOnlyList<string> ToCreate { get; init; } = [];
    public IReadOnlyList<string> ToUpdate { get; init; } = [];
    public IReadOnlyList<string> ToDelete { get; init; } = [];
    public IReadOnlyList<string> Unchanged { get; init; } = [];
    public IReadOnlyList<PlanUnknownInfo> Unknown { get; init; } = [];
}

public sealed record PlanUnknownInfo(string ServiceName, string Reason);

public sealed record ServiceDeploymentInfo
{
    public required string ServiceName { get; init; }
    public string? DeploymentId { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}

public sealed record ServiceExplanationInfo
{
    public required string Name { get; init; }
    public string? Image { get; init; }
    public string? ImageTag { get; init; }
    public int Count { get; init; } = 1;
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Ports { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyDictionary<string, string> AdditionalInfo { get; init; } = new Dictionary<string, string>();
}

public sealed record ServiceDependencyInfo(string From, string To);

public enum DiffKindInfo
{
    Added,
    Removed,
    Changed,
    Unchanged,
    Unknown
}

public sealed record DiffEntryInfo
{
    public required string ServiceName { get; init; }
    public DiffKindInfo Kind { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    public string? Field { get; init; }
    public string? Reason { get; init; }
}

/// <summary>What ran before a rollout touched a service. Must never hold a secret value.</summary>
public sealed record ServiceSnapshotInfo
{
    public required string ServiceName { get; init; }
    public bool Existed { get; init; }
    public string? Revision { get; init; }
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();
}

public sealed record AppliedRevisionInfo
{
    public required string ServiceName { get; init; }
    public bool Success { get; init; }
    public string? Revision { get; init; }
    public string? Error { get; init; }
}

public enum HealthStateInfo
{
    Progressing,
    Healthy,
    Failed,
    RevertedByPlatform
}

public sealed record HealthObservationInfo(HealthStateInfo State, string? Detail = null, string? Revision = null);

public sealed record RestoreResult(string? Revision);

public sealed record BoolResult(bool Value);

public sealed record ReasonResult(string? Reason);

public sealed record ManagedServiceInfo(string Name, string? TargetId = null, string? Environment = null);

public sealed record LinesResult(IReadOnlyList<string> Lines);

// ── Calls from the plugin ────────────────────────────────────────────────────

/// <summary>
/// Asks Deployify for one input of a <c>parameter.v0</c> resource in the manifest of the current
/// call. Deployify answers only for parameters the manifest declares, and records secret values
/// so they are kept out of logs and state.
/// </summary>
public sealed record ParameterValueRequest
{
    public required string Parameter { get; init; }

    /// <summary>The input name; <c>value</c> for a plain parameter.</summary>
    public string Input { get; init; } = "value";
}

/// <summary>The value, or null when no source supplied one.</summary>
public sealed record ParameterValueResult(string? Value);

public enum LogLevelInfo
{
    Trace,
    Debug,
    Information,
    Warning,
    Error
}

public sealed record LogMessage(LogLevelInfo Level, string Message, string? Category = null);
