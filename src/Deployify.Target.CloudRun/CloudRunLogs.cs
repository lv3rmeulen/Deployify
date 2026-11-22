using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.ServiceLogs;

namespace Deployify.Target.CloudRun;

/// <summary>
/// A service's console output on Cloud Run, read from Cloud Logging: the stdout and stderr logs
/// of the service's revisions (request logs left out). The tail is the most recent lines of the
/// service as a whole; following asks Cloud Logging for newer entries every few seconds.
/// </summary>
internal static class CloudRunLogs
{
    /// <summary>What "all" reads: the most Deployify asks Cloud Logging for at once.</summary>
    internal const int MaxEntries = 10_000;

    public static async IAsyncEnumerable<ServiceLogLine> ReadAsync(
        GcloudClient client, CloudRunTargetConfig cfg, ServiceLogQuery query, TimeSpan pollInterval,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (await client.DescribeAsync(query.Service, ct) is null)
        {
            throw new ServiceLogsException($"{query.Service} isn't deployed to Cloud Run in {cfg.ProjectId}/{cfg.Region}.");
        }

        var filter = Filter(query.Service, cfg.Region, query.Since);
        DateTimeOffset? last = null;
        if (query.Tail != 0)
        {
            var limit = Math.Clamp(query.Tail ?? MaxEntries, 1, MaxEntries);
            var lines = await client.ReadLogsAsync(filter, limit, query.Since is not null, ct);
            foreach (var line in Parse(lines).OrderBy(l => l.Timestamp))
            {
                last = line.Timestamp;
                yield return line;
            }
        }

        last ??= query.Since ?? DateTimeOffset.UtcNow;
        while (query.Follow)
        {
            await Task.Delay(pollInterval, ct);
            var json = await client.ReadLogsAsync(Filter(query.Service, cfg.Region, last, exclusive: true), MaxEntries, true, ct);
            foreach (var line in Parse(json).OrderBy(l => l.Timestamp))
            {
                last = line.Timestamp ?? last;
                yield return line;
            }
        }
    }

    internal static string Filter(string service, string region, DateTimeOffset? since, bool exclusive = false)
    {
        var filter = $"resource.type=\"cloud_run_revision\" AND resource.labels.service_name=\"{service}\" AND " +
                     $"resource.labels.location=\"{region}\" AND " +
                     "(log_id(\"run.googleapis.com/stdout\") OR log_id(\"run.googleapis.com/stderr\"))";
        return since is { } t
            ? $"{filter} AND timestamp{(exclusive ? ">" : ">=")}\"{t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture)}\""
            : filter;
    }

    /// <summary>Entries of <c>gcloud logging read --format json</c>.</summary>
    internal static IEnumerable<ServiceLogLine> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            yield break;
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var text = CloudRunServiceStatus.Str(entry, "textPayload")
                       ?? (CloudRunServiceStatus.Obj(entry, "jsonPayload") is { } payload
                           ? CloudRunServiceStatus.Str(payload, "message") ?? payload.GetRawText()
                           : null);
            if (text is null)
            {
                continue;
            }

            DateTimeOffset? time = DateTimeOffset.TryParse(CloudRunServiceStatus.Str(entry, "timestamp"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var t) ? t : null;
            var instance = CloudRunServiceStatus.Obj(entry, "labels") is { } labels ? CloudRunServiceStatus.Str(labels, "instanceId") : null;
            yield return new ServiceLogLine
            {
                Text = text.TrimEnd('\n', '\r'),
                Timestamp = time,
                Instance = instance is { Length: > 12 } ? instance[^12..] : instance,
                IsError = CloudRunServiceStatus.Str(entry, "logName")?.EndsWith("%2Fstderr", StringComparison.Ordinal) == true
            };
        }
    }
}
