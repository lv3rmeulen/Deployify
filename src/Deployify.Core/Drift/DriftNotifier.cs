using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Core.Drift;

/// <summary>An alert about one environment.</summary>
/// <param name="Event">Why it is sent.</param>
/// <param name="Check">The check that called for it.</param>
/// <param name="Since">When the environment entered its current status.</param>
/// <param name="PreviousStatus">The status before this check, when there was one.</param>
/// <param name="RunUrl">The CI run that made the check, when known.</param>
public sealed record DriftAlert(DriftEvent Event, DriftCheck Check, DateTimeOffset Since, DriftStatus? PreviousStatus, string? RunUrl);

/// <summary>Whether an alert reached one channel.</summary>
public sealed record DriftDelivery(string Channel, DriftChannelType Type, bool Ok, string? Error);

/// <summary>Sends drift alerts.</summary>
public interface IDriftNotifier
{
    /// <summary>Sends <paramref name="alert"/> to <paramref name="channel"/>. Never throws for a failed delivery; the result says.</summary>
    Task<DriftDelivery> SendAsync(DriftChannel channel, DriftAlert alert, CancellationToken ct = default);
}

/// <summary>Posts alerts to Slack, Teams and plain webhooks over HTTP.</summary>
public sealed class HttpDriftNotifier(HttpClient http, Func<string, string?>? getVariable = null) : IDriftNotifier
{
    private readonly Func<string, string?> _getVariable = getVariable ?? Environment.GetEnvironmentVariable;

