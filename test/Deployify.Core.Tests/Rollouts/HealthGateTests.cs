using System;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Rollouts;

public sealed class HealthGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RolloutOptions Fast = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(1),
        ProbeTimeout = TimeSpan.FromMilliseconds(100),
        RequiredSuccesses = 2
    };

    [Fact]
    public async Task ObservationThatThrows_CountsAsProgressing_NotAsFailure()
    {
        var target = Substitute.For<IRolloutTarget>();
        var calls = 0;
        target.ObserveAsync(Arg.Any<EnvironmentConfig>(), "api", "api:2", Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? Task.FromException<HealthObservation>(new InvalidOperationException("throttled"))
                : Task.FromResult(HealthObservation.Healthy()));

        var result = await HealthGate.WaitAsync(target, new EnvironmentConfig(), "api", "api:2",
            TimeSpan.FromSeconds(5), Fast, ct: Ct);

        result.Outcome.ShouldBe(GateOutcome.Healthy);
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task HangingObservation_IsCutOffByProbeTimeout_AndTheGateTimesOut()
    {
        var target = Substitute.For<IRolloutTarget>();
        target.ObserveAsync(Arg.Any<EnvironmentConfig>(), "api", "api:2", Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await Task.Delay(Timeout.Infinite, ci.ArgAt<CancellationToken>(3));
                return HealthObservation.Healthy();
            });

        var result = await HealthGate.WaitAsync(target, new EnvironmentConfig(), "api", "api:2",
            TimeSpan.FromMilliseconds(400), Fast, ct: Ct);

        result.Outcome.ShouldBe(GateOutcome.TimedOut);
        result.Detail!.ShouldContain("health check took longer than");
    }

    [Fact]
    public async Task CancellingTheCaller_Throws_RatherThanReportingATimeout()
    {
        var target = Substitute.For<IRolloutTarget>();
        target.ObserveAsync(Arg.Any<EnvironmentConfig>(), "api", "api:2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(HealthObservation.Progressing()));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        Exception? thrown = null;
        try
        {
            await HealthGate.WaitAsync(
                target, new EnvironmentConfig(), "api", "api:2", TimeSpan.FromSeconds(30), Fast, ct: cts.Token);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        thrown.ShouldBeAssignableTo<OperationCanceledException>();
    }
}
