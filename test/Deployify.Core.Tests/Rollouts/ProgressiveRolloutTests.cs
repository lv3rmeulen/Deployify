using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Rollouts;

/// <summary>
/// A <see cref="FakeRolloutTarget"/> that can also run canary and blue-green rollouts. The
/// candidate of a service is "{name}:c"; promoting it rolls out "{name}:2".
/// </summary>
internal sealed class FakeShiftingTarget(FakeRolloutTarget inner, TrafficPrecision precision = TrafficPrecision.Exact)
    : ITrafficShiftingTarget
{
    public FakeRolloutTarget Inner { get; } = inner;
    public List<string> Calls => Inner.Calls;
    public string? Unsupported { get; set; }
    public string? PreviewUrl { get; set; }
    public RolloutStrategyMode StrategyMode { get; set; } = RolloutStrategyMode.Driven;
    public TrafficPrecision Precision { get; } = precision;

    /// <summary>Observations of a candidate, in order; the last one repeats.</summary>
    public Dictionary<string, Queue<HealthObservation>> CandidateHealth { get; } = [];

    public Task<string?> GetStrategyUnsupportedReasonAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, RolloutStrategy strategy, CancellationToken ct = default)
        => Task.FromResult(Unsupported);

    public Task<AppliedRevision> StartCandidateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service, RolloutStrategy strategy,
        ServiceSnapshot before, CancellationToken ct = default)
    {
        Calls.Add($"start {service}");
        return Task.FromResult(new AppliedRevision
        {
            ServiceName = service, Success = true, Revision = $"{service}:c", PreviewUrl = PreviewUrl
        });
    }

    public Task<HealthObservation> ObserveCandidateAsync(EnvironmentConfig config, string service, string candidate, CancellationToken ct = default)
    {
        Calls.Add($"observe-candidate {candidate}");
        if (CandidateHealth.TryGetValue(candidate, out var q) && q.Count > 0)
        {
            return Task.FromResult(q.Count > 1 ? q.Dequeue() : q.Peek());
        }

        return Task.FromResult(HealthObservation.Healthy("candidate ok"));
    }

    public Task<int> ShiftTrafficAsync(EnvironmentConfig config, string service, string candidate, int percent, CancellationToken ct = default)
    {
        Calls.Add($"shift {service} {percent}");
        return Task.FromResult(Precision == TrafficPrecision.Exact ? percent : 33);
    }

    public Task<string?> PromoteAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service, string candidate,
        CancellationToken ct = default)
    {
        Calls.Add($"promote {service}");
        return Task.FromResult<string?>($"{service}:2");
    }

    public Task FinishAsync(EnvironmentConfig config, string service, string candidate, CancellationToken ct = default)
    {
        Calls.Add($"finish {service}");
        return Task.CompletedTask;
    }

    // IRolloutTarget, delegated to the plain fake.
    public Task<ServiceSnapshot> CaptureAsync(AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
        => Inner.CaptureAsync(manifest, config, service, ct);

    public Task<AppliedRevision> ApplyServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service, CancellationToken ct = default)
        => Inner.ApplyServiceAsync(manifest, config, outputDirectory, service, ct);

    public Task<HealthObservation> ObserveAsync(EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
        => Inner.ObserveAsync(config, service, revision, ct);

    public Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
        => Inner.RestoreAsync(config, snapshot, ct);

    public Task<bool> HasNativeRollbackAsync(EnvironmentConfig config, CancellationToken ct = default)
        => Inner.HasNativeRollbackAsync(config, ct);

    /// <summary>A deployment target with <paramref name="services"/> whose capabilities are this fake.</summary>
    public IDeploymentTarget Target(params string[] services)
    {
        IReadOnlyList<ServiceExplanation> explanations =
            [.. services.Select(s => new ServiceExplanation { Name = s, Image = $"registry/{s}", ImageTag = "v1" })];
        var target = Substitute.For<IDeploymentTarget, ICapabilityProvider>();
        target.DisplayName.Returns("Fake");
        target.ExplainAsync(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>(), Arg.Any<CancellationToken>()).Returns(explanations);
        target.GetDependenciesAsync(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<ServiceDependency>)[]);
        ((ICapabilityProvider)target).GetCapability(Arg.Any<Type>())
            .Returns(call => ((Type)call[0]).IsInstanceOfType(this) ? this : null);
        return target;
    }
}

