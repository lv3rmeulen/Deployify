using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.ServiceLogs;

namespace Deployify.Target.Nomad;

/// <summary>
/// A service's logs on Nomad: the stdout and stderr of the job's main task (named like the job),
/// in each of its running allocations, read through the client filesystem API. Shared by the
/// Nomad and Nomad + Consul targets.
/// </summary>
/// <remarks>
/// Nomad stores the raw output without times, so lines carry no timestamp and <c>--since</c>
/// can't be honoured. The tail is read from the end of each log file, then following starts at
/// the end, so a line written in the moment between the two can be missed.
/// </remarks>
public static class NomadLogs
{
    /// <summary>Bytes read from the end of a log per line asked for; lines are rarely longer.</summary>
    private const int BytesPerLine = 512;

    private const long MaxTailBytes = 8 * 1024 * 1024;

    public static async IAsyncEnumerable<ServiceLogLine> ReadAsync(
        NomadClient client, string jobId, ServiceLogQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (query.Since is not null)
        {
            throw new ServiceLogsException(
                "Nomad doesn't record when each log line was written, so --since can't be used on Nomad. Use --tail instead.");
        }

        if (await client.GetJobAsync(jobId, ct) is null)
        {
            throw new ServiceLogsException($"Nomad has no job '{jobId}' in this namespace. Is it deployed to this environment?");
        }

        var allocations = PickAllocations(await client.GetJobAllocationsJsonAsync(jobId, ct), jobId);
        if (allocations.Count == 0)
        {
            yield break;
        }

        if (query.Tail != 0)
        {
            foreach (var alloc in allocations)
            {
                foreach (var stderr in new[] { false, true })
                {
                    foreach (var text in await ReadTailAsync(client, alloc, stderr, query.Tail, ct))
                    {
                        yield return Line(alloc, text, stderr);
                    }
                }
            }
        }

        if (!query.Follow)
        {
            yield break;
        }

        var live = allocations
            .SelectMany(alloc => new[] { false, true }.Select(stderr => FollowAsync(client, alloc, stderr, ct)))
            .ToList();
        await foreach (var line in LogLines.CombineAsync(live, follow: true, ct))
        {
            yield return line;
        }
    }

    /// <summary>
    /// The last <paramref name="tail"/> lines (all when null). Reads a window from the end of the
    /// log; when that holds fewer lines than asked for, the log is shorter than the window, so it
    /// is read from the start instead, which keeps its first line.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadTailAsync(
        NomadClient client, NomadAllocation alloc, bool stderr, int? tail, CancellationToken ct)
    {
        if (tail is { } n)
        {
            var window = Math.Min(MaxTailBytes, (long)Math.Max(1, n) * BytesPerLine);
            var lines = await client.StreamTaskLogLinesAsync(alloc.Id, alloc.Task, stderr, follow: false, window, ct).ToListAsync(ct);
            if (lines.Count >= n)
            {
                return lines[^n..];
            }
        }

        var all = await client.StreamTaskLogLinesAsync(alloc.Id, alloc.Task, stderr, follow: false, bytesFromEnd: null, ct).ToListAsync(ct);
        return tail is { } t && all.Count > t ? all[^t..] : all;
    }

    private static async IAsyncEnumerable<ServiceLogLine> FollowAsync(
        NomadClient client, NomadAllocation alloc, bool stderr, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var text in client.StreamTaskLogLinesAsync(alloc.Id, alloc.Task, stderr, follow: true, bytesFromEnd: 0, ct))
        {
            yield return Line(alloc, text, stderr);
        }
    }

    private static ServiceLogLine Line(NomadAllocation alloc, string text, bool stderr)
        => new() { Text = text, Instance = alloc.Id[..Math.Min(8, alloc.Id.Length)], IsError = stderr };

    internal sealed record NomadAllocation(string Id, string Task);

    /// <summary>
    /// The allocations that are running or about to; when there are none, the newest one, so the
    /// output of a job that keeps crashing can still be read. Oldest first.
    /// </summary>
    internal static IReadOnlyList<NomadAllocation> PickAllocations(string allocationsJson, string jobId)
    {
        using var doc = JsonDocument.Parse(allocationsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var all = doc.RootElement.EnumerateArray()
            .Select(a => (
                Id: Str(a, "ID"),
                Status: Str(a, "ClientStatus"),
                Created: a.TryGetProperty("CreateTime", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0,
                Task: MainTask(a, jobId)))
            .Where(a => !string.IsNullOrEmpty(a.Id))
            .OrderBy(a => a.Created)
            .ToList();
        var active = all.Where(a => a.Status is "running" or "pending").ToList();
        var picked = active.Count > 0 ? active : all.Count > 0 ? [all[^1]] : [];
        return [.. picked.Select(a => new NomadAllocation(a.Id!, a.Task))];
    }

    /// <summary>The task named like the job; otherwise the first that isn't a sidecar such as "{job}-dapr".</summary>
    private static string MainTask(JsonElement alloc, string jobId)
    {
        if (!alloc.TryGetProperty("TaskStates", out var states) || states.ValueKind != JsonValueKind.Object)
        {
            return jobId;
        }

        var names = states.EnumerateObject().Select(p => p.Name).ToList();
        return names.Contains(jobId, StringComparer.Ordinal)
            ? jobId
            : names.FirstOrDefault(n => !n.EndsWith("-dapr", StringComparison.Ordinal)) ?? jobId;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
