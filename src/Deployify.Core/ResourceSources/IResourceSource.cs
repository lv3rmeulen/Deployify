using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging;

namespace Deployify.Core.ResourceSources;

/// <summary>
/// Where the resource server gets what it shows in the Aspire dashboard: the resources deployed
/// to one environment, their console logs, and the commands that can be run on them. Each target
/// that can be watched implements one; the server (Deployify.ResourceServer) only talks to this
/// interface, so it doesn't know or care whether the resources run on Kubernetes, Nomad or
/// anything else.
/// </summary>
/// <remarks>
/// Implementations deal in plain records (<see cref="ResourceSnapshot"/>), not the dashboard's
/// gRPC types, so a target or plugin doesn't need gRPC or Aspire to provide one. Most sources
/// poll their platform; derive from <see cref="PollingResourceSource"/> and only write
/// <see cref="PollingResourceSource.GetResourcesAsync"/>.
/// </remarks>
public interface IResourceSource : IAsyncDisposable
{
    /// <summary>Shown in the dashboard's title and the CLI, e.g. "Kubernetes (prod-cluster)".</summary>
    string DisplayName { get; }

    /// <summary>
    /// The resources, as a sequence of complete sets: every item is everything that exists right
    /// now, not a change. The server compares consecutive sets to find what was added, changed or
    /// removed. Yield a new set whenever something may have changed; yielding an identical set
    /// is harmless. Don't yield an empty set because the platform couldn't be reached: that reads
    /// as "everything was deleted". Ends when <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<ResourceSnapshot>> WatchAsync(CancellationToken ct);

    /// <summary>
    /// Console output of one resource (by <see cref="ResourceSnapshot.Name"/>): recent lines first,
    /// then, with <paramref name="follow"/>, new lines as they're written until
    /// <paramref name="ct"/> is cancelled. Ends without lines for a resource that has none.
    /// </summary>
    IAsyncEnumerable<ResourceLogLine> WatchLogsAsync(string resourceName, bool follow, CancellationToken ct);

    /// <summary>
    /// Runs one of the commands the resource lists in <see cref="ResourceSnapshot.Commands"/>.
    /// Failures the user should see are returned as <see cref="ResourceCommandResult.Failed"/>,
    /// not thrown.
    /// </summary>
    Task<ResourceCommandResult> ExecuteCommandAsync(string resourceName, string commandName, CancellationToken ct);

    /// <summary>
    /// Asks for a new set as soon as possible instead of at the next poll, e.g. right after a
    /// command changed something. Optional.
    /// </summary>
    void RequestRefresh()
    {
    }
}

/// <summary>
/// Optional capability of an <see cref="Interfaces.IDeploymentTarget"/>: watch what it deployed.
/// <c>deployify dashboard</c> uses it to serve the environment's resources to the Aspire dashboard.
/// </summary>
public interface IResourceSourceProvider
{
    /// <summary>A source for the environment's resources; the caller disposes it.</summary>
    IResourceSource CreateResourceSource(EnvironmentConfig config, ResourceSourceOptions options, ILoggerFactory loggers);
}

/// <summary>How a resource source should behave; the same for every target.</summary>
public sealed class ResourceSourceOptions
{
    /// <summary>How often a polling source asks its platform. Default: 3 seconds.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How many existing lines a log stream starts with. Default: 1000.</summary>
    public int LogTailLines { get; init; } = 1000;

    /// <summary>
    /// Show environment variable values. Values that come from secrets are never shown, whatever
    /// this says. Default: true.
    /// </summary>
    public bool ShowEnvironmentValues { get; init; } = true;
}
