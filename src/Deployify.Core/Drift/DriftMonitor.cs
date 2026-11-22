using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Deployments;
using Deployify.Core.Models;
using Deployify.Core.Services;

namespace Deployify.Core.Drift;

/// <summary>The outcome of one environment's drift run: the check, and the alert it led to.</summary>
public sealed record DriftRun(DriftCheck Check, DriftEvent? Event, IReadOnlyList<DriftDelivery> Deliveries, DriftStatus? PreviousStatus);

/// <summary>
/// One drift run for an environment: check it, decide whether that calls for an alert, send it,
/// and remember what was sent. Shared by <c>deployify drift</c> and anything else that schedules checks.
/// </summary>
public static class DriftMonitor
{
    /// <param name="notifier">Sends the alerts; null checks without alerting and without touching the stored status (<c>--no-notify</c>).</param>
    public static async Task<DriftRun> RunAsync(DeploymentSession session, IDriftNotifier? notifier,
        TimeProvider? time = null, CancellationToken ct = default)
    {
        time ??= TimeProvider.System;
        var config = session.Config.Drift ?? new DriftConfig();
        if (config.Validate() is { } problem)
        {
            var invalid = new DriftCheck
            {
                Environment = session.EnvName,
                Status = DriftStatus.Error,
                CheckedAt = time.GetUtcNow(),
                TargetId = session.TargetType.Value,
                TargetName = session.Target.DisplayName,
                Reason = problem
            };
            return new DriftRun(invalid, null, [], null);
        }

        var check = await DriftDetector.CheckAsync(session, time, ct);
        if (notifier is null)
        {
            return new DriftRun(check, null, [], null);
        }

        DriftStatusRecord? previous;
        try
        {
            previous = await DriftStatusStore.ReadAsync(session.State, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without the last result every run would alert; better to say so and alert as for a first check.
            session.Reporter.Warn($"Could not read the last drift check of {session.EnvName} ({ex.Message}); alerting as if it were the first.");
            previous = null;
        }

        var now = time.GetUtcNow();
        var alert = DriftAlertPolicy.Decide(previous, check, config, now);
        var deliveries = new List<DriftDelivery>();
        if (alert is { } e && config.Notify.Count > 0)
        {
            var since = previous is not null && previous.Status == check.Status ? previous.Since : now;
            var message = new DriftAlert(e, check, since, previous?.Status, ReleaseContext.Resolve().RunUrl);
            foreach (var channel in config.Notify)
            {
                deliveries.Add(await notifier.SendAsync(channel, message, ct));
            }
        }

        var delivered = deliveries.Exists(d => d.Ok);
        if (DriftAlertPolicy.Next(previous, check, alert, delivered, config.Notify.Count > 0, now) is { } next)
        {
            try
            {
                await DriftStatusStore.WriteAsync(session.State, next, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                session.Reporter.Warn($"Could not record the drift check of {session.EnvName} ({ex.Message}); the next run may alert again.");
            }
        }

        return new DriftRun(check, alert, deliveries, previous?.Status);
    }
}
