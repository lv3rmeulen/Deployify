using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Helm.Tests;

public sealed class HelmStrategyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RolloutStrategy Strategy(RolloutStrategyKind kind)
        => RolloutStrategies.For(new EnvironmentConfig { Rollout = new RolloutConfig { Strategy = kind } }, "api");

    [Fact]
    public async Task Canary_IsSupported_BlueGreenIsNot()
    {
        var target = new HelmDeploymentTarget(Substitute.For<IPublisher>(), NullLoggerFactory.Instance, new FakeProcessRunner());

        (await target.GetStrategyUnsupportedReasonAsync(new AspireManifest(), new EnvironmentConfig(), "api",
            Strategy(RolloutStrategyKind.Canary), Ct)).ShouldBeNull();
        (await target.GetStrategyUnsupportedReasonAsync(new AspireManifest(), new EnvironmentConfig(), "api",
            Strategy(RolloutStrategyKind.BlueGreen), Ct))!.ShouldContain("Use Canary");
        target.Precision.ShouldBe(TrafficPrecision.Instances);
    }
}
