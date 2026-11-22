using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Deployments;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.Services;
using Deployify.Core.State;
using Deployify.Tests.Shared;
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

    private DeploymentRunner Runner(EnvironmentConfig? config = null) => new(new DeploymentSession(
        Substitute.For<IServiceProvider>(), config ?? new EnvironmentConfig { Target = DeploymentTargetType.Compose },
        "dev", DeploymentTargetType.Compose, _target, _dir)
    { State = _state });

    private static EnvironmentConfig TaggedConfig(bool withDigest)
    {
        var config = new EnvironmentConfig { Target = DeploymentTargetType.Compose, ContainerRegistry = "localhost:5000", ImageTag = "abc123" };
        if (withDigest)
        {
            config.PublishedImages.Record(ServiceImage.Repository("api", config), "abc123",
                "sha256:" + new string('a', 64), DateTimeOffset.UnixEpoch);
        }

        return config;
    }

    [Fact]
    public async Task RequireDigest_WithoutPublishing_StopsBeforePlanning_WhenAnImageHasNoDigest()
    {
        var result = await Runner(TaggedConfig(withDigest: false)).RunAsync(
            _manifest, new DeploymentRunOptions { Publish = false, RequireDigest = true }, Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Failed);
        result.Summary.ShouldContain("no published digest for tag abc123");
        result.Summary.ShouldContain("api (localhost:5000/api:abc123)");
        await _target.DidNotReceiveWithAnyArgs().PlanAsync(default!, default!, default!, Ct);
        await _target.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task RequireDigest_WithoutPublishing_Deploys_WhenEveryImageHasADigest()
    {
        var result = await Runner(TaggedConfig(withDigest: true)).RunAsync(
            _manifest, new DeploymentRunOptions { Publish = false, RequireDigest = true }, Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Deployed);
        await _target.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task RequireDigest_IsNotChecked_WhenTheRunPublishes()
    {
        var result = await Runner(TaggedConfig(withDigest: false)).RunAsync(
            _manifest, new DeploymentRunOptions { RequireDigest = true }, Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Deployed);
        await _target.ReceivedWithAnyArgs(1).PublishAsync(default!, default!, default!, Ct);
    }

    // ── Release history ───────────────────────────────────────────────────────

    private const string ApiDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task A_deploy_is_recorded_with_its_images()
    {
        var result = await Runner(TaggedConfig(withDigest: true)).RunAsync(_manifest, new DeploymentRunOptions { Publish = false }, Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Deployed);
        var release = (await ReleaseLog.ListAsync(_state, ct: Ct)).ShouldHaveSingleItem();
        release.Kind.ShouldBe(ReleaseKind.Deploy);
        release.Outcome.ShouldBe(ReleaseOutcome.Succeeded);
        release.Environment.ShouldBe("dev");
        release.Tag.ShouldBe("abc123");
        release.Summary.ShouldBeNull();
        release.Images.ShouldBe([new ReleaseImage("api", "localhost:5000/api", "abc123", ApiDigest)]);
    }

    [Fact]
    public async Task A_deploy_of_images_built_elsewhere_takes_the_commit_from_their_publish_record()
    {
        await ReleaseLog.AppendAsync(_state, new ReleaseRecord
        {
            Id = "publish00001", Kind = ReleaseKind.Publish, Environment = "dev",
            At = DateTimeOffset.UtcNow.AddMinutes(-5), Tag = "abc123", Commit = "4f2c9e1a7b3d"
        }, ct: Ct);

        await Runner(TaggedConfig(withDigest: true)).RunAsync(_manifest, new DeploymentRunOptions { Publish = false }, Ct);

        (await ReleaseLog.ListAsync(_state, ct: Ct))[0].Commit.ShouldBe("4f2c9e1a7b3d");
    }

    [Fact]
    public async Task A_commit_SHA_tag_is_the_commit()
    {
        var sha = "4F2C9E1A7B3D4F2C9E1A7B3D4F2C9E1A7B3D4F2C";
        var config = new EnvironmentConfig { Target = DeploymentTargetType.Compose, ContainerRegistry = "localhost:5000", ImageTag = sha };

        await Runner(config).RunAsync(_manifest, new DeploymentRunOptions { Publish = false }, Ct);

        var release = (await ReleaseLog.ListAsync(_state, ct: Ct)).ShouldHaveSingleItem();
        release.Commit.ShouldBe(sha.ToLowerInvariant());
        release.Images.ShouldHaveSingleItem().Digest.ShouldBeNull("nothing was published, so the image isn't pinned");
    }

    [Fact]
    public async Task A_run_that_publishes_records_the_publish_and_then_the_deploy()
    {
        var config = TaggedConfig(withDigest: false);
        _target.WhenForAnyArgs(t => t.PublishAsync(default!, default!, default!, default))
            .Do(_ => config.PublishedImages.Record("localhost:5000/api", "abc123", ApiDigest, DateTimeOffset.UtcNow));

        await Runner(config).RunAsync(_manifest, new DeploymentRunOptions(), Ct);

        var history = await ReleaseLog.ListAsync(_state, ct: Ct);
        history.Select(r => r.Kind).ShouldBe([ReleaseKind.Deploy, ReleaseKind.Publish]);
        history.ShouldAllBe(r => r.Images.Single().Digest == ApiDigest);
    }

    [Fact]
    public async Task A_failed_deploy_is_recorded_as_failed_with_the_reason()
    {
        _target.ApplyAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(
            [new DeploymentResult { ServiceName = "api", Success = false, Error = "boom" }]);

        var result = await Runner(TaggedConfig(withDigest: true)).RunAsync(_manifest, new DeploymentRunOptions { Publish = false }, Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Failed);
        var release = (await ReleaseLog.ListAsync(_state, ct: Ct)).ShouldHaveSingleItem();
        release.Outcome.ShouldBe(ReleaseOutcome.Failed);
        release.Summary.ShouldBe(result.Summary);
    }

    [Fact]
    public async Task A_declined_deploy_is_not_recorded()
    {
        await Runner(TaggedConfig(withDigest: true)).RunAsync(_manifest, new DeploymentRunOptions
        {
            Publish = false,
            Approve = (_, _) => Task.FromResult(false)
        }, Ct);

        (await ReleaseLog.ListAsync(_state, ct: Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task History_keep_from_the_state_config_prunes_old_records()
    {
        await using var state = new StateSession(_dir, "dev", new StateBackendConfig { History = { Keep = 2 } });
        var runner = new DeploymentRunner(new DeploymentSession(
            Substitute.For<IServiceProvider>(), TaggedConfig(withDigest: true), "dev", DeploymentTargetType.Compose, _target, _dir)
        { State = state });

        for (var i = 0; i < 3; i++)
        {
            await runner.RunAsync(_manifest, new DeploymentRunOptions { Publish = false }, Ct);
        }

        (await ReleaseLog.ListAsync(state, limit: 100, ct: Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task History_keep_minus_one_records_nothing()
    {
        await using var state = new StateSession(_dir, "dev", new StateBackendConfig { History = { Keep = ReleaseLog.Disabled } });
        var config = TaggedConfig(withDigest: false);
        _target.WhenForAnyArgs(t => t.PublishAsync(default!, default!, default!, default))
            .Do(_ => config.PublishedImages.Record("localhost:5000/api", "abc123", ApiDigest, DateTimeOffset.UtcNow));
        var runner = new DeploymentRunner(new DeploymentSession(
            Substitute.For<IServiceProvider>(), config, "dev", DeploymentTargetType.Compose, _target, _dir)
        { State = state });

        var result = await runner.RunAsync(_manifest, new DeploymentRunOptions(), Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Deployed);
        (await state.Backend.ListAsync(ReleaseLog.Prefix(state), Ct)).ShouldBeEmpty("neither the publish nor the deploy is recorded");
    }

    // ── RequireSignature ──────────────────────────────────────────────────────

    private const string ApiImage = "localhost:5000/api@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static EnvironmentConfig SignedConfig(bool withDigest = true, SigningConfig? signing = null)
    {
        var config = TaggedConfig(withDigest);
        config.Signing = signing ?? new SigningConfig { PublicKey = "env:COSIGN_PUBLIC_KEY" };
        return config;
    }

    /// <summary>cosign that verifies every image, or fails <c>verify</c> with <paramref name="error"/>.</summary>
    private static FakeProcessRunner Cosign(string? error = null)
    {
        var runner = new FakeProcessRunner();
        if (error is not null)
        {
            runner.When(["verify"], new ProcessResult(1, "", error));
        }

        return runner;
    }

    private static DeploymentRunOptions SignatureOptions(FakeProcessRunner cosign, bool publish) => new()
    {
        Publish = publish,
        RequireSignature = true,
        Signer = new ImageSigner(cosign)
    };

    [Fact]
    public async Task RequireSignature_WithoutPublishing_VerifiesByDigest_ThenDeploys()
    {
        var cosign = Cosign();

        var result = await Runner(SignedConfig()).RunAsync(_manifest, SignatureOptions(cosign, publish: false), Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Deployed, result.Summary);
        cosign.Single("verify").Args.ShouldBe(["verify", "--key", "env://COSIGN_PUBLIC_KEY", ApiImage]);
    }

    [Fact]
    public async Task RequireSignature_WithoutPublishing_StopsBeforePlanning_WhenASignatureDoesntVerify()
    {
        var result = await Runner(SignedConfig()).RunAsync(
            _manifest, SignatureOptions(Cosign("Error: no matching signatures"), publish: false), Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Failed);
        result.Summary.ShouldContain("1 of 1 image(s) have no signature dev trusts (api)");
        await _target.DidNotReceiveWithAnyArgs().PlanAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task RequireSignature_WithoutPublishing_StopsBeforePlanning_WhenAnImageHasNoDigest()
    {
        var cosign = Cosign();

        var result = await Runner(SignedConfig(withDigest: false)).RunAsync(_manifest, SignatureOptions(cosign, publish: false), Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Failed);
        result.Summary.ShouldContain("RequireSignature is set, but 1 image(s) have no published digest");
        cosign.Calls.ShouldNotContain(c => c.Args[0] == "verify");
        await _target.DidNotReceiveWithAnyArgs().PlanAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task RequireSignature_StopsBeforePlanning_WithoutSettingsToVerifyWith()
    {
        var result = await Runner(SignedConfig(signing: new SigningConfig { Enabled = true })).RunAsync(
            _manifest, SignatureOptions(Cosign(), publish: true), Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Failed);
        result.Summary.ShouldContain("signing.identity");
        await _target.DidNotReceiveWithAnyArgs().PlanAsync(default!, default!, default!, Ct);
        await _target.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task RequireSignature_StopsBeforePlanning_WhenCosignIsMissing()
    {
        var cosign = Cosign();
        cosign.Missing.Add("cosign");

        var result = await Runner(SignedConfig()).RunAsync(_manifest, SignatureOptions(cosign, publish: true), Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Failed);
        result.Summary.ShouldContain("cosign isn't installed");
        await _target.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task RequireSignature_WhenPublishing_VerifiesThePushedImages_BeforeDeploying()
    {
        var cosign = Cosign("Error: no signatures found");

        var result = await Runner(SignedConfig()).RunAsync(_manifest, SignatureOptions(cosign, publish: true), Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Failed);
        await _target.ReceivedWithAnyArgs(1).PublishAsync(default!, default!, default!, Ct);
        cosign.Single("verify").Args[^1].ShouldBe(ApiImage);
        await _target.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default!, Ct);
    }

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
    public async Task ResourceAServiceDependsOn_ThatCantBeDeployed_StopsBeforePlanning()
    {
        var runner = RunnerWithAudit(new ManifestAuditResult(
            [new ManifestDiagnostic("npm", "executable.v0", ManifestDiagnosticSeverity.Error, "'npm' runs a local executable")],
            [new PlanSkipped("npm", "executable.v0", "runs a local executable")]));

        var result = await runner.RunAsync(_manifest, new DeploymentRunOptions(), Ct);

        result.Outcome.ShouldBe(DeploymentRunOutcome.Failed);
        result.Summary.ShouldContain("nothing was changed");
        await _target.DidNotReceiveWithAnyArgs().PlanAsync(default!, default!, default!, Ct);
        await _target.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task UnusedResourceThatCantBeDeployed_IsSkipped_UnlessStrict()
    {
        var audit = new ManifestAuditResult(
            [new ManifestDiagnostic("npm", "executable.v0", ManifestDiagnosticSeverity.Warning, "'npm' runs a local executable")],
            [new PlanSkipped("npm", "executable.v0", "runs a local executable")]);

        var result = await RunnerWithAudit(audit).RunAsync(_manifest, new DeploymentRunOptions(), Ct);
        result.Outcome.ShouldBe(DeploymentRunOutcome.Deployed);
        result.Plan!.Skipped.ShouldHaveSingleItem().Name.ShouldBe("npm");

        var strict = await RunnerWithAudit(audit).RunAsync(_manifest, new DeploymentRunOptions { Strict = true }, Ct);
        strict.Outcome.ShouldBe(DeploymentRunOutcome.Failed);
        strict.Summary.ShouldContain("--strict");
    }

    private DeploymentRunner RunnerWithAudit(ManifestAuditResult audit)
    {
        var session = new DeploymentSession(
            Substitute.For<IServiceProvider>(), new EnvironmentConfig { Target = DeploymentTargetType.Compose },
            "dev", DeploymentTargetType.Compose, _target, _dir)
        { State = _state };
        session.ManifestAudit = audit;
        return new DeploymentRunner(session);
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
