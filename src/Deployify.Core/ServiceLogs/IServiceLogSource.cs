using System;
using System.Collections.Generic;
using System.Threading;
using Deployify.Core.Models;

namespace Deployify.Core.ServiceLogs;

/// <summary>
/// Optional capability of an <see cref="Interfaces.IDeploymentTarget"/>: read the console output
/// (stdout and stderr) of a service it deployed. <c>deployify logs</c> uses it, so reading a
/// service's logs works the same way whatever the service runs on.
/// </summary>
/// <remarks>
/// Ask for it with <c>target.GetCapability&lt;IServiceLogSource&gt;()</c>, not a type check: a
/// plugin target only supports it when its plugin declared the <c>logs</c> capability.
/// </remarks>
public interface IServiceLogSource
{
    /// <summary>
    /// The service's output: first the lines already written that <paramref name="query"/> asks
    /// for (oldest first), then, with <see cref="ServiceLogQuery.Follow"/>, new lines as they are
    /// written until <paramref name="ct"/> is cancelled. Lines of several instances may be
    /// interleaved. Throws <see cref="ServiceLogsException"/> for something the user should
    /// fix (an unknown service, an option the platform can't honor); ends without lines for a
    /// service that has written nothing.
    /// </summary>
    IAsyncEnumerable<ServiceLogLine> ReadLogsAsync(EnvironmentConfig config, ServiceLogQuery query, CancellationToken ct = default);
}

/// <summary>What to read. The same for every target; each maps it onto its platform.</summary>
public sealed record ServiceLogQuery
{
    /// <summary>Lines <see cref="Tail"/> defaults to.</summary>
    public const int DefaultTail = 100;

    /// <summary>The service, by its name in the AppHost.</summary>
    public required string Service { get; init; }

    /// <summary>
    /// How many of the most recent existing lines to start with, per instance; 0 for none (only
    /// new lines, with <see cref="Follow"/>). Null for everything the platform still keeps.
    /// </summary>
    public int? Tail { get; init; } = DefaultTail;

    /// <summary>Only lines written at or after this time.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Keep reading new lines until cancelled.</summary>
    public bool Follow { get; init; }
}

/// <summary>One line a service wrote.</summary>
public sealed record ServiceLogLine
{
    /// <summary>The line, without its line ending or any prefix the platform added.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// Which instance wrote it (a pod, an allocation, a container, a task, a replica), when the
    /// platform says. Null when it doesn't or the service has one instance only.
    /// </summary>
    public string? Instance { get; init; }

    /// <summary>When the platform received it, when the platform records that.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>The line went to stderr, when the platform keeps the two apart.</summary>
    public bool IsError { get; init; }
}

/// <summary>A problem reading logs that the user can do something about; the message says what.</summary>
public sealed class ServiceLogsException(string message, Exception? inner = null) : Exception(message, inner);
