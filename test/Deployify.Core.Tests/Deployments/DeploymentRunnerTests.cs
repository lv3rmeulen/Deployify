using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Deployments;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.State;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Deployments;

public sealed class DeploymentRunnerTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("deployify-runner-").FullName;
    private readonly IDeploymentTarget _target = Substitute.For<IDeploymentTarget>();
    private readonly StateSession _state;
    private readonly AspireManifest _manifest = new()
    {
        Resources = new Dictionary<string, AspireResource>
        {
            ["api"] = new() { Type = "project.v0", Path = "/src/Api/Api.csproj" }
        }
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DeploymentRunnerTests()
    {
        _state = new StateSession(_dir, "dev", config: null);
        _target.TargetType.Returns(DeploymentTargetType.Compose);
        _target.DisplayName.Returns("Fake");
        _target.ExplainAsync(default!, default!, default).ReturnsForAnyArgs([new ServiceExplanation { Name = "api" }]);
        _target.GetDependenciesAsync(default!, default!, default).ReturnsForAnyArgs([]);
        _target.PlanAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(new PlanResult { ToUpdate = ["api"] });
        _target.ApplyAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(
            [new DeploymentResult { ServiceName = "api", Success = true }]);
    }

    public async ValueTask DisposeAsync()
    {
        await _state.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DeploymentRunner Runner() => new(new DeploymentSession(
        Substitute.For<IServiceProvider>(), new EnvironmentConfig { Target = DeploymentTargetType.Compose },
        "dev", DeploymentTargetType.Compose, _target, _dir)
    { State = _state });

    [Fact]
    public async Task Deploys_AfterPublishing_AndRecordsTheServices()
    {
        var result = await Runner().RunAsync(_manifest, new DeploymentRunOptions(), Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Deployed);
        result.Summary.ShouldContain("1 service(s) deployed to dev");
        Received.InOrder(() =>
        {
            _target.PlanAsync(_manifest, Arg.Any<EnvironmentConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            _target.PublishAsync(_manifest, Arg.Any<EnvironmentConfig>(), _dir, Arg.Any<CancellationToken>());
            _target.ApplyAsync(_manifest, Arg.Any<EnvironmentConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        });
        (await _state.Inventory.LoadAsync(Ct)).Services.Select(s => s.Name).ShouldBe(["api"]);
    }

    [Fact]
    public async Task Declined_ChangesNothing()
    {
        PlanResult? asked = null;
        var result = await Runner().RunAsync(_manifest, new DeploymentRunOptions
        {
            Approve = (plan, _) =>
            {
                asked = plan;
                return Task.FromResult(false);
            }
        }, Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Declined);
        result.Succeeded.ShouldBeTrue();
        asked!.ToUpdate.ShouldBe(["api"]);
        await _target.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default!, Ct);
        await _target.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default!, Ct);
        (await _state.Inventory.LoadAsync(Ct)).Services.ShouldBeEmpty();
    }

    [Fact]
    public async Task SkipsPublishing_WhenTurnedOff()
    {
        await Runner().RunAsync(_manifest, new DeploymentRunOptions { Publish = false }, Ct);

        await _target.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default!, Ct);
        await _target.ReceivedWithAnyArgs(1).ApplyAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task FailedPublish_StopsBeforeApply()
    {
        _target.PublishAsync(default!, default!, default!, Ct).ThrowsAsyncForAnyArgs(new InvalidOperationException("no docker"));

        var result = await Runner().RunAsync(_manifest, new DeploymentRunOptions(), Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Failed);
        result.Summary.ShouldContain("no docker");
        await _target.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task FailedService_FailsTheRun()
    {
        _target.ApplyAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(
            [new DeploymentResult { ServiceName = "api", Success = false, Error = "boom" }]);

        var result = await Runner().RunAsync(_manifest, new DeploymentRunOptions(), Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Failed);
        result.Summary.ShouldBe("Apply finished with errors: 0 succeeded, 1 failed.");
    }

    [Fact]
    public void ProjectPaths_AreAnchoredToTheManifestFolder()
    {
        var manifest = new AspireManifest
        {
            Resources = new Dictionary<string, AspireResource>
            {
                ["api"] = new() { Type = "project.v0", Path = "../Api/Api.csproj" },
                ["abs"] = new() { Type = "project.v0", Path = "/x/Abs.csproj" },
                ["redis"] = new() { Type = "container.v0", Image = "redis" }
            }
        };

        manifest.WithProjectPathsRelativeTo("/repo/AppHost/aspire-output");

        manifest.Resources["api"].Path.ShouldBe(Path.GetFullPath("/repo/AppHost/Api/Api.csproj"));
        manifest.Resources["abs"].Path.ShouldBe("/x/Abs.csproj");
        manifest.Resources["redis"].Path.ShouldBeNull();
    }
}
