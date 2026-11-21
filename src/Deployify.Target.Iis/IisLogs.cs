using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.ServiceLogs;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Iis;

/// <summary>
/// A site's logs on IIS, read on the server by deployify-iis.ps1 (<c>-Action Logs</c>): the app's
/// console output when <see cref="IisTargetConfig.CaptureStdout"/> is on (the ASP.NET Core
/// Module's stdout files, one per process, shown as <c>pid-N</c>), and the module's own entries
/// in the Application event log for the site (start-up failures and crashes, shown as
/// <c>event-log</c>). Following asks the server again every few seconds for what is new since
/// the last answer.
/// </summary>
internal static class IisLogs
{
    public static async IAsyncEnumerable<ServiceLogLine> ReadAsync(
        IisRemote remote, string siteName, ServiceLogQuery query, TimeSpan pollInterval, ILogger logger,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["siteName"] = siteName,
            ["tail"] = query.Tail,
            ["since"] = query.Since?.UtcDateTime.ToString("o"),
            ["follow"] = query.Follow
        };

        // The answer holds the app's output: keep it out of Deployify's own log.
        var result = await remote.RunAsync("Logs", request, sensitive: true, ct);
        if (!(result.TryGetProperty("captured", out var captured) && captured.ValueKind == JsonValueKind.True))
        {
            logger.LogWarning(
                "{Site} doesn't write its console output anywhere Deployify can read, so only the ASP.NET Core Module's " +
                "event log entries are shown. Set iis.captureStdout to true and apply to capture it.", siteName);
        }

        foreach (var line in Parse(result))
        {
            if (LogLines.IsSince(line, query.Since))
            {
                yield return line;
            }
        }

        while (query.Follow)
        {
            await Task.Delay(pollInterval, ct);
            request["cursor"] = result.TryGetProperty("cursor", out var cursor) ? JsonNode.Parse(cursor.GetRawText()) : null;
            request["tail"] = null;
            result = await remote.RunAsync("Logs", request, sensitive: true, ct);
            foreach (var line in Parse(result))
            {
                yield return line;
            }
        }
    }

    /// <summary>The <c>lines</c> of an answer. Windows PowerShell 5.1 writes a one-item array as the item itself.</summary>
    internal static IReadOnlyList<ServiceLogLine> Parse(JsonElement result)
    {
        if (!result.TryGetProperty("lines", out var lines))
        {
            return [];
        }

        var items = lines.ValueKind switch
        {
            JsonValueKind.Array => [.. lines.EnumerateArray()],
            JsonValueKind.Object => [lines],
            _ => new List<JsonElement>()
        };

        var parsed = new List<ServiceLogLine>(items.Count);
        foreach (var item in items)
        {
            DateTimeOffset? time = item.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.String &&
                                   LogLines.TryParseTimestamp(t.GetString()!, out var ts)
                ? ts
                : null;
            parsed.Add(new ServiceLogLine
            {
                Text = item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "",
                Instance = item.TryGetProperty("instance", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null,
                Timestamp = time,
                IsError = item.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.True
            });
        }

        return parsed;
    }
}