public sealed class ProgressiveRolloutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RolloutOptions Fast = new()
    {
        HealthTimeout = TimeSpan.FromSeconds(5),
        PollInterval = TimeSpan.FromMilliseconds(1),
        ProbeTimeout = TimeSpan.FromSeconds(2),
        RequiredSuccesses = 2,
        RollbackTimeout = TimeSpan.FromSeconds(10)
    };

    private static EnvironmentConfig Canary(params (int Weight, string Pause)[] steps) => new()
    {
        Rollout = new RolloutConfig
        {
            Strategy = RolloutStrategyKind.Canary,
            Canary = new CanaryConfig { Steps = [.. steps.Select(s => new CanaryStep { Weight = s.Weight, Pause = s.Pause })] }
        }
    };

    private static EnvironmentConfig BlueGreen(string? preview = null, string delay = "0") => new()
    {
        Rollout = new RolloutConfig
        {
            Strategy = RolloutStrategyKind.BlueGreen,
            BlueGreen = new BlueGreenConfig { PreviewPause = preview, ScaleDownDelay = delay }
        }
    };

    private static Task<RolloutResult> RunAsync(TempDir dir, FakeShiftingTarget fake, EnvironmentConfig config,
        params string[] services)
        => new RolloutEngine().RunAsync(new RolloutRequest
        {
            Target = fake.Target(services.Length == 0 ? ["api"] : services),
            Rollout = fake,
            Manifest = new AspireManifest(),
            Config = config,
            EnvironmentName = "prod",
            OutputDirectory = dir.Path,
            StateDirectory = dir.Path,
            Options = Fast
        }, Ct);

    [Fact]
    public async Task Canary_ShiftsEachStep_ThenPromotesAndFinishes()
    {
        using var dir = new TempDir();
        var fake = new FakeShiftingTarget(new FakeRolloutTarget()) { PreviewUrl = "https://preview" };

        var result = await RunAsync(dir, fake, Canary((10, "0"), (50, "0")));

        result.Outcome.ShouldBe(RolloutOutcome.Succeeded);
        var order = new[] { "start api", "shift api 10", "shift api 50", "promote api", "observe api:2", "finish api" }
            .Select(c => fake.Calls.IndexOf(c)).ToList();
        order.ShouldAllBe(i => i >= 0);
        order.ShouldBe(order.Order().ToList());
        fake.Calls.ShouldNotContain("apply api");
        var api = result.Services.Single();
        api.State.ShouldBe(ServiceRolloutState.Healthy);
        api.Candidate.ShouldBe("api:c");
        api.Revision.ShouldBe("api:2");
        api.TrafficPercent.ShouldBe(100);
        api.PreviewUrl.ShouldBe("https://preview");
        api.Strategy.ShouldBe("canary 10% 0s → 50% 0s → 100%");
    }

    [Fact]
    public async Task Canary_UnhealthyDuringStep_RestoresAndNeverPromotes()
    {
        using var dir = new TempDir();
        var fake = new FakeShiftingTarget(new FakeRolloutTarget());
        // Healthy twice for the start gate, twice after the shift, then it breaks during the pause.
        fake.CandidateHealth["api:c"] = new Queue<HealthObservation>(
        [
            HealthObservation.Healthy(), HealthObservation.Healthy(), HealthObservation.Healthy(), HealthObservation.Healthy(),
            HealthObservation.Failed("5xx spike")
        ]);

        var result = await RunAsync(dir, fake, Canary((20, "10s")));

        result.Outcome.ShouldBe(RolloutOutcome.RolledBack);
        result.Reason!.ShouldContain("5xx spike");
        result.Reason!.ShouldContain("20%");
        fake.Calls.ShouldContain("restore api");
        fake.Calls.ShouldNotContain("promote api");
        fake.Calls.ShouldNotContain("finish api");
    }

    [Fact]
    public async Task Canary_CandidateNeverHealthy_FailsBeforeAnyTraffic()
    {
        using var dir = new TempDir();
        var fake = new FakeShiftingTarget(new FakeRolloutTarget());
        fake.CandidateHealth["api:c"] = new Queue<HealthObservation>([HealthObservation.Failed("image pull failed")]);

        var result = await RunAsync(dir, fake, Canary((10, "0")));

        result.Outcome.ShouldBe(RolloutOutcome.RolledBack);
        fake.Calls.ShouldNotContain(c => c.StartsWith("shift"));
        result.Services.Single().State.ShouldBe(ServiceRolloutState.RolledBack);
    }

    [Fact]
    public async Task FixedPrecision_ShiftsOnce_AndReportsTheRealShare()
    {
        using var dir = new TempDir();
        var fake = new FakeShiftingTarget(new FakeRolloutTarget(), TrafficPrecision.Fixed);

        var result = await RunAsync(dir, fake, Canary((10, "0"), (50, "0")));

        result.Outcome.ShouldBe(RolloutOutcome.Succeeded);
        fake.Calls.Count(c => c.StartsWith("shift")).ShouldBe(1);
    }

    [Fact]
    public async Task BlueGreen_SwitchesAllTrafficAtOnce()
    {
        using var dir = new TempDir();
        var fake = new FakeShiftingTarget(new FakeRolloutTarget());

        var result = await RunAsync(dir, fake, BlueGreen());

        result.Outcome.ShouldBe(RolloutOutcome.Succeeded);
        fake.Calls.Where(c => c.StartsWith("shift")).ShouldBe(["shift api 100"]);
        fake.Calls.IndexOf("shift api 100").ShouldBeLessThan(fake.Calls.IndexOf("promote api"));
    }

    [Fact]
    public async Task NewService_IsDeployedDirectly()
    {
        using var dir = new TempDir();
        var inner = new FakeRolloutTarget();
        inner.Snapshots["api"] = new ServiceSnapshot { ServiceName = "api", Existed = false };
        var fake = new FakeShiftingTarget(inner);

        var result = await RunAsync(dir, fake, Canary((10, "0")));

        result.Outcome.ShouldBe(RolloutOutcome.Succeeded);
        fake.Calls.ShouldContain("apply api");
        fake.Calls.ShouldNotContain("start api");
    }

    [Fact]
    public async Task ServiceOverride_PicksTheStrategyPerService()
    {
        using var dir = new TempDir();
        var fake = new FakeShiftingTarget(new FakeRolloutTarget());
        var config = new EnvironmentConfig
        {
            ServiceOverrides =
            {
                ["api"] = new ServiceOverride { Rollout = new RolloutConfig { Strategy = RolloutStrategyKind.BlueGreen, BlueGreen = new() { ScaleDownDelay = "0" } } }
            }
        };

        var result = await RunAsync(dir, fake, config, "api", "worker");

        result.Outcome.ShouldBe(RolloutOutcome.Succeeded);
        fake.Calls.ShouldContain("start api");
        fake.Calls.ShouldContain("apply worker");
        fake.Calls.ShouldNotContain("start worker");
    }

    [Fact]
    public async Task UnsupportedStrategy_StopsBeforeAnythingChanges()
    {
        using var dir = new TempDir();
        var fake = new FakeShiftingTarget(new FakeRolloutTarget()) { Unsupported = "the service has no HTTP ingress" };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(dir, fake, Canary((10, "0"))));

        ex.Message.ShouldContain("no HTTP ingress");
        fake.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_TargetWithoutStrategies_RefusesCanary()
    {
        var target = FakeRolloutTarget.Target(["api"]);

        var errors = await RolloutStrategyCheck.CheckAsync(target, new AspireManifest(), Canary((10, "1m")), healthGate: true, Ct);

        errors.Single().ShouldContain("can't do canary or blue-green");
    }

    [Fact]
    public async Task Check_DrivenStrategyWithoutHealthGate_IsRefused()
    {
        var fake = new FakeShiftingTarget(new FakeRolloutTarget());

        var errors = await RolloutStrategyCheck.CheckAsync(fake.Target("api"), new AspireManifest(), Canary((10, "1m")), healthGate: false, Ct);

        errors.Single().ShouldContain("--no-health-gate");
    }

    [Fact]
    public async Task Check_NativeStrategyWithoutHealthGate_IsFine()
    {
        var fake = new FakeShiftingTarget(new FakeRolloutTarget()) { StrategyMode = RolloutStrategyMode.Native };

        var errors = await RolloutStrategyCheck.CheckAsync(fake.Target("api"), new AspireManifest(), BlueGreen(), healthGate: false, Ct);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_RollingNeedsNothing()
    {
        var errors = await RolloutStrategyCheck.CheckAsync(
            FakeRolloutTarget.Target(["api"]), new AspireManifest(), new EnvironmentConfig(), healthGate: false, Ct);

        errors.ShouldBeEmpty();
    }
}

