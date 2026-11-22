using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.State;

namespace Deployify.Core.Drift;

/// <summary>Why an alert is sent.</summary>
public enum DriftEvent
{
    /// <summary>The environment drifted (it matched its deploy, or hadn't been checked, before).</summary>
    Drifted,

    /// <summary>It was already drifted, and what drifted has changed.</summary>
    Changed,

    /// <summary>It is still drifted (or still failing) the same way after <see cref="DriftConfig.RemindAfterHours"/>.</summary>
    Reminder,

    /// <summary>It matches its deploy again after an alert about it.</summary>
    Resolved,

    /// <summary>The check can't tell: the target is unreachable, or some services couldn't be compared.</summary>
    Failing
}

/// <summary>
/// The result of the last drift check of an environment and the last alert sent about it, kept at
/// <c>{prefix}/{env}/drift.json</c> so that each scheduled run alerts about changes, not about the
/// same drift over and over, whichever machine runs it.
/// </summary>
public sealed record DriftStatusRecord
{
    public required DriftStatus Status { get; init; }

    /// <summary>See <see cref="DriftCheck.Fingerprint"/>.</summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>When the environment entered <see cref="Status"/>.</summary>
    public required DateTimeOffset Since { get; init; }

    public required DateTimeOffset CheckedAt { get; init; }

    /// <summary>When an alert about the current status was last delivered; null when none was.</summary>
    public DateTimeOffset? NotifiedAt { get; init; }

    public DriftEvent? NotifiedEvent { get; init; }
}

/// <summary>Decides which alert, if any, a check calls for. Pure, so the rules can be tested on their own.</summary>
public static class DriftAlertPolicy
{
    /// <summary>
    /// The alert <paramref name="check"/> calls for, given the <paramref name="previous"/> one.
    /// Null when nothing is worth telling anyone: it is as it was, or it was never alerted about.
    /// </summary>
    public static DriftEvent? Decide(DriftStatusRecord? previous, DriftCheck check, DriftConfig config, DateTimeOffset now)
    {
        var failing = check.Status is DriftStatus.Error or DriftStatus.Unknown;
        var wasFailing = previous?.Status is DriftStatus.Error or DriftStatus.Unknown;

        switch (check.Status)
        {
            case DriftStatus.Drifted when previous?.Status != DriftStatus.Drifted:
                return DriftEvent.Drifted;
            case DriftStatus.Drifted when previous.Fingerprint != check.Fingerprint:
                return DriftEvent.Changed;
            case DriftStatus.Drifted:
                return ReminderDue(previous, config, now) ? DriftEvent.Reminder : null;

            case DriftStatus.Error or DriftStatus.Unknown when !config.NotifyOnError:
                return null;
            case DriftStatus.Error or DriftStatus.Unknown when !wasFailing:
                return DriftEvent.Failing;
            case DriftStatus.Error or DriftStatus.Unknown:
                return failing && ReminderDue(previous!, config, now) ? DriftEvent.Reminder : null;

            case DriftStatus.InSync:
                return config.NotifyOnResolve && previous is { NotifiedAt: not null, Status: not DriftStatus.InSync }
                    ? DriftEvent.Resolved
                    : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// What to store after <paramref name="check"/>. <paramref name="delivered"/>: the alert for
    /// <paramref name="alert"/> reached at least one channel. Null means keep the stored record as
    /// it is: the check didn't look (skipped, not deployed), or an alert was due and none got
    /// through, so the next check decides it again instead of losing it.
    /// </summary>
    public static DriftStatusRecord? Next(DriftStatusRecord? previous, DriftCheck check, DriftEvent? alert, bool delivered,
        bool anyChannels, DateTimeOffset now)
    {
        if (check.Status is DriftStatus.Skipped or DriftStatus.NotDeployed)
        {
            return null;
        }

        if (alert is not null && anyChannels && !delivered)
        {
            return previous is null ? null : previous with { CheckedAt = now };
        }

        var sameStatus = previous is not null && previous.Status == check.Status;
        var record = new DriftStatusRecord
        {
            Status = check.Status,
            Fingerprint = check.Fingerprint,
            Since = sameStatus ? previous!.Since : now,
            CheckedAt = now,
            NotifiedAt = sameStatus ? previous!.NotifiedAt : null,
            NotifiedEvent = sameStatus ? previous!.NotifiedEvent : null
        };

        return alert is not null && delivered
            ? record with { NotifiedAt = now, NotifiedEvent = alert }
            : record;
    }

    private static bool ReminderDue(DriftStatusRecord previous, DriftConfig config, DateTimeOffset now)
        => config.RemindAfterHours is { } hours && previous.NotifiedAt is { } at && now - at >= TimeSpan.FromHours(hours);
}

/// <summary>Reads and writes <see cref="DriftStatusRecord"/>.</summary>
public static class DriftStatusStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static string Key(StateSession state) => state.Root + "drift.json";

    public static async Task<DriftStatusRecord?> ReadAsync(StateSession state, CancellationToken ct = default)
    {
        var key = Key(state);
        if (await state.Backend.ReadAsync(key, ct) is not { } blob)
        {
            return null;
        }

        var what = $"State '{key}' in {state.Backend.Description}";
        var root = StateFormat.ParseObject(blob.Data, what);
        StateFormat.ReadVersion(root, StateFormat.VersionProperty, StateFormat.DriftStatus, what);
        try
        {
            return root.Deserialize<DriftStatusRecord>(s_json);
        }
        catch (JsonException ex)
        {
            throw new StateFormatException($"{what} is not a valid drift status: {ex.Message}", ex);
        }
    }

    public static Task WriteAsync(StateSession state, DriftStatusRecord record, CancellationToken ct = default)
    {
        var node = JsonSerializer.SerializeToNode(record, s_json)!.AsObject();
        var root = new System.Text.Json.Nodes.JsonObject { [StateFormat.VersionProperty] = StateFormat.DriftStatus };
        foreach (var (name, value) in node.ToList())
        {
            node.Remove(name);
            root[name] = value;
        }

        var data = JsonSerializer.SerializeToUtf8Bytes(root, s_json);
        return state.PutAsync(Key(state), _ => data, ct);
    }
}
