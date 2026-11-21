using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Deployify.Core.ServiceLogs;

namespace Deployify.Target.AzureContainerApps;

/// <summary>
/// A service's logs on Azure Container Apps: the console log stream of the app's container in
/// each replica of its latest ready revision, through <c>az containerapp logs show</c>, which
/// reads one replica at a time and keeps at most the last 300 lines of each.
/// </summary>
internal static class AcaLogs
{
    /// <summary>What the log stream API returns at most.</summary>
    internal const int MaxTail = 300;

    public static async IAsyncEnumerable<ServiceLogLine> ReadAsync(
        AcaCliClient client, ServiceLogQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var app = await client.GetAppAsync(query.Service, ct)
                  ?? throw new ServiceLogsException(
                      $"There is no Container App '{query.Service}' in this resource group. Is it deployed to this environment?");
        var revision = app.LatestReadyRevision ?? app.LatestRevision;
        if (revision is null)
        {
            yield break;
        }

        var replicas = await client.ListReplicasAsync(query.Service, revision, ct);
        var tail = Math.Clamp(query.Tail ?? MaxTail, 0, MaxTail);
        var streams = replicas
            .Select(r => ReplicaAsync(client, query, revision, r.Name,
                r.Containers.Contains(query.Service, StringComparer.Ordinal) ? query.Service : r.Containers.FirstOrDefault() ?? query.Service,
                tail, ct))
            .ToList();
        await foreach (var line in LogLines.CombineAsync(streams, query.Follow, ct))
        {
            yield return line;
        }
    }

    private static async IAsyncEnumerable<ServiceLogLine> ReplicaAsync(
        AcaCliClient client, ServiceLogQuery query, string revision, string replica, string container, int tail,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var problems = new List<string>();
        var count = 0;
        await foreach (var raw in client.StreamLogsAsync(query.Service, revision, replica, container, tail, query.Follow, ct))
        {
            if (raw.IsStdErr)
            {
                problems.Add(raw.Text);
                continue;
            }

            if (Parse(raw.Text, ReplicaName(replica, revision)) is { } line && LogLines.IsSince(line, query.Since))
            {
                count++;
                yield return line;
            }
        }

        // The Azure CLI also warns on stderr while it works; that's only a failure when nothing came out.
        if (count == 0 && problems.Any(p => p.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ServiceLogsException($"az could not read the logs of replica {replica}: {string.Join(" ", problems).Trim()}");
        }
    }

    /// <summary>
    /// One line of <c>--format json</c>: <c>{"TimeStamp": "...", "Log": "..."}</c>. The log text
    /// can carry the container runtime's own prefix ("stdout F text"), which says which stream
    /// it was. The stream's own "Connecting to the container" notices are left out.
    /// </summary>
    internal static ServiceLogLine? Parse(string json, string instance)
    {
        string? time, text;
        try
        {
            using var doc = JsonDocument.Parse(json);
            time = AcaJson.Str(doc.RootElement, "TimeStamp");
            text = AcaJson.Str(doc.RootElement, "Log");
        }
        catch (JsonException)
        {
            return null;
        }

        if (text is null ||
            text.StartsWith("Connecting to the container", StringComparison.Ordinal) ||
            text.StartsWith("Successfully Connected to container", StringComparison.Ordinal))
        {
            return null;
        }

        DateTimeOffset? timestamp = time is not null && LogLines.TryParseTimestamp(time, out var t) ? t : null;
        var (runtimeTime, rest) = LogLines.SplitTimestamp(text);
        var isError = false;
        if (rest.StartsWith("stdout ", StringComparison.Ordinal) || rest.StartsWith("stderr ", StringComparison.Ordinal))
        {
            var afterStream = rest[7..];
            if (afterStream.StartsWith("F ", StringComparison.Ordinal) || afterStream.StartsWith("P ", StringComparison.Ordinal))
            {
                isError = rest.StartsWith("stderr", StringComparison.Ordinal);
                text = afterStream[2..];
                timestamp = runtimeTime ?? timestamp;
            }
        }

        return new ServiceLogLine { Text = text.TrimEnd('\r', '\n'), Instance = instance, Timestamp = timestamp, IsError = isError };
    }

    /// <summary>"api--0000003-7d9f8c6b5-x2x9z" is shown without its revision: "7d9f8c6b5-x2x9z".</summary>
    internal static string ReplicaName(string replica, string revision)
        => replica.StartsWith(revision + "-", StringComparison.Ordinal) ? replica[(revision.Length + 1)..] : replica;
}
