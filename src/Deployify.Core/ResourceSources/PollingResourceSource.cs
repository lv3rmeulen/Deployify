using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Deployify.Core.ResourceSources;

/// <summary>
/// An <see cref="IResourceSource"/> that asks its platform for every resource at a fixed
/// interval. Derived classes only write <see cref="GetResourcesAsync"/>; a failed poll is logged
/// and skipped, so an unreachable platform never empties the dashboard.
/// </summary>
public abstract class PollingResourceSource(ResourceSourceOptions options, ILogger logger) : IResourceSource
{
    private readonly SemaphoreSlim _refresh = new(0, 1);

    protected ResourceSourceOptions Options { get; } = options;

    protected ILogger Logger { get; } = logger;

    public abstract string DisplayName { get; }

    /// <summary>Everything that exists right now. Throws when the platform can't be asked.</summary>
    protected abstract Task<IReadOnlyList<ResourceSnapshot>> GetResourcesAsync(CancellationToken ct);

    public abstract IAsyncEnumerable<ResourceLogLine> WatchLogsAsync(string resourceName, bool follow, CancellationToken ct);

    public abstract Task<ResourceCommandResult> ExecuteCommandAsync(string resourceName, string commandName, CancellationToken ct);

    public async IAsyncEnumerable<IReadOnlyList<ResourceSnapshot>> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var failing = false;
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<ResourceSnapshot>? resources = null;
            try
            {
                resources = await GetResourcesAsync(ct);
                if (failing)
                {
                    Logger.LogInformation("{Source}: reachable again.", DisplayName);
                    failing = false;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception ex)
            {
                // Logged once per outage, not on every poll.
                if (!failing)
                {
                    Logger.LogWarning("{Source}: could not read resources ({Error}); showing the last known state.",
                        DisplayName, ex.Message);
                    failing = true;
                }
            }

            if (resources is not null)
            {
                yield return resources;
            }

            try
            {
                await _refresh.WaitAsync(Options.PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    public void RequestRefresh()
    {
        try
        {
            _refresh.Release();
        }
        catch (SemaphoreFullException)
        {
            // A refresh is already pending.
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        _refresh.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
