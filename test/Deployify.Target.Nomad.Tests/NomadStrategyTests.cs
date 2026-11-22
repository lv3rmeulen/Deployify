using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

public sealed class NomadStrategyTests
{
    private readonly JobGenerator _sut = new(NullLogger<JobGenerator>.Instance);

    private async Task<NomadJobJson> JobAsync(EnvironmentConfig config, int count = 1)
    {
        using var dir = new TempDir();
        config.ServiceOverrides["myapp"] = new ServiceOverride { Count = count, Rollout = config.Rollout };
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService(), config, TestContext.Current.CancellationToken);
        return await NomadJobJson.ReadAsync(await _sut.WriteJobAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken));
    }

    private static EnvironmentConfig With(RolloutConfig? rollout)
    {
        var config = Fixtures.NomadConfig();
        config.Rollout = rollout;
        return config;
    }

    [Fact]
    public async Task Rolling_HasNoCanaries()
    {
        var job = await JobAsync(With(null), count: 3);

        job.Group["Update"]!.AsObject().ContainsKey("Canary").ShouldBeFalse();
        job.Service!.ContainsKey("CanaryTags").ShouldBeFalse();
    }

    [Fact]
    public async Task Canary_AsksForCanariesFromTheFirstStep_AndPromotesItself()
    {
        var job = await JobAsync(With(new RolloutConfig
        {
            Strategy = RolloutStrategyKind.Canary,
            Canary = new CanaryConfig { Steps = [new() { Weight = 25, Pause = "1m" }, new() { Weight = 50 }] }
        }), count: 3);

        var update = job.Group["Update"]!.AsObject();
        ((int)update["Canary"]!).ShouldBe(1);
        ((bool)update["AutoPromote"]!).ShouldBeFalse();
        NomadJobJson.Strings(job.Service!["CanaryTags"]).ShouldBe([JobGenerator.CanaryTag]);
    }

    [Fact]
    public async Task BlueGreen_StartsAFullSecondSet()
    {
        var job = await JobAsync(With(new RolloutConfig { Strategy = RolloutStrategyKind.BlueGreen }), count: 4);

        ((int)job.Group["Update"]!["Canary"]!).ShouldBe(4);
    }

    [Theory]
    [InlineData(1, 10, 1)]
    [InlineData(3, 25, 1)]
    [InlineData(3, 50, 3)]
    [InlineData(9, 10, 1)]
    [InlineData(10, 20, 3)]
    public void CanaryCount_FollowsTheFirstWeight(int count, int weight, int canaries)
        => JobGenerator.CanaryCount(new RolloutStrategy(RolloutStrategyKind.Canary,
            [new RolloutStep(weight, System.TimeSpan.Zero)], System.TimeSpan.Zero, System.TimeSpan.Zero), count).ShouldBe(canaries);

    private static NomadDeploymentInfo Canaries(int desired, int healthy, int unhealthy = 0, string status = "running", bool promoted = false)
        => new(5, status, null, DesiredTotal: 3, healthy, unhealthy, CreateIndex: 50)
        {
            Id = "d-1", DesiredCanaries = desired, Promoted = promoted
        };

    private static NomadJobInfo Running => new(5, "running", Stop: false);

    [Fact]
    public void Canaries_AllHealthy_AreHealthyWhileWaitingForPromotion()
    {
        var obs = NomadRollout.EvaluateCanaries(Running, [Canaries(2, 2)], 5, autoRevert: true);

        obs.State.ShouldBe(HealthState.Healthy);
        obs.Detail.ShouldBe("2/2 canary allocation(s) healthy");
        Canaries(2, 2).AwaitingPromotion.ShouldBeTrue();
    }

    [Fact]
    public void Canaries_Starting_AreProgressing()
        => NomadRollout.EvaluateCanaries(Running, [Canaries(2, 1)], 5, autoRevert: true).State.ShouldBe(HealthState.Progressing);

    [Fact]
    public void Canaries_Unhealthy_Fail()
        => NomadRollout.EvaluateCanaries(Running, [Canaries(2, 1, unhealthy: 1)], 5, autoRevert: true).State.ShouldBe(HealthState.Failed);

    [Fact]
    public void Canaries_DeploymentFailed_IsFailed()
        => NomadRollout.EvaluateCanaries(Running, [Canaries(2, 0, status: "failed")], 5, autoRevert: false).State.ShouldBe(HealthState.Failed);

    [Fact]
    public void Promoted_IsNotAwaitingPromotion()
        => Canaries(2, 2, promoted: true).AwaitingPromotion.ShouldBeFalse();
}
