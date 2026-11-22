using System;
using System.Globalization;
using System.Linq;
using Deployify.Core.Models;

namespace Deployify.Target.AwsEcs;

/// <summary>
/// Maps Deployify's rollout strategies to ECS's built-in deployment strategies, which ECS runs
/// itself: BLUE_GREEN, CANARY (one step) and LINEAR (equal steps). Traffic shifting needs Service
/// Connect, the only way Deployify connects ECS services; bake times are whole minutes.
/// </summary>
public static class EcsStrategies
{
    /// <summary>Why ECS can't run <paramref name="strategy"/> for a service, or null when it can.</summary>
    public static string? UnsupportedReason(RolloutStrategy strategy, bool serviceConnect)
    {
        switch (strategy.Kind)
        {
            case RolloutStrategyKind.BlueGreen:
                if (strategy.PreviewPause > TimeSpan.Zero)
                {
                    return "ECS only waits before the switch through deployment lifecycle hooks, which Deployify doesn't set up. " +
                           "Set blueGreen.previewPause to 0 (ECS still keeps the old version for scaleDownDelay).";
                }

                return Minutes(strategy.ScaleDownDelay, "blueGreen.scaleDownDelay");

            case RolloutStrategyKind.Canary:
                if (!serviceConnect)
                {
                    return "ECS shifts canary traffic through Service Connect or a load balancer, and this service uses neither. " +
                           "Set aws.serviceConnectNamespace (the service needs a port), or use BlueGreen.";
                }

                if (strategy.Steps.Select(s => Minutes(s.Pause, "canary.steps[].pause")).FirstOrDefault(r => r is not null) is { } bad)
                {
                    return bad;
                }

                if (strategy.Steps.Count == 1 || IsLinear(strategy))
                {
                    return null;
                }

                return "ECS runs either one canary step (CANARY) or equal steps with equal pauses that go all the way up " +
                       "(LINEAR: 20%, 40%, 60%, 80%). Change the steps to one of those.";

            default:
                return null;
        }
    }

    /// <summary>The <c>--deployment-configuration</c> shorthand for a strategy <see cref="UnsupportedReason"/> accepts.</summary>
    public static string Shorthand(RolloutStrategy strategy)
    {
        switch (strategy.Kind)
        {
            case RolloutStrategyKind.BlueGreen:
                return $"strategy=BLUE_GREEN,bakeTimeInMinutes={Whole(strategy.ScaleDownDelay)}";
            case RolloutStrategyKind.Canary when strategy.Steps.Count == 1:
                var step = strategy.Steps[0];
                return "strategy=CANARY,bakeTimeInMinutes=0," +
                       $"canaryConfiguration={{canaryPercent={Percent(step.Weight)},canaryBakeTimeInMinutes={Whole(step.Pause)}}}";
            case RolloutStrategyKind.Canary:
                var first = strategy.Steps[0];
                return "strategy=LINEAR,bakeTimeInMinutes=0," +
                       $"linearConfiguration={{stepPercent={Percent(first.Weight)},stepBakeTimeInMinutes={Whole(first.Pause)}}}";
            default:
                return "strategy=ROLLING";
        }
    }

    /// <summary>Steps w, 2w, 3w… up to below 100, all with the same pause.</summary>
    internal static bool IsLinear(RolloutStrategy strategy)
    {
        var steps = strategy.Steps;
        if (steps.Count < 2)
        {
            return false;
        }

        var w = steps[0].Weight;
        var expected = Enumerable.Range(1, (100 - 1) / w).Select(k => k * w).ToList();
        return steps.Select(s => s.Weight).SequenceEqual(expected) && steps.All(s => s.Pause == steps[0].Pause);
    }

    private static string? Minutes(TimeSpan t, string setting)
        => t.Ticks % TimeSpan.TicksPerMinute != 0 || t > TimeSpan.FromMinutes(1440)
            ? $"ECS bake times are whole minutes up to 24h; {setting} is {Durations.Format(t)}."
            : null;

    private static string Whole(TimeSpan t) => ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture);

    private static string Percent(int weight) => weight.ToString("0.0", CultureInfo.InvariantCulture);
}