public sealed class RolloutStrategiesTests
{
    [Fact]
    public void Canary_WithoutSteps_DefaultsToTenPercentForFiveMinutes()
    {
        var config = new EnvironmentConfig { Rollout = new RolloutConfig { Strategy = RolloutStrategyKind.Canary } };

        var strategy = RolloutStrategies.For(config, "api");

        strategy.Steps.ShouldBe([new RolloutStep(10, TimeSpan.FromMinutes(5))]);
        strategy.WaitTime.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void BlueGreen_Defaults()
    {
        var config = new EnvironmentConfig { Rollout = new RolloutConfig { Strategy = RolloutStrategyKind.BlueGreen } };

        var strategy = RolloutStrategies.For(config, "api");

        strategy.PreviewPause.ShouldBe(TimeSpan.Zero);
        strategy.ScaleDownDelay.ShouldBe(TimeSpan.FromMinutes(1));
        strategy.Describe().ShouldBe("blue-green (keep old 1m)");
    }

    [Fact]
    public void NoSection_IsRolling()
        => RolloutStrategies.For(new EnvironmentConfig(), "api").ShouldBe(RolloutStrategy.Rolling);

    [Fact]
    public void Validate_ReportsEveryBadSetting_WithItsPath()
    {
        var config = new EnvironmentConfig
        {
            Rollout = new RolloutConfig
            {
                Strategy = RolloutStrategyKind.Canary,
                Canary = new CanaryConfig { Steps = [new() { Weight = 50, Pause = "1m" }, new() { Weight = 20 }, new() { Weight = 100 }] }
            },
            ServiceOverrides =
            {
                ["api"] = new ServiceOverride
                {
                    Rollout = new RolloutConfig { Strategy = RolloutStrategyKind.BlueGreen, BlueGreen = new() { PreviewPause = "soon" } }
                }
            }
        };

        var errors = RolloutStrategies.Validate(config, ["api", "worker"]);

        errors.ShouldContain(e => e.StartsWith("rollout.canary.steps[1].weight") && e.Contains("higher"));
        errors.ShouldContain(e => e.StartsWith("rollout.canary.steps[2].weight is 100"));
        errors.ShouldContain(e => e.StartsWith("serviceOverrides.api.rollout.blueGreen.previewPause is 'soon'"));
        Should.Throw<FormatException>(() => RolloutStrategies.For(config, "worker"));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("45s", 45)]
    [InlineData("5m", 300)]
    [InlineData("1h30m", 5400)]
    [InlineData("2M10S", 130)]
    public void Durations_Parse(string text, int seconds)
    {
        Durations.TryParse(text, out var d).ShouldBeTrue();
        d.ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("m5")]
    [InlineData("5d")]
    [InlineData("-5m")]
    public void Durations_RejectNonsense(string text) => Durations.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void Durations_Format() => Durations.Format(TimeSpan.FromSeconds(5430)).ShouldBe("1h30m30s");
}
