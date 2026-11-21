using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Deployify.Core.ServiceLogs;

namespace Deployify.Target.AwsEcs;

/// <summary>
/// A service's logs on ECS: the CloudWatch Logs streams its tasks write through the awslogs
/// driver, named <c>{service}/{service}/{taskId}</c> in the environment's log group. The tail
/// comes from each running task's stream (or, when none runs, the most recent stream);
/// following uses <c>aws logs tail --follow</c> on every stream of the service, so tasks that
/// start later are followed too.
/// </summary>
internal static class EcsLogs
{
    /// <summary>The most get-log-events returns in one call; also what "all" reads per task.</summary>
    internal const int MaxEvents = 10_000;

    public static async IAsyncEnumerable<ServiceLogLine> ReadAsync(
        EcsCliClient client, ServiceLogQuery query, Func<DateTimeOffset> now, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var prefix = $"{query.Service}/{query.Service}/";
        var started = now();
        var history = new List<ServiceLogLine>();
        if (query.Tail != 0)
        {
            var taskIds = await client.ListServiceTaskIdsAsync(query.Service, ct);
            var streams = taskIds.Select(id => prefix + id).ToList();
            if (streams.Count == 0 && await client.GetLatestLogStreamAsync(prefix, ct) is { } latest)
            {
                streams.Add(latest);
            }

            var limit = Math.Clamp(query.Tail ?? MaxEvents, 1, MaxEvents);
            foreach (var stream in streams)
            {
                foreach (var (time, message) in await client.GetLogEventsAsync(stream, limit, query.Since, ct))
                {
                    history.Add(new ServiceLogLine { Text = message, Instance = TaskOf(stream), Timestamp = time });
                }
            }
        }
        else
        {
            // Still fails clearly for a service that doesn't exist.
            await client.ListServiceTaskIdsAsync(query.Service, ct);
        }

        foreach (var line in history.OrderBy(l => l.Timestamp))
        {
            yield return line;
        }

        if (!query.Follow)
        {
            yield break;
        }

        // Follow from the newest line already shown (or from now), skipping the lines shown at
        // that same moment, so nothing in between is lost or printed twice.
        var from = history.Count > 0 ? history.Max(l => l.Timestamp!.Value) : started;
        if (query.Since is { } since && since > from)
        {
            from = since;
        }

        var shown = history.Where(l => l.Timestamp >= from).Select(Key).ToHashSet();
        var problems = new List<string>();
        await foreach (var raw in client.FollowLogsAsync(prefix, from, ct))
        {
            if (raw.IsStdErr)
            {
                problems.Add(raw.Text);
                continue;
            }

            if (ParseTail(raw.Text) is { } line && !shown.Remove(Key(line)))
            {
                yield return line;
            }
        }

        if (problems.Count > 0 && !ct.IsCancellationRequested)
        {
            throw new ServiceLogsException($"aws logs tail stopped: {string.Join(" ", problems).Trim()}");
        }
    }

    /// <summary>One line of <c>aws logs tail --format detailed</c>: "TIME STREAM MESSAGE".</summary>
    internal static ServiceLogLine? ParseTail(string text)
    {
        var (timestamp, rest) = LogLines.SplitTimestamp(text);
        if (timestamp is null)
        {
            return null;
        }

        var space = rest.IndexOf(' ');
        var stream = space < 0 ? rest : rest[..space];
        return new ServiceLogLine
        {
            Text = space < 0 ? "" : rest[(space + 1)..],
            Instance = TaskOf(stream),
            Timestamp = timestamp
        };
    }

    /// <summary>"api/api/0123456789abcdef" is task "0123456789abcdef", shown by its first 8 characters like the console.</summary>
    internal static string TaskOf(string stream)
    {
        var id = stream[(stream.LastIndexOf('/') + 1)..];
        return id.Length > 8 ? id[..8] : id;
    }

    private static (string?, DateTimeOffset?, string) Key(ServiceLogLine l) => (l.Instance, l.Timestamp, l.Text);
}
