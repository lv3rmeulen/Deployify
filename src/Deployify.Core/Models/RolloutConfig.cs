using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace Deployify.Core.Models;

/// <summary>How a new version of a service replaces the running one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RolloutStrategyKind>))]
public enum RolloutStrategyKind
{
    /// <summary>The platform's own rolling update: old instances are replaced a few at a time. The default.</summary>
    Rolling,

    /// <summary>
    /// The new version runs next to the old one and gets a growing share of the traffic, step by
    /// step, while its health is watched. Any failure sends all traffic back to the old version.
    /// </summary>
    Canary,

    /// <summary>
    /// The new version is started in full next to the old one without traffic, checked, and then
    /// gets all the traffic at once. The old version is kept for a while so going back is instant.
    /// </summary>
    BlueGreen
}

/// <summary>
/// <c>rollout</c> section of an environment (or of one service in <c>serviceOverrides</c>) in
/// deployify-state.json. A service override replaces the environment's section as a whole.
/// </summary>
public sealed class RolloutConfig
{
    /// <summary>Rolling (default), Canary or BlueGreen.</summary>
    public RolloutStrategyKind Strategy { get; set; } = RolloutStrategyKind.Rolling;

    /// <summary>Steps of a canary rollout. Only read when <see cref="Strategy"/> is Canary.</summary>
    public CanaryConfig? Canary { get; set; }

    /// <summary>Timing of a blue-green rollout. Only read when <see cref="Strategy"/> is BlueGreen.</summary>
    public BlueGreenConfig? BlueGreen { get; set; }
}

public sealed class CanaryConfig
{
    /// <summary>
    /// Traffic steps, in order. Each sends <see cref="CanaryStep.Weight"/> percent of the traffic to
    /// the new version and then waits <see cref="CanaryStep.Pause"/> while watching its health. After
    /// the last step the new version gets all the traffic. Empty means one step: 10% for 5 minutes.
    /// </summary>
    public List<CanaryStep> Steps { get; set; } = [];
}

public sealed class CanaryStep
{
    /// <summary>Percentage of the traffic the new version gets in this step, 1 to 99.</summary>
    public int Weight { get; set; }

    /// <summary>How long to stay at this weight: <c>30s</c>, <c>5m</c>, <c>1h30m</c>. Defaults to 5m.</summary>
    public string? Pause { get; set; }
}

public sealed class BlueGreenConfig
{
    /// <summary>
    /// How long the new version runs without traffic before the switch, so it can be tried out at
    /// its preview address. Defaults to 0: switch as soon as it is healthy.
    /// </summary>
    public string? PreviewPause { get; set; }

    /// <summary>
    /// How long the old version is kept after the switch while the new one is watched. A failure
    /// in that time switches straight back. Defaults to 1m.
    /// </summary>
    public string? ScaleDownDelay { get; set; }
}

/// <summary>One canary step with its pause parsed.</summary>
public sealed record RolloutStep(int Weight, TimeSpan Pause);

/// <summary>
/// The strategy one service is deployed with, with defaults filled in and durations parsed.
/// Get it from <see cref="RolloutStrategies.For"/>.
/// </summary>
public sealed record RolloutStrategy(
    RolloutStrategyKind Kind,
    IReadOnlyList<RolloutStep> Steps,
    TimeSpan PreviewPause,
    TimeSpan ScaleDownDelay)
{
    public static readonly TimeSpan DefaultStepPause = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultScaleDownDelay = TimeSpan.FromMinutes(1);
    public const int DefaultCanaryWeight = 10;

    public static RolloutStrategy Rolling { get; } = new(RolloutStrategyKind.Rolling, [], TimeSpan.Zero, TimeSpan.Zero);

    public bool IsProgressive => Kind != RolloutStrategyKind.Rolling;

    /// <summary>The time the strategy spends waiting on purpose: every pause and delay added up.</summary>
    public TimeSpan WaitTime => Kind switch
    {
        RolloutStrategyKind.Canary => Steps.Aggregate(TimeSpan.Zero, (t, s) => t + s.Pause),
        RolloutStrategyKind.BlueGreen => PreviewPause + ScaleDownDelay,
        _ => TimeSpan.Zero
    };

    /// <summary>A short description: <c>canary 10% 5m → 50% 5m → 100%</c>, <c>blue-green (keep old 1m)</c>.</summary>
    public string Describe() => Kind switch
    {
        RolloutStrategyKind.Canary =>
            "canary " + string.Join(" → ", Steps.Select(s => $"{s.Weight}% {Durations.Format(s.Pause)}")) + " → 100%",
        RolloutStrategyKind.BlueGreen =>
            "blue-green (" + (PreviewPause > TimeSpan.Zero ? $"preview {Durations.Format(PreviewPause)}, " : "") +
            $"keep old {Durations.Format(ScaleDownDelay)})",
        _ => "rolling"
    };
}

/// <summary>Reads the rollout strategy of each service from an environment's settings.</summary>
public static class RolloutStrategies
{
    /// <summary>The settings that apply to <paramref name="service"/>: its override, else the environment's.</summary>
    public static RolloutConfig? ConfigFor(EnvironmentConfig config, string service)
        => config.ServiceOverrides.TryGetValue(service, out var o) && o.Rollout is { } own ? own : config.Rollout;

