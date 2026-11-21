using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Deployify.Core.ServiceLogs;

namespace Deployify.Target.Compose;

/// <summary>
/// A service's logs on Docker: <c>docker logs</c> of each of the service's containers in the
/// Compose project, found by Compose's labels, so no compose file is needed. Running containers
/// are read; when none runs, the most recent one, so a crashed service's output can still be read.
/// </summary>
internal static class ComposeLogs
{
    public static async IAsyncEnumerable<ServiceLogLine> ReadAsync(
        ComposeRunner runner, string project, ServiceLogQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var containers = await runner.ListServiceContainersAsync(query.Service, ct);
        if (containers.Count == 0)
        {
            throw new ServiceLogsException(
                $"Compose project '{project}' has no containers for '{query.Service}'. Is it deployed to this environment?");
        }

        var running = containers.Where(c => c.State == "running").ToList();
        var picked = running.Count > 0 ? running : [containers[0]];
        var streams = picked.Select(c => ContainerAsync(runner, c.Id, InstanceName(c.Name, project), query, ct)).ToList();

        if (query.Follow)
        {
            await foreach (var line in LogLines.CombineAsync(streams, follow: true, ct))
            {
                yield return line;
            }

            yield break;
        }

        // stdout and stderr arrive on separate pipes; the timestamps put them back in order.
        var all = new List<ServiceLogLine>();
        foreach (var stream in streams)
        {
            all.AddRange(await stream.ToListAsync(ct));
        }

        foreach (var line in all.OrderBy(l => l.Timestamp ?? DateTimeOffset.MinValue))
        {
            yield return line;
        }
    }

    private static async IAsyncEnumerable<ServiceLogLine> ContainerAsync(
        ComposeRunner runner, string id, string instance, ServiceLogQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        var problems = new List<string>();
        var count = 0;
        await foreach (var raw in runner.StreamContainerLogsAsync(id, query.Tail, query.Since, query.Follow, ct))
        {
            var (timestamp, text) = LogLines.SplitTimestamp(raw.Text);
            if (timestamp is null && raw.IsStdErr)
            {
                // With --timestamps every line of the container has one: this is docker's own message.
                problems.Add(raw.Text);
                continue;
            }

            count++;
            yield return new ServiceLogLine { Text = text, Instance = instance, Timestamp = timestamp, IsError = raw.IsStdErr };
        }

        if (count == 0 && problems.Count > 0)
        {
            throw new ServiceLogsException($"docker could not read the logs of {instance}: {string.Join(" ", problems).Trim()}");
        }
    }

    /// <summary>"deployify-api-1" in project "deployify" is shown as "api-1".</summary>
    internal static string InstanceName(string containerName, string project)
        => containerName.StartsWith(project + "-", StringComparison.Ordinal) ? containerName[(project.Length + 1)..] : containerName;
}
