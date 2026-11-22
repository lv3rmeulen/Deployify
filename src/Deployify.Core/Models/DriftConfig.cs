using System;
using System.Collections.Generic;

namespace Deployify.Core.Models;

/// <summary>
/// The <c>drift</c> section of an environment: whether <c>deployify drift</c> checks it, where
/// alerts go, and how often a drift that is still there is reported again. Leave it out and the
/// environment is still checked, with results printed and in the exit code but no alerts sent.
/// </summary>
public sealed class DriftConfig
{
    /// <summary>
    /// When false, <c>deployify drift --all</c> skips this environment (a dev environment people
    /// change by hand, say). Checking it by name with <c>--env</c> still works.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Where alerts go. Empty: nowhere; the check still reports and sets the exit code.</summary>
    public List<DriftChannel> Notify { get; set; } = [];

    /// <summary>
    /// Alert again about a drift that hasn't changed after this many hours. Null (the default)
    /// alerts once per drift: when it appears, when what drifted changes, and when it is resolved.
    /// </summary>
    public double? RemindAfterHours { get; set; }

    /// <summary>Send an alert when an environment that drifted matches its deploy again. Default true.</summary>
    public bool NotifyOnResolve { get; set; } = true;

    /// <summary>
    /// Send an alert when the check itself can't tell (the target is unreachable, or some services
    /// couldn't be compared). Default true: a check that keeps failing is a check that isn't running.
    /// </summary>
    public bool NotifyOnError { get; set; } = true;

    /// <summary>
    /// Differences that are expected and never count as drift: a service name (<c>worker</c>), or a
    /// service and field (<c>worker:instance_count</c>, as <c>deployify diff</c> names fields).
    /// <c>*</c> matches any service: <c>*:desired_count</c> for an autoscaled count.
    /// </summary>
    public List<string> Ignore { get; set; } = [];

    /// <summary>
    /// Why this section isn't valid, or null when it is. Checked before a drift run, so a typo in
    /// a channel fails the check loudly instead of silently sending no alerts.
    /// </summary>
    public string? Validate()
    {
        if (RemindAfterHours is { } hours && (double.IsNaN(hours) || hours <= 0))
        {
            return $"drift.remindAfterHours is {hours}; use a number of hours above 0, or leave it out to alert once per drift.";
        }

        for (var i = 0; i < Notify.Count; i++)
        {
            var channel = Notify[i];
            if (string.IsNullOrWhiteSpace(channel.Url))
            {
                return $"drift.notify[{i}] ({channel.Type}) has no url. Use \"env:NAME\" to read it from an environment variable.";
            }

            if (!channel.Url.StartsWith(DriftChannel.EnvPrefix, StringComparison.Ordinal) &&
                !Uri.TryCreate(channel.Url, UriKind.Absolute, out _))
            {
                return $"drift.notify[{i}].url is not an absolute URL or an \"env:NAME\" reference.";
            }
        }

        foreach (var rule in Ignore)
        {
            if (string.IsNullOrWhiteSpace(rule) || rule.StartsWith(':') || rule.EndsWith(':'))
            {
                return $"drift.ignore has an invalid entry '{rule}'; use a service name or service:field.";
            }
        }

        return null;
    }
}

/// <summary>Where a drift alert is sent.</summary>
public enum DriftChannelType
{
    /// <summary>A Slack incoming webhook.</summary>
    Slack,

    /// <summary>A Microsoft Teams workflow ("Post to a channel when a webhook request is received"); gets an Adaptive Card.</summary>
    Teams,

    /// <summary>Any URL: gets the alert as JSON (see the drift guide for its shape), for PagerDuty, Opsgenie or your own handler.</summary>
    Webhook
}

/// <summary>One place drift alerts go.</summary>
public sealed class DriftChannel
{
    /// <summary>Prefix of a <see cref="Url"/> or header value read from an environment variable.</summary>
    public const string EnvPrefix = "env:";

    public DriftChannelType Type { get; set; } = DriftChannelType.Webhook;

    /// <summary>
    /// The webhook URL. Webhook URLs are secrets (anyone with one can post), and deployify-state.json
    /// is committed, so use <c>"env:NAME"</c> to read it from environment variable NAME.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Extra request headers (<see cref="DriftChannelType.Webhook"/> only). Values can be <c>"env:NAME"</c> too.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>A name for messages and the JSON output; defaults to the type.</summary>
    public string? Name { get; set; }

    /// <summary>The name shown for this channel.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Type.ToString().ToLowerInvariant() : Name;
}
