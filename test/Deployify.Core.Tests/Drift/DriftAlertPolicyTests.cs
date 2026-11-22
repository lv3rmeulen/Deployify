using System;
using Deployify.Core.Drift;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Drift;

/// <summary>
/// Which alert a check calls for: one when drift appears, changes or is resolved, reminders only
/// when asked for, and nothing at all for the same drift on every scheduled run.
/// </summary>
public sealed class DriftAlertPolicyTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private static readonly DriftEntry s_api = new("api", DriftKind.Changed, "image", "api:hotfix", "api:4f2c9e1", null);
    private static readonly DriftEntry s_worker = new("worker", DriftKind.Missing, null, null, null, null);

    private static DriftCheck Check(DriftStatus status, params DriftEntry[] entries) => new()
    {
        Environment = "prod",
        Status = status,
        CheckedAt = s_t0,
        Entries = entries
    };

    private static DriftStatusRecord Stored(DriftStatus status, string fingerprint = "", DateTimeOffset? notifiedAt = null) => new()
    {
        Status = status,
        Fingerprint = fingerprint,
        Since = s_t0.AddDays(-1),
        CheckedAt = s_t0.AddHours(-1),
        NotifiedAt = notifiedAt
    };

    private static readonly DriftConfig s_defaults = new();

    [Fact]
    public void ANewDrift_Alerts()
    {
        DriftAlertPolicy.Decide(null, Check(DriftStatus.Drifted, s_api), s_defaults, s_t0).ShouldBe(DriftEvent.Drifted);
        DriftAlertPolicy.Decide(Stored(DriftStatus.InSync), Check(DriftStatus.Drifted, s_api), s_defaults, s_t0).ShouldBe(DriftEvent.Drifted);
    }

    [Fact]
    public void TheSameDrift_DoesNotAlertAgain_UnlessARemindIsDue()
    {
        var check = Check(DriftStatus.Drifted, s_api);
        var stored = Stored(DriftStatus.Drifted, check.Fingerprint, notifiedAt: s_t0.AddHours(-5));

        DriftAlertPolicy.Decide(stored, check, s_defaults, s_t0).ShouldBeNull();
        DriftAlertPolicy.Decide(stored, check, new DriftConfig { RemindAfterHours = 6 }, s_t0).ShouldBeNull();
        DriftAlertPolicy.Decide(stored, check, new DriftConfig { RemindAfterHours = 4 }, s_t0).ShouldBe(DriftEvent.Reminder);
    }

    [Fact]
    public void ADriftThatChanged_Alerts()
    {
        var stored = Stored(DriftStatus.Drifted, Check(DriftStatus.Drifted, s_api).Fingerprint, s_t0.AddHours(-1));
        DriftAlertPolicy.Decide(stored, Check(DriftStatus.Drifted, s_api, s_worker), s_defaults, s_t0).ShouldBe(DriftEvent.Changed);
    }

    [Fact]
    public void Resolved_OnlyAfterAnAlert_AndOnlyWhenWanted()
    {
        var notified = Stored(DriftStatus.Drifted, "x", s_t0.AddHours(-1));
        var silent = Stored(DriftStatus.Drifted, "x");

        DriftAlertPolicy.Decide(notified, Check(DriftStatus.InSync), s_defaults, s_t0).ShouldBe(DriftEvent.Resolved);
        DriftAlertPolicy.Decide(silent, Check(DriftStatus.InSync), s_defaults, s_t0).ShouldBeNull();
        DriftAlertPolicy.Decide(notified, Check(DriftStatus.InSync), new DriftConfig { NotifyOnResolve = false }, s_t0).ShouldBeNull();
        DriftAlertPolicy.Decide(Stored(DriftStatus.InSync, notifiedAt: s_t0), Check(DriftStatus.InSync), s_defaults, s_t0).ShouldBeNull();
    }

    [Fact]
    public void AFailingCheck_AlertsOnce_UnlessErrorsAreOff()
    {
        var error = Check(DriftStatus.Error) with { Reason = "unreachable" };

        DriftAlertPolicy.Decide(Stored(DriftStatus.InSync), error, s_defaults, s_t0).ShouldBe(DriftEvent.Failing);
        DriftAlertPolicy.Decide(Stored(DriftStatus.Error, notifiedAt: s_t0.AddHours(-1)), error, s_defaults, s_t0).ShouldBeNull();
        DriftAlertPolicy.Decide(Stored(DriftStatus.Unknown), error, s_defaults, s_t0).ShouldBeNull();
        DriftAlertPolicy.Decide(null, error, new DriftConfig { NotifyOnError = false }, s_t0).ShouldBeNull();

        // Working again after an alert about it.
        DriftAlertPolicy.Decide(Stored(DriftStatus.Error, notifiedAt: s_t0.AddHours(-1)), Check(DriftStatus.InSync), s_defaults, s_t0)
            .ShouldBe(DriftEvent.Resolved);
    }

    [Fact]
    public void SkippedAndNotDeployed_NeverAlert_NorChangeTheRecord()
    {
        var stored = Stored(DriftStatus.Drifted, "x", s_t0);
        foreach (var status in new[] { DriftStatus.Skipped, DriftStatus.NotDeployed })
        {
            DriftAlertPolicy.Decide(stored, Check(status), s_defaults, s_t0).ShouldBeNull();
            DriftAlertPolicy.Next(stored, Check(status), null, false, true, s_t0).ShouldBeNull();
        }
    }

    [Fact]
    public void Next_KeepsWhenTheStatusBegan_AndRecordsTheAlert()
    {
        var check = Check(DriftStatus.Drifted, s_api);
        var first = DriftAlertPolicy.Next(Stored(DriftStatus.InSync), check, DriftEvent.Drifted, delivered: true, anyChannels: true, s_t0)!;
        first.Since.ShouldBe(s_t0);
        first.NotifiedAt.ShouldBe(s_t0);
        first.NotifiedEvent.ShouldBe(DriftEvent.Drifted);
        first.Fingerprint.ShouldBe(check.Fingerprint);

        var later = DriftAlertPolicy.Next(first, check, null, false, true, s_t0.AddHours(1))!;
        later.Since.ShouldBe(s_t0);
        later.NotifiedAt.ShouldBe(s_t0);
        later.CheckedAt.ShouldBe(s_t0.AddHours(1));
    }

    [Fact]
    public void Next_DoesNotRecordAnAlertNoChannelAccepted_SoTheNextRunSendsItAgain()
    {
        var previous = Stored(DriftStatus.InSync);
        var check = Check(DriftStatus.Drifted, s_api);

        var next = DriftAlertPolicy.Next(previous, check, DriftEvent.Drifted, delivered: false, anyChannels: true, s_t0)!;

        next.Status.ShouldBe(DriftStatus.InSync);
        DriftAlertPolicy.Decide(next, check, s_defaults, s_t0).ShouldBe(DriftEvent.Drifted);
        DriftAlertPolicy.Next(null, check, DriftEvent.Drifted, delivered: false, anyChannels: true, s_t0).ShouldBeNull();
    }

    [Fact]
    public void Next_WithoutChannels_RecordsTheStatus_ButNoAlert()
    {
        var next = DriftAlertPolicy.Next(null, Check(DriftStatus.Drifted, s_api), DriftEvent.Drifted, delivered: false, anyChannels: false, s_t0)!;

        next.Status.ShouldBe(DriftStatus.Drifted);
        next.NotifiedAt.ShouldBeNull();
    }
}
