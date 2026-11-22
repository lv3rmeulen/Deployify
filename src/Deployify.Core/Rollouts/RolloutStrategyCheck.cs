using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;

namespace Deployify.Core.Rollouts;

/// <summary>
/// Checks, before anything is deployed, that every service's rollout strategy can run: the
/// settings are valid, the target supports the strategy, and (for strategies Deployify drives)
/// the deploy goes through a health-gated rollout. A strategy that can't run stops the deploy:
/// it never falls back to a rolling update without saying so.
/// </summary>
public static class RolloutStrategyCheck
{
    /// <summary>
    /// Problems with the environment's rollout strategies, each a full sentence; empty when the
    /// deploy can go ahead. <paramref name="healthGate"/> is false when the health gate was
    /// switched off (<c>--no-health-gate</c>).
    /// </summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(
        IDeploymentTarget target, AspireManifest manifest, EnvironmentConfig config, bool healthGate,
        CancellationToken ct = default)
    {
        var services = (await target.ExplainAsync(manifest, config, ct)).Select(e => e.Name).ToList();
        var errors = RolloutStrategies.Validate(config, services).ToList();
        if (errors.Count > 0)
        {
            return errors;
        }

        var progressive = services
            .Select(s => (Service: s, Strategy: RolloutStrategies.For(config, s)))
            .Where(p => p.Strategy.IsProgressive)
            .ToList();
        if (progressive.Count == 0)
        {
            return [];
        }

        var names = string.Join(", ", progressive.Select(p => $"{p.Service} ({Name(p.Strategy.Kind)})"));
        if (target.GetCapability<IRolloutStrategyTarget>() is not { } strategies)
        {
            return
            [
                $"{target.DisplayName} can't do canary or blue-green rollouts, but {names} ask for one. " +
                "Set rollout.strategy to Rolling for this environment."
            ];
        }

        if (strategies.StrategyMode == RolloutStrategyMode.Driven)
        {
            if (target.GetCapability<ITrafficShiftingTarget>() is not { } shifting)
            {
                return [$"{target.DisplayName} reports canary and blue-green support but can't shift traffic; {names} can't be deployed."];
            }

            if (!healthGate)
            {
                return
                [
                    $"{names} use a strategy that Deployify runs as a health-gated rollout, so it can't be " +
                    "combined with --no-health-gate. Drop the flag, or set rollout.strategy to Rolling."
                ];
            }

            if (await shifting.GetUnavailableReasonAsync(config, ct) is { } unavailable)
            {
                return [$"{names} need a health-gated rollout, which can't run here: {unavailable}."];
            }
        }

        foreach (var (service, strategy) in progressive)
        {
            if (await strategies.GetStrategyUnsupportedReasonAsync(manifest, config, service, strategy, ct) is { } reason)
            {
                errors.Add($"{service}: {Name(strategy.Kind)} on {target.DisplayName}: {reason}");
            }
        }

        return errors;
    }

    internal static string Name(RolloutStrategyKind kind) => kind switch
    {
        RolloutStrategyKind.Canary => "canary",
        RolloutStrategyKind.BlueGreen => "blue-green",
        _ => "rolling"
    };
}
