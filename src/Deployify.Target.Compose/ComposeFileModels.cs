using System.Collections.Generic;
using Deployify.Yaml;

namespace Deployify.Target.Compose;

// The parts of the Compose file format (https://docs.docker.com/reference/compose-file/) that
// Deployify writes. Property names become snake_case keys; null properties are left out. Values
// that come from the user or the app are Quoted.

internal sealed class ComposeFileServices
{
    public required Dictionary<string, ComposeServiceDefinition> Services { get; init; }
}

internal sealed class ComposeFileVolumes
{
    /// <summary>A volume with no settings is null (<c>data:</c>), which Compose reads as the defaults.</summary>
    public required Dictionary<string, ComposeVolumeDefinition?> Volumes { get; init; }
}

internal sealed class ComposeFileNetworks
{
    public required Dictionary<string, ComposeNetworkDefinition> Networks { get; init; }
}

internal sealed class ComposeServiceDefinition
{
    public required Quoted Image { get; init; }
    public List<string>? Command { get; init; }
    public required string Restart { get; init; }
    public List<string>? Profiles { get; init; }
    public List<Quoted>? Ports { get; init; }
    public List<string>? EnvFile { get; init; }
    public Dictionary<string, Quoted>? Environment { get; init; }
    public List<string>? Volumes { get; init; }
    public string? NetworkMode { get; init; }
    public List<string>? Networks { get; init; }

    /// <summary>
    /// The short form (a list of service names) or the long form (service name →
    /// <see cref="ComposeDependency"/>).
    /// </summary>
    public object? DependsOn { get; init; }
}

internal sealed class ComposeDependency
{
    public required string Condition { get; init; }
}

internal sealed class ComposeVolumeDefinition
{
    public required string Driver { get; init; }
}

internal sealed class ComposeNetworkDefinition
{
    public bool? External { get; init; }
    public string? Driver { get; init; }
}