    public async Task<DriftDelivery> SendAsync(DriftChannel channel, DriftAlert alert, CancellationToken ct = default)
    {
        try
        {
            var url = Resolve(channel.Url, "url");
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(DriftMessages.Payload(channel.Type, alert).ToJsonString(), Encoding.UTF8, "application/json")
            };
            if (channel.Type == DriftChannelType.Webhook)
            {
                foreach (var (name, value) in channel.Headers)
                {
                    request.Headers.TryAddWithoutValidation(name, Resolve(value, $"header {name}"));
                }
            }

            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return new DriftDelivery(channel.DisplayName, channel.Type, true, null);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return new DriftDelivery(channel.DisplayName, channel.Type, false,
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}" + (body.Length == 0 ? "" : $": {Truncate(body, 200)}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The URL is a secret: say what went wrong without it.
            var message = ex is TaskCanceledException ? "timed out" : ex.Message;
            return new DriftDelivery(channel.DisplayName, channel.Type, false, message);
        }
    }

    private string Resolve(string value, string what)
    {
        if (!value.StartsWith(DriftChannel.EnvPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        var name = value[DriftChannel.EnvPrefix.Length..];
        return _getVariable(name) is { Length: > 0 } resolved
            ? resolved
            : throw new InvalidOperationException($"The {what} comes from environment variable {name}, which is not set.");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>What an alert says, as text and as each channel's payload.</summary>
public static class DriftMessages
{
    /// <summary>How many differences a chat message lists before "and N more".</summary>
    public const int MaxListed = 10;

    /// <summary>One line: "prod drifted from its deploy: 2 changed, 1 missing".</summary>
    public static string Title(DriftAlert alert)
    {
        var env = alert.Check.Environment;
        return alert.Event switch
        {
            DriftEvent.Drifted => $"{env} drifted from its last deploy: {Counts(alert.Check)}",
            DriftEvent.Changed => $"{env} drift changed: {Counts(alert.Check)}",
            DriftEvent.Reminder when alert.Check.Status == DriftStatus.Drifted =>
                $"{env} is still drifted (since {alert.Since:yyyy-MM-dd HH:mm} UTC): {Counts(alert.Check)}",
            DriftEvent.Reminder => $"{env} drift check is still failing (since {alert.Since:yyyy-MM-dd HH:mm} UTC)",
            DriftEvent.Resolved when alert.PreviousStatus == DriftStatus.Drifted => $"{env} matches its last deploy again",
            DriftEvent.Resolved => $"{env} drift check is working again: no drift",
            DriftEvent.Failing when alert.Check.Status == DriftStatus.Unknown =>
                $"{env} drift check couldn't compare {alert.Check.Count(DriftKind.Unknown)} service(s)",
            DriftEvent.Failing => $"{env} drift check failed",
            _ => env
        };
    }

    /// <summary>"2 changed, 1 missing, 1 extra".</summary>
    public static string Counts(DriftCheck check)
    {
        var parts = new List<string>();
        foreach (var (kind, label) in new[] { (DriftKind.Changed, "changed"), (DriftKind.Missing, "missing"), (DriftKind.Extra, "extra"), (DriftKind.Unknown, "not checked") })
        {
            if (check.Count(kind) is var n and > 0)
            {
                parts.Add($"{n} {label}");
            }
        }

        return parts.Count == 0 ? "no differences" : string.Join(", ", parts);
    }

    /// <summary>One line per difference, the way the CLI prints them: "~ api (image)".</summary>
    public static IEnumerable<string> Lines(DriftCheck check)
        => check.Entries.Select(e => e.Kind switch
        {
            DriftKind.Missing => $"- {e.Service}: deployed, but no longer on the target",
            DriftKind.Extra => $"+ {e.Service}: on the target, but not deployed by Deployify",
            DriftKind.Changed => $"~ {e.Service}" + (e.Field is null ? ": changed" : $": {e.Field} changed") + ShortValues(e),
            _ => $"? {e.Service}: couldn't be compared" + (e.Reason is null ? "" : $" ({OneLine(e.Reason)})")
        });

    /// <summary>The facts under the title: the target, the deploy compared against, the reason.</summary>
    public static IEnumerable<string> Details(DriftAlert alert)
    {
        var check = alert.Check;
        if (check.TargetName is not null)
        {
            yield return $"Target: {check.TargetName}";
        }

        if (check.Baseline is { } b)
        {
            var what = string.Join(", ", new[] { b.Tag is null ? null : $"tag {b.Tag}", b.Commit is null ? null : $"commit {Short(b.Commit)}", b.Actor is null ? null : $"by {b.Actor}" }
                .Where(s => s is not null));
            yield return $"Last deploy: {b.At:yyyy-MM-dd HH:mm} UTC" + (what.Length == 0 ? "" : $" ({what})");
        }

        if (check.Reason is not null && check.Status is DriftStatus.Error)
        {
            yield return $"Reason: {OneLine(check.Reason)}";
        }

        if (check.Ignored > 0)
        {
            yield return $"Ignored: {check.Ignored} expected difference(s) (drift.ignore)";
        }
    }

    public static JsonObject Payload(DriftChannelType type, DriftAlert alert) => type switch
    {
        DriftChannelType.Slack => Slack(alert),
        DriftChannelType.Teams => Teams(alert),
        _ => Webhook(alert)
    };

    /// <summary>A Slack incoming-webhook message: the title and a bulleted list in mrkdwn, plain text as the fallback.</summary>
    public static JsonObject Slack(DriftAlert alert)
    {
        var text = new StringBuilder();
        text.Append(Emoji(alert)).Append(" *").Append(SlackEscape(Title(alert))).Append('*');
        foreach (var line in Listed(alert.Check))
        {
            text.Append("\n• ").Append(SlackEscape(line));
        }

        var context = Details(alert).Select(SlackEscape).ToList();
        if (alert.RunUrl is not null)
        {
            context.Add($"<{alert.RunUrl}|Drift check run>");
        }

        var blocks = new JsonArray
        {
            new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = text.ToString() } }
        };
        if (context.Count > 0)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "context",
                ["elements"] = new JsonArray(context.Select(c => (JsonNode)new JsonObject { ["type"] = "mrkdwn", ["text"] = c }).ToArray())
            });
        }

        return new JsonObject { ["text"] = Title(alert), ["blocks"] = blocks };
    }

    /// <summary>A message with an Adaptive Card, as a Teams "When a webhook request is received" workflow expects.</summary>
    public static JsonObject Teams(DriftAlert alert)
    {
        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock", ["size"] = "Medium", ["weight"] = "Bolder", ["wrap"] = true,
                ["color"] = alert.Event == DriftEvent.Resolved ? "Good" : "Attention",
                ["text"] = Title(alert)
            }
        };
        foreach (var line in Listed(alert.Check))
        {
            body.Add(new JsonObject { ["type"] = "TextBlock", ["wrap"] = true, ["spacing"] = "None", ["text"] = "- " + line });
        }

        var facts = new JsonArray();
        foreach (var detail in Details(alert))
        {
            var colon = detail.IndexOf(": ", StringComparison.Ordinal);
            facts.Add(new JsonObject { ["title"] = detail[..colon], ["value"] = detail[(colon + 2)..] });
        }

        if (facts.Count > 0)
        {
            body.Add(new JsonObject { ["type"] = "FactSet", ["facts"] = facts });
        }

        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body
        };
        if (alert.RunUrl is not null)
        {
            card["actions"] = new JsonArray
            {
                new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Drift check run", ["url"] = alert.RunUrl }
            };
        }

        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray
            {
                new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["content"] = card }
            }
        };
    }

    /// <summary>
    /// Everything about the alert, for a handler to act on. Field names are part of the 1.x
    /// stability policy: new ones may be added, existing ones keep their meaning.
    /// </summary>
    public static JsonObject Webhook(DriftAlert alert)
    {
        var check = alert.Check;
        var b = check.Baseline;
        return new JsonObject
        {
            ["source"] = "deployify",
            ["kind"] = "drift",
            ["event"] = Camel(alert.Event),
            ["title"] = Title(alert),
            ["environment"] = check.Environment,
            ["target"] = check.TargetId is null ? null : new JsonObject { ["id"] = check.TargetId, ["name"] = check.TargetName },
            ["status"] = Camel(check.Status),
            ["previousStatus"] = alert.PreviousStatus is { } p ? Camel(p) : null,
            ["since"] = alert.Since,
            ["checkedAt"] = check.CheckedAt,
            ["fingerprint"] = check.Fingerprint,
            ["reason"] = check.Reason,
            ["ignored"] = check.Ignored,
            ["runUrl"] = alert.RunUrl,
            ["deployed"] = b is null ? null : new JsonObject
            {
                ["releaseId"] = b.ReleaseId,
                ["at"] = b.At,
                ["tag"] = b.Tag,
                ["commit"] = b.Commit,
                ["actor"] = b.Actor,
                ["runUrl"] = b.RunUrl
            },
            ["entries"] = new JsonArray([.. check.Entries.Select(e => (JsonNode)new JsonObject
            {
                ["service"] = e.Service,
                ["kind"] = Camel(e.Kind),
                ["field"] = e.Field,
                ["live"] = e.Live,
                ["deployed"] = e.Deployed,
                ["reason"] = e.Reason
            })])
        };
    }

    private static IEnumerable<string> Listed(DriftCheck check)
    {
        var lines = Lines(check).ToList();
        foreach (var line in lines.Take(MaxListed))
        {
            yield return line;
        }

        if (lines.Count > MaxListed)
        {
            yield return $"… and {lines.Count - MaxListed} more";
        }
    }

    private static string ShortValues(DriftEntry e)
    {
        // A rendered diff (Kubernetes, Helm) is too long for a chat line; single values fit.
        if (e.Live is null || e.Deployed is null || e.Live.Contains('\n') || e.Deployed.Contains('\n') ||
            e.Live.Length + e.Deployed.Length > 120)
        {
            return "";
        }

        return $" (live {e.Live}, deployed {e.Deployed})";
    }

    private static string Emoji(DriftAlert alert) => alert.Event switch
    {
        DriftEvent.Resolved => ":white_check_mark:",
        DriftEvent.Failing => ":grey_question:",
        _ => ":warning:"
    };

    private static string OneLine(string s) => s.ReplaceLineEndings(" ");

    private static string Short(string commit) => commit.Length > 12 ? commit[..12] : commit;

    private static string SlackEscape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Camel<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
