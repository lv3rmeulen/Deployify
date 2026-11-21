using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Deployify.Core.ServiceLogs;

namespace Deployify.Target.Kubernetes;

/// <summary>
/// A service's logs on a cluster: <c>kubectl logs</c> of every pod labelled <c>app={service}</c>
/// in the service's namespace. Shared by every target that deploys to Kubernetes (Kubernetes,
/// Helm, Argo CD, Flux, OVHcloud), since they all deploy the same manifests.
/// </summary>
internal static class KubernetesLogs
{
    public static async IAsyncEnumerable<ServiceLogLine> ReadAsync(
        KubectlRunner kubectl, string namespaceName, ServiceLogQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var problems = new List<string>();
        var count = 0;
        var lines = kubectl.StreamServiceLogsAsync(namespaceName, query.Service, query.Tail, query.Since, query.Follow, ct);
        if (query.Follow)
        {
            await foreach (var line in lines)
            {
                if (line.IsStdErr)
                {
                    problems.Add(line.Text);
                    continue;
                }

                count++;
                yield return Parse(line.Text, query.Service);
            }
        }
        else
        {
            // kubectl prints one pod after the other; put them in time order.
            var all = new List<ServiceLogLine>();
            await foreach (var line in lines)
            {
                if (line.IsStdErr)
                {
                    problems.Add(line.Text);
                }
                else
                {
                    all.Add(Parse(line.Text, query.Service));
                    count++;
                }
            }

            foreach (var line in LogLines.InTimeOrder([.. all.GroupBy(l => l.Instance ?? "").Select(g => (IReadOnlyList<ServiceLogLine>)[.. g])]))
            {
                yield return line;
            }
        }

        // kubectl also warns on stderr while it works; that's only a failure when nothing came out.
        if (count == 0)
        {
            ThrowIfFailed(problems, namespaceName, query.Service);
        }
    }

    /// <summary>
    /// One line of <c>kubectl logs --prefix --timestamps</c>: "[pod/NAME/CONTAINER] TIME TEXT".
    /// The instance is the pod, with the container added when it isn't the service's own.
    /// </summary>
    internal static ServiceLogLine Parse(string text, string service)
    {
        string? instance = null;
        if (text.StartsWith("[pod/", StringComparison.Ordinal) && text.IndexOf("] ", StringComparison.Ordinal) is var close and > 0)
        {
            var parts = text[5..close].Split('/');
            instance = parts.Length >= 2 && parts[1] != service ? $"{parts[0]}/{parts[1]}" : parts[0];
            text = text[(close + 2)..];
        }

        var (timestamp, rest) = LogLines.SplitTimestamp(text);
        return new ServiceLogLine { Text = rest, Instance = instance, Timestamp = timestamp };
    }

    private static void ThrowIfFailed(List<string> problems, string namespaceName, string service)
    {
        var messages = problems.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (messages.Count == 0)
        {
            return;
        }

        if (messages.Any(m => m.StartsWith("No resources found", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ServiceLogsException(
                $"'{service}' has no pods in namespace {namespaceName}. Is it deployed to this environment?");
        }

        throw new ServiceLogsException($"kubectl could not read the logs of '{service}': {string.Join(" ", messages).Trim()}");
    }
}
