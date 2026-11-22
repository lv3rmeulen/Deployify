using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.AwsEcs.Tests;

public sealed class EcsStrategyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProcessResult Ok(string stdout) => new(0, stdout, "");

    private static RolloutStrategy Canary(params (int Weight, string Pause)[] steps)
        => RolloutStrategies.For(new EnvironmentConfig
        {
            Rollout = new RolloutConfig
            {
                Strategy = RolloutStrategyKind.Canary,
                Canary = new CanaryConfig { Steps = [.. steps.Select(s => new CanaryStep { Weight = s.Weight, Pause = s.Pause })] }
            }
        }, "api");

    private static RolloutStrategy BlueGreen(string? preview = null, string delay = "5m")
        => RolloutStrategies.For(new EnvironmentConfig
        {
            Rollout = new RolloutConfig
            {
                Strategy = RolloutStrategyKind.BlueGreen, BlueGreen = new BlueGreenConfig { PreviewPause = preview, ScaleDownDelay = delay }
            }
        }, "api");

    [Fact]
    public void OneStep_IsCanary()
    {
        EcsStrategies.UnsupportedReason(Canary((10, "5m")), serviceConnect: true).ShouldBeNull();
        EcsStrategies.Shorthand(Canary((10, "5m"))).ShouldBe(
            "strategy=CANARY,bakeTimeInMinutes=0,canaryConfiguration={canaryPercent=10.0,canaryBakeTimeInMinutes=5}");
    }

    [Fact]
    public void EqualStepsAllTheWayUp_AreLinear()
    {
        var linear = Canary((25, "2m"), (50, "2m"), (75, "2m"));

        EcsStrategies.UnsupportedReason(linear, serviceConnect: true).ShouldBeNull();
        EcsStrategies.Shorthand(linear).ShouldBe(
            "strategy=LINEAR,bakeTimeInMinutes=0,linearConfiguration={stepPercent=25.0,stepBakeTimeInMinutes=2}");
    }

    [Theory]
    [InlineData(new[] { 10, 50 }, new[] { "1m", "1m" })]   // not equal increments
    [InlineData(new[] { 25, 50 }, new[] { "1m", "1m" })]   // stops before 75
    [InlineData(new[] { 25, 50, 75 }, new[] { "1m", "2m", "1m" })] // pauses differ
    public void OtherSteps_AreRefused(int[] weights, string[] pauses)
        => EcsStrategies.UnsupportedReason(Canary([.. weights.Zip(pauses)]), serviceConnect: true)!.ShouldContain("LINEAR");

    [Fact]
    public void Canary_NeedsServiceConnect()
        => EcsStrategies.UnsupportedReason(Canary((10, "5m")), serviceConnect: false)!.ShouldContain("Service Connect");

    [Fact]
    public void BakeTimes_AreWholeMinutes()
        => EcsStrategies.UnsupportedReason(Canary((10, "90s")), serviceConnect: true)!.ShouldContain("whole minutes");

    [Fact]
    public void BlueGreen_KeepsTheOldVersionForTheDelay_AndRefusesAPreviewPause()
    {
        EcsStrategies.Shorthand(BlueGreen()).ShouldBe("strategy=BLUE_GREEN,bakeTimeInMinutes=5");
        EcsStrategies.UnsupportedReason(BlueGreen(), serviceConnect: false).ShouldBeNull();
        EcsStrategies.UnsupportedReason(BlueGreen(preview: "2m"), serviceConnect: true)!.ShouldContain("previewPause");
    }

    [Fact]
    public async Task UpdateService_WithCanary_SendsTheStrategy_WithoutTheCircuitBreaker()
    {
        var fake = new FakeProcessRunner()
            .When(["list-services"], Ok("""{"serviceArns":["arn:x/api"]}"""))
            .When(["update-service"], Ok("""{"service":{"serviceArn":"arn:x"}}"""));
        var client = new EcsCliClient(new EcsTargetConfig(), NullLogger.Instance, fake);
        client.ServiceStrategies["api"] = Canary((10, "5m"));

        await client.UpsertServiceAsync("api", "arn:td:2", 1, Ct);

        var args = fake.Single("update-service").Args.ToList();
        args[args.IndexOf("--deployment-configuration") + 1].ShouldStartWith("strategy=CANARY,");
        args.ShouldNotContain(a => a.Contains("deploymentCircuitBreaker"));
    }

    [Fact]
    public async Task BackToRolling_SaysSoExplicitly()
    {
        var fake = new FakeProcessRunner()
            .When(["list-services"], Ok("""{"serviceArns":["arn:x/api"]}"""))
            .When(["describe-services"], Ok("""{"services":[{"status":"ACTIVE","deploymentConfiguration":{"strategy":"BLUE_GREEN"},"deployments":[]}]}"""))
            .When(["update-service"], Ok("""{"service":{"serviceArn":"arn:x"}}"""));
        var client = new EcsCliClient(new EcsTargetConfig(), NullLogger.Instance, fake);
        client.ServiceStrategies["api"] = RolloutStrategy.Rolling;

        await client.UpsertServiceAsync("api", "arn:td:2", 1, Ct);

        var args = fake.Single("update-service").Args.ToList();
        args[args.IndexOf("--deployment-configuration") + 1]
            .ShouldBe("strategy=ROLLING,deploymentCircuitBreaker={enable=true,rollback=true}");
    }

    [Fact]
    public async Task Restore_OfABlueGreenService_KeepsItsStrategy()
    {
        var fake = new FakeProcessRunner()
            .When(["list-services"], Ok("""{"serviceArns":["arn:x/api"]}"""))
            .When(["describe-services"], Ok("""{"services":[{"status":"ACTIVE","deploymentConfiguration":{"strategy":"BLUE_GREEN"},"deployments":[]}]}"""))
            .When(["update-service"], Ok("""{"service":{"serviceArn":"arn:x"}}"""));

        await new EcsCliClient(new EcsTargetConfig(), NullLogger.Instance, fake).RestoreServiceAsync("api", "arn:td:1", 1, Ct);

        fake.Single("update-service").Args.ShouldNotContain("--deployment-configuration");
    }
}
