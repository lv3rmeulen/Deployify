using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Deployify.Core.ServiceLogs;

namespace Deployify.Target.DigitalOcean;

/// <summary>
/// A service's run logs on App Platform: the logs endpoint of its component in the environment's
/// app, which returns signed URLs to read from. Without following, the live URL (or, when the
/// component has none, its archived logs) is read to the end; following streams the live URL.
/// </summary>
internal static class DoLogs
{
    public static async IAsyncEnumerable<ServiceLogLine> ReadAsync(
        IDigitalOceanApi api, string appName, IReadOnlySet<string> components, string appId, ServiceLogQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var component = Names.Component(query.Service);
        if (!components.Contains(component))
        {
            throw new ServiceLogsException(
                $"App '{appName}' has no component '{component}' for '{query.Service}'. Is it deployed to this environment?");
        }

        var urls = await api.GetRunLogUrlsAsync(appId, component, query.Follow, query.Tail, ct);
        IReadOnlyList<string> sources = urls.LiveUrl is { } live ? [live] : query.Follow ? [] : urls.HistoricUrls;
        foreach (var url in sources)
        {
            await foreach (var text in api.ReadLogLinesAsync(url, ct))
            {
                if (text.Length > 0 && Parse(text, component) is var line && LogLines.IsSince(line, query.Since))
                {
                    yield return line;
                }
            }
        }
    }

    /// <summary>
    /// A run log line: "COMPONENT [INSTANCE] TIME TEXT", where the parts before the text are
    /// left out when missing. Anything that doesn't look like that is the text itself.
    /// </summary>
    internal static ServiceLogLine Parse(string text, string component)
    {
        var rest = text;
        if (rest.StartsWith(component + " ", StringComparison.Ordinal))
        {
            rest = rest[(component.Length + 1)..];
        }

        var (timestamp, afterTime) = LogLines.SplitTimestamp(rest);
        if (timestamp is not null)
        {
            return new ServiceLogLine { Text = afterTime, Timestamp = timestamp };
        }

        // An instance name before the time.
        var space = rest.IndexOf(' ');
        if (space > 0 && LogLines.SplitTimestamp(rest[(space + 1)..]) is ({ } t, var body))
        {
            return new ServiceLogLine { Text = body, Instance = rest[..space], Timestamp = t };
        }

        return new ServiceLogLine { Text = text };
    }
}