    /// <summary>
    /// The strategy <paramref name="service"/> is deployed with. Throws <see cref="FormatException"/>
    /// with a message naming the setting when the settings are invalid; <see cref="Validate"/>
    /// reports every problem at once instead.
    /// </summary>
    public static RolloutStrategy For(EnvironmentConfig config, string service)
    {
        var errors = new List<string>();
        var strategy = Resolve(ConfigFor(config, service), Where(config, service), errors);
        return errors.Count == 0 ? strategy : throw new FormatException(errors[0]);
    }

    /// <summary>True when any of <paramref name="services"/> uses Canary or BlueGreen.</summary>
    public static bool AnyProgressive(EnvironmentConfig config, IEnumerable<string> services)
        => services.Any(s => ConfigFor(config, s) is { Strategy: not RolloutStrategyKind.Rolling });

    /// <summary>Every problem with the rollout settings of <paramref name="services"/>, or none.</summary>
    public static IReadOnlyList<string> Validate(EnvironmentConfig config, IEnumerable<string> services)
    {
        var errors = new List<string>();
        if (config.Rollout is { } env)
        {
            Resolve(env, "rollout", errors);
        }

        foreach (var service in services)
        {
            if (config.ServiceOverrides.TryGetValue(service, out var o) && o.Rollout is { } own)
            {
                Resolve(own, Where(config, service), errors);
            }
        }

        return [.. errors.Distinct()];
    }

    private static string Where(EnvironmentConfig config, string service)
        => config.ServiceOverrides.TryGetValue(service, out var o) && o.Rollout is not null
            ? $"serviceOverrides.{service}.rollout"
            : "rollout";

    private static RolloutStrategy Resolve(RolloutConfig? cfg, string where, List<string> errors)
    {
        if (cfg is null)
        {
            return RolloutStrategy.Rolling;
        }

        switch (cfg.Strategy)
        {
            case RolloutStrategyKind.Canary:
            {
                var configured = cfg.Canary?.Steps ?? [];
                var steps = new List<RolloutStep>();
                if (configured.Count == 0)
                {
                    steps.Add(new RolloutStep(RolloutStrategy.DefaultCanaryWeight, RolloutStrategy.DefaultStepPause));
                }

                for (var i = 0; i < configured.Count; i++)
                {
                    var step = configured[i];
                    var at = $"{where}.canary.steps[{i}]";
                    if (step.Weight is < 1 or > 99)
                    {
                        errors.Add($"{at}.weight is {step.Weight}: use 1 to 99. The new version gets 100% after the last step.");
                    }
                    else if (steps.Count > 0 && step.Weight <= steps[^1].Weight)
                    {
                        errors.Add($"{at}.weight ({step.Weight}%) must be higher than the step before it ({steps[^1].Weight}%).");
                    }

                    steps.Add(new RolloutStep(step.Weight, Duration(step.Pause, RolloutStrategy.DefaultStepPause, $"{at}.pause", errors)));
                }

                return new RolloutStrategy(RolloutStrategyKind.Canary, steps, TimeSpan.Zero, TimeSpan.Zero);
            }

            case RolloutStrategyKind.BlueGreen:
                return new RolloutStrategy(RolloutStrategyKind.BlueGreen, [],
                    Duration(cfg.BlueGreen?.PreviewPause, TimeSpan.Zero, $"{where}.blueGreen.previewPause", errors),
                    Duration(cfg.BlueGreen?.ScaleDownDelay, RolloutStrategy.DefaultScaleDownDelay, $"{where}.blueGreen.scaleDownDelay", errors));

            default:
                return RolloutStrategy.Rolling;
        }
    }

    private static TimeSpan Duration(string? text, TimeSpan fallback, string where, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (Durations.TryParse(text, out var value))
        {
            return value;
        }

        errors.Add($"{where} is '{text}': use a duration such as 30s, 5m or 1h30m.");
        return fallback;
    }
}

/// <summary>Durations as written in deployify-state.json: <c>0</c>, <c>45s</c>, <c>5m</c>, <c>1h30m</c>.</summary>
public static class Durations
{
    public static bool TryParse(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text == "0")
        {
            return true;
        }

        var i = 0;
        var total = TimeSpan.Zero;
        while (i < text.Length)
        {
            var start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }

            if (i == start || i == text.Length ||
                !int.TryParse(text.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            TimeSpan part;
            switch (char.ToLowerInvariant(text[i++]))
            {
                case 's':
                    part = TimeSpan.FromSeconds(value);
                    break;
                case 'm':
                    part = TimeSpan.FromMinutes(value);
                    break;
                case 'h':
                    part = TimeSpan.FromHours(value);
                    break;
                default:
                    return false;
            }

            total += part;
        }

        duration = total;
        return true;
    }

    /// <summary><c>90s</c> → <c>1m30s</c>; zero is <c>0s</c>.</summary>
    public static string Format(TimeSpan t)
    {
        if (t <= TimeSpan.Zero)
        {
            return "0s";
        }

        var parts = new List<string>();
        if ((int)t.TotalHours > 0)
        {
            parts.Add($"{(int)t.TotalHours}h");
        }

        if (t.Minutes > 0)
        {
            parts.Add($"{t.Minutes}m");
        }

        if (t.Seconds > 0)
        {
            parts.Add($"{t.Seconds}s");
        }

        return parts.Count == 0 ? "0s" : string.Concat(parts);
    }
}
