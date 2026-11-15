using System;
using System.Collections.Generic;

namespace Deployify.Core.ResourceSources;

/// <summary>
/// One resource as the dashboard shows it: a deployed service, or one of its instances (a pod,
/// an allocation) with <see cref="Parent"/> pointing at the service.
/// </summary>
public sealed record ResourceSnapshot
{
    /// <summary>Unique among the source's resources; the dashboard's key for it (and for its logs and commands).</summary>
    public required string Name { get; init; }

    /// <summary>The kind of resource, e.g. "Deployment", "Pod", "NomadJob". Shown in the Type column.</summary>
    public required string ResourceType { get; init; }

    /// <summary>Shown instead of <see cref="Name"/>; defaults to it.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The state text, preferably one of <see cref="ResourceStates"/>, which the dashboard knows how to show.</summary>
    public string? State { get; init; }

    /// <summary>How the state is coloured.</summary>
    public ResourceStateStyle? StateStyle { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? StoppedAt { get; init; }

    /// <summary>The <see cref="Name"/> of the resource this one belongs to; the dashboard nests it under that one.</summary>
    public string? Parent { get; init; }

    public IReadOnlyList<ResourceUrl> Urls { get; init; } = [];

    public IReadOnlyList<ResourceEnvironmentVariable> Environment { get; init; } = [];

    public IReadOnlyList<ResourceProperty> Properties { get; init; } = [];

    /// <summary>Health checks. None means the dashboard derives health from the state alone.</summary>
    public IReadOnlyList<ResourceHealthReport> HealthReports { get; init; } = [];

    public IReadOnlyList<ResourceCommandInfo> Commands { get; init; } = [];
}

/// <summary>A URL the resource is reachable at.</summary>
/// <param name="EndpointName">The endpoint it belongs to (a port name), if any.</param>
/// <param name="Url">The full URL.</param>
/// <param name="IsInternal">Only reachable from inside the platform (a cluster-internal address).</param>
public sealed record ResourceUrl(string? EndpointName, string Url, bool IsInternal = false);

/// <param name="Value">Null when it isn't shown (it comes from a secret, or values are hidden).</param>
public sealed record ResourceEnvironmentVariable(string Name, string? Value);

/// <param name="Name">Key; the dashboard knows a few (see <see cref="ResourcePropertyNames"/>).</param>
/// <param name="Value">Shown as text.</param>
public sealed record ResourceProperty(string Name, string? Value, string? DisplayName = null, bool IsSensitive = false);

public sealed record ResourceHealthReport(string Key, ResourceHealthStatus Status, string? Description = null);

/// <summary>A command the dashboard offers on a resource.</summary>
public sealed record ResourceCommandInfo
{
    /// <summary>Passed back to <see cref="IResourceSource.ExecuteCommandAsync"/>. The dashboard puts <see cref="ResourceCommandNames"/> on its own buttons.</summary>
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>A Fluent UI icon name, e.g. "Play", "Stop", "ArrowCounterclockwise".</summary>
    public string? IconName { get; init; }

    /// <summary>Asked before the command runs.</summary>
    public string? ConfirmationMessage { get; init; }

    /// <summary>Shown as a button rather than in the menu.</summary>
    public bool IsHighlighted { get; init; }

    /// <summary>Shown but greyed out when false (e.g. "start" on something running).</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>One line of console output.</summary>
public readonly record struct ResourceLogLine(string Text, bool IsStdErr = false);

/// <summary>The outcome of a command.</summary>
public sealed record ResourceCommandResult(bool Success, string? Message = null)
{
    public static ResourceCommandResult Succeeded(string? message = null) => new(true, message);

    public static ResourceCommandResult Failed(string message) => new(false, message);
}

public enum ResourceStateStyle
{
    Success,
    Info,
    Warning,
    Error
}

public enum ResourceHealthStatus
{
    Healthy,
    Degraded,
    Unhealthy
}

/// <summary>State texts the Aspire dashboard knows (KnownResourceStates in Aspire.Hosting).</summary>
public static class ResourceStates
{
    public const string Starting = "Starting";
    public const string Running = "Running";
    public const string FailedToStart = "FailedToStart";
    public const string RuntimeUnhealthy = "RuntimeUnhealthy";
    public const string Stopping = "Stopping";
    public const string Exited = "Exited";
    public const string Finished = "Finished";
    public const string Waiting = "Waiting";
    public const string NotStarted = "NotStarted";
}

/// <summary>Command names the Aspire dashboard shows as its own start/stop/restart buttons (KnownResourceCommands).</summary>
public static class ResourceCommandNames
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Restart = "restart";

    public static ResourceCommandInfo StartCommand(bool enabled) => new()
    {
        Name = Start, DisplayName = "Start", IconName = "Play", IsHighlighted = true, Enabled = enabled
    };

    public static ResourceCommandInfo StopCommand(bool enabled, string? confirmation = null) => new()
    {
        Name = Stop, DisplayName = "Stop", IconName = "Stop", IsHighlighted = true, Enabled = enabled,
        ConfirmationMessage = confirmation
    };

    public static ResourceCommandInfo RestartCommand(bool enabled, string? description = null) => new()
    {
        Name = Restart, DisplayName = "Restart", IconName = "ArrowCounterclockwise", Enabled = enabled,
        Description = description
    };
}

/// <summary>Property names the dashboard shows in a known place (KnownProperties in Aspire.Hosting).</summary>
public static class ResourcePropertyNames
{
    public const string ContainerImage = "container.image";
    public const string ContainerId = "container.id";
}
