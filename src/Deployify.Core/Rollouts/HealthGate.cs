using System;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;

namespace Deployify.Core.Rollouts;

public enum GateOutcome
{
    Healthy,
    Failed,
    TimedOut,
    RevertedByPlatform
}

public sealed record GateResult(GateOutcome Outcome, string? Detail);

/// <summary>
/// Polls <see cref="IRolloutTarget.ObserveAsync"/> until a revision has been healthy
/// <see cref="RolloutOptions.RequiredSuccesses"/> times in a row, fails, or runs out of time.
/// </summary>
public static class HealthGate
{
    /// <summary>
    /// Waits for <paramref name="revision"/> of <paramref name="service"/>. Running past
    /// <paramref name="timeout"/> returns <see cref="GateOutcome.TimedOut"/>; cancelling
    /// <paramref name="ct"/> throws. A failing or slow observation counts as "still progressing".
    /// </summary>
    public static async Task<GateResult> WaitAsync(
        IRolloutTarget target,
        EnvironmentConfig config,
        string service,
        string revision,
        TimeSpan timeout,
        RolloutOptions options,
        Action<HealthObservation>? onObservation = null,
        CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var required = Math.Max(1, options.RequiredSuccesses);
        var consecutive = 0;
        HealthObservation? last = null;

        try
        {
            while (true)
            {
                var observation = await ObserveOnceAsync(target, config, service, revision, options, deadline.Token);
                last = observation;
                onObservation?.Invoke(observation);

                switch (observation.State)
                {
                    case HealthState.Failed:
                        return new GateResult(GateOutcome.Failed, observation.Detail);
                    case HealthState.RevertedByPlatform:
                        return new GateResult(GateOutcome.RevertedByPlatform, observation.Detail);
                    case HealthState.Healthy:
                        consecutive++;
                        if (consecutive >= required)
                        {
                            return new GateResult(GateOutcome.Healthy, observation.Detail);
                        }

                        break;
                    default:
                        consecutive = 0;
                        break;
                }

                await Task.Delay(options.PollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var detail = $"not healthy after {Format(timeout)}";
            return new GateResult(GateOutcome.TimedOut, last?.Detail is { } d ? $"{detail} (last seen: {d})" : detail);
        }
    }

    private static async Task<HealthObservation> ObserveOnceAsync(
        IRolloutTarget target, EnvironmentConfig config, string service, string revision,
        RolloutOptions options, CancellationToken deadline)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        probe.CancelAfter(options.ProbeTimeout);
        try
        {
            return await target.ObserveAsync(config, service, revision, probe.Token);
        }
        catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
        {
            return HealthObservation.Progressing($"health check took longer than {Format(options.ProbeTimeout)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthObservation.Progressing($"health check failed: {ex.Message}");
        }
    }

    internal static string Format(TimeSpan t)
        => t.TotalMinutes >= 1 && t.Seconds == 0 ? $"{(int)t.TotalMinutes}m"
            : t.TotalSeconds >= 1 ? $"{(int)t.TotalSeconds}s"
            : $"{(int)t.TotalMilliseconds}ms";
}
