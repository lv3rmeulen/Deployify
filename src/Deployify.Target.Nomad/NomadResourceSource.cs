using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Deployify.Core.ResourceSources;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Nomad;

/// <summary>
/// The jobs Deployify deployed to Nomad, for the Aspire dashboard: every job with its
/// allocations, read from the HTTP API (two requests per poll, plus a job's spec when its
/// version changed). Logs come from the client fs API; start/stop register or stop the job,
/// restart restarts allocations in place.
/// </summary>
public sealed class NomadResourceSource : PollingResourceSource
{
    /// <summary>Roughly how much of a log a stream starts with: the API counts bytes, not lines.</summary>
    private const long BytesPerLogLine = 160;

    private readonly NomadClient _client;
    private readonly NomadTargetConfig _config;
    private readonly Dictionary<string, (long ModifyIndex, string Json)> _jobSpecs = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, NomadResourceLocation> _locations =
        new Dictionary<string, NomadResourceLocation>(StringComparer.Ordinal);

    internal NomadResourceSource(NomadClient client, NomadTargetConfig config, ResourceSourceOptions options, ILogger logger)
        : base(options, logger)
    {
        _client = client;
        _config = config;
    }

    public override string DisplayName => $"Nomad ({_config.Address}, namespace {_config.Namespace})";

    protected override async Task<IReadOnlyList<ResourceSnapshot>> GetResourcesAsync(CancellationToken ct)
    {
        var jobs = NomadResourceMapper.ManagedJobs(await _client.GetJobListJsonAsync(ct));
        var allocations = await _client.GetAllocationsJsonAsync(ct);

        // A job's spec only changes with its modify index: fetch it again only then.
        foreach (var job in jobs)
        {
            if ((!_jobSpecs.TryGetValue(job.Id, out var cached) || cached.ModifyIndex != job.JobModifyIndex) && await _client.GetJobJsonAsync(job.Id, ct) is { } json)
            {
                _jobSpecs[job.Id] = (job.JobModifyIndex, json);
            }
        }

        foreach (var gone in _jobSpecs.Keys.Except(jobs.Select(j => j.Id)).ToList())
        {
            _jobSpecs.Remove(gone);
        }

        var (resources, locations) = NomadResourceMapper.Map(
            jobs, _jobSpecs.ToDictionary(p => p.Key, p => p.Value.Json), allocations, _config.Namespace, Options);
        _locations = locations;
        return resources;
    }

    public override IAsyncEnumerable<ResourceLogLine> WatchLogsAsync(string resourceName, bool follow, CancellationToken ct)
    {
        if (!_locations.TryGetValue(resourceName, out var location) || location.ActiveAllocs.Count == 0)
        {
            return AsyncEnumerable.Empty<ResourceLogLine>();
        }

        // A job's logs are those of all its allocations, each line prefixed with the allocation.
        var prefix = location.AllocId is null && location.ActiveAllocs.Count > 1;
        var streams = location.ActiveAllocs.SelectMany(alloc => new[]
        {
            AllocationLogsAsync(alloc, location.Task, stderr: false, follow, prefix, ct),
            AllocationLogsAsync(alloc, location.Task, stderr: true, follow, prefix, ct)
        });
        return MergeAsync(streams, ct);
    }

    private async IAsyncEnumerable<ResourceLogLine> AllocationLogsAsync(
        string allocId, string task, bool stderr, bool follow, bool prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var tag = prefix ? $"[{allocId[..Math.Min(8, allocId.Length)]}] " : "";
        await foreach (var line in _client.StreamTaskLogsAsync(allocId, task, stderr, follow,
                           Options.LogTailLines * BytesPerLogLine, ct))
        {
            yield return new ResourceLogLine(tag + line, stderr);
        }
    }

    /// <summary>Interleaves several log streams as their lines arrive; a failing stream is logged and dropped.</summary>
    private async IAsyncEnumerable<ResourceLogLine> MergeAsync(
        IEnumerable<IAsyncEnumerable<ResourceLogLine>> streams, [EnumeratorCancellation] CancellationToken ct)
    {
        var merged = Channel.CreateUnbounded<ResourceLogLine>(new UnboundedChannelOptions { SingleReader = true });
        var pumps = streams.Select(async stream =>
        {
            try
            {
                await foreach (var line in stream.WithCancellation(ct))
                {
                    await merged.Writer.WriteAsync(line, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // ignore
            }
            catch (HttpRequestException ex)
            {
                Logger.LogDebug("Nomad log stream ended: {Error}", ex.Message);
            }
        }).ToList();
        _ = Task.WhenAll(pumps).ContinueWith(_ => merged.Writer.TryComplete(), TaskScheduler.Default);

        await foreach (var line in merged.Reader.ReadAllAsync(ct))
        {
            yield return line;
        }
    }

    public override async Task<ResourceCommandResult> ExecuteCommandAsync(string resourceName, string commandName, CancellationToken ct)
    {
        if (!_locations.TryGetValue(resourceName, out var location))
        {
            return ResourceCommandResult.Failed($"Unknown resource '{resourceName}'.");
        }

        try
        {
            if (location.AllocId is { } allocId)
            {
                switch (commandName)
                {
                    case ResourceCommandNames.Restart:
                        await _client.RestartAllocationAsync(allocId, ct);
                        return ResourceCommandResult.Succeeded($"Restarted allocation {resourceName}.");
                    case NomadResourceMapper.RescheduleCommand:
                        await _client.StopAllocationAsync(allocId, ct);
                        return ResourceCommandResult.Succeeded($"Stopped allocation {resourceName}; Nomad places a replacement.");
                    default:
                        return ResourceCommandResult.Failed($"Allocations have no '{commandName}' command.");
                }
            }

            switch (commandName)
            {
                case ResourceCommandNames.Stop:
                    await _client.StopJobAsync(location.JobId, purge: false, ct);
                    return ResourceCommandResult.Succeeded($"Stopped job {location.JobId}.");
                case ResourceCommandNames.Start:
                    await _client.StartJobAsync(location.JobId, ct);
                    return ResourceCommandResult.Succeeded($"Started job {location.JobId}.");
                case ResourceCommandNames.Restart:
                    foreach (var alloc in location.ActiveAllocs)
                    {
                        await _client.RestartAllocationAsync(alloc, ct);
                    }

                    return ResourceCommandResult.Succeeded(
                        $"Restarted {location.ActiveAllocs.Count} allocation(s) of {location.JobId}.");
                default:
                    return ResourceCommandResult.Failed($"Unknown command '{commandName}'.");
            }
        }
        catch (HttpRequestException ex)
        {
            return ResourceCommandResult.Failed(ex.Message);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await base.DisposeAsync();
    }
}
