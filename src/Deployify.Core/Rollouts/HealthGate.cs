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

/// <summary>How a gate ended. <see cref="Revision"/> is set when the platform reverted, to what it went back to.</summary>
public sealed record GateResult(GateOutcome Outcome, string? Detail, string? Revision = null);

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
    public static Task<GateResult> WaitAsync(
        IRolloutTarget target,
        EnvironmentConfig config,
        string service,
        string revision,
        TimeSpan timeout,
        RolloutOptions options,
        Action<HealthObservation>? onObservation = null,
        CancellationToken ct = default)
        => WaitAsync(probe => target.ObserveAsync(config, service, revision, probe), timeout, options, onObservation, ct);

    /// <summary>
    /// Polls <paramref name="observe"/> (one look, cancelled after <see cref="RolloutOptions.ProbeTimeout"/>)
    /// until it has been healthy <see cref="RolloutOptions.RequiredSuccesses"/> times in a row,
    /// fails, or <paramref name="timeout"/> passes. Cancelling <paramref name="ct"/> throws.
    /// </summary>
    public static async Task<GateResult> WaitAsync(
        Func<CancellationToken, Task<HealthObservation>> observe,
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
                var observation = await ObserveOnceAsync(observe, options, deadline.Token);
                last = observation;
                onObservation?.Invoke(observation);

                switch (observation.State)
                {
                    case HealthState.Failed:
                        return new GateResult(GateOutcome.Failed, observation.Detail);
                    case HealthState.RevertedByPlatform:
                        return new GateResult(GateOutcome.RevertedByPlatform, observation.Detail, observation.Revision);
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

    /// <summary>
    /// Watches a revision that is already healthy for <paramref name="duration"/> (a canary step's
    /// pause, a blue-green delay). It must stay healthy: a failed observation, or
    /// <see cref="RolloutOptions.RequiredSuccesses"/> unhealthy ones in a row, ends the watch with
    /// that result. Returns a <see cref="GateOutcome.Healthy"/> result when the time is up.
    /// Cancelling <paramref name="ct"/> throws.
    /// </summary>
    public static async Task<GateResult> WatchAsync(
        Func<CancellationToken, Task<HealthObservation>> observe,
        TimeSpan duration,
        RolloutOptions options,
        Action<HealthObservation>? onObservation = null,
        CancellationToken ct = default)
    {
        var until = DateTimeOffset.UtcNow + duration;
        var tolerated = Math.Max(1, options.RequiredSuccesses);
        var unhealthy = 0;
        HealthObservation? last = null;

        while (true)
        {
            var left = until - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return new GateResult(GateOutcome.Healthy, last?.Detail);
            }

            await Task.Delay(left < options.PollInterval ? left : options.PollInterval, ct);
            var observation = await ObserveOnceAsync(observe, options, ct);
            last = observation;
            onObservation?.Invoke(observation);

            switch (observation.State)
            {
                case HealthState.Failed:
                    return new GateResult(GateOutcome.Failed, observation.Detail);
                case HealthState.RevertedByPlatform:
                    return new GateResult(GateOutcome.RevertedByPlatform, observation.Detail, observation.Revision);
                case HealthState.Healthy:
                    unhealthy = 0;
                    break;
                default:
                    if (++unhealthy >= tolerated)
                    {
                        return new GateResult(GateOutcome.Failed, $"stopped being healthy: {observation.Detail ?? "no detail"}");
                    }

                    break;
            }
        }
    }

    private static async Task<HealthObservation> ObserveOnceAsync(
        Func<CancellationToken, Task<HealthObservation>> observe, RolloutOptions options, CancellationToken deadline)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        probe.CancelAfter(options.ProbeTimeout);
        try
        {
            return await observe(probe.Token);
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
