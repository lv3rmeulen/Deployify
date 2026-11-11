using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.Services;
using Deployify.Core.State;
using Deployify.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Hosting.Tests;

/// <summary>
/// Runs the real Aspire pipeline in-process (what <c>aspire do deployify-deploy</c> runs), with
/// Deployify's target swapped for a fake.
/// </summary>
public sealed class AspireDeployPipelineTests : IDisposable
{
    private readonly string _appHost = Directory.CreateTempSubdirectory("deployify-hosting-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_appHost, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void RunMode_KeepsTheResourceOutOfTheModel()
    {
        var builder = CreateBuilder([]);

        builder.AddDeployify();

        builder.Resources.OfType<DeployifyEnvironmentResource>().ShouldBeEmpty();
    }

    [Fact]
    public void PublishMode_AddsAStepRequiredByDeploy_AndPinsTheOutputPath()
    {
        var builder = CreateBuilder(["--operation", "publish"]);

        var deployify = builder.AddDeployify("prod");

        deployify.Resource.StepName.ShouldBe("prod-deploy");
        deployify.Resource.Annotations.OfType<PipelineStepAnnotation>().ShouldHaveSingleItem();
        deployify.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>().ShouldHaveSingleItem();
        builder.Configuration["Pipeline:OutputPath"].ShouldBe(Path.Combine(_appHost, "aspire-output"));
    }

    [Fact]
    public void PublishMode_LeavesAnExplicitOutputPathAlone()
    {
        var builder = CreateBuilder(["--operation", "publish", "--output-path", "/somewhere/else"]);

        builder.AddDeployify();

        builder.Configuration["Pipeline:OutputPath"].ShouldBe("/somewhere/else");
    }

    [Fact]
    public async Task DeployStep_ReadsTheAspireManifest_AndAppliesWithDeployify()
    {
        var ct = TestContext.Current.CancellationToken;
        var apiProject = WriteProject("Api");
        await WriteStateAsync(ct);
        var target = FakeTarget();

        var builder = CreateBuilder(["--operation", "publish", "--step", "deployify-deploy"]);
        builder.AddProject("api", apiProject);
        builder.AddDeployify(configure: o =>
        {
            o.AutoApprove = true;
            o.ImageTag = "build-42";
        }).Resource.ConfigureServices = s => s.AddSingleton<IDeploymentTargetFactory>(new DeploymentTargetFactory([target]));

        await RunAsync(builder, ct);

        // Aspire's publish-manifest step wrote the manifest where the step looked for it.
        File.Exists(Path.Combine(_appHost, "aspire-output", "aspire-manifest.json")).ShouldBeTrue();

        // Deployify got the project with an absolute path, published it, then planned and applied.
        await target.Received(1).PublishAsync(
            Arg.Is<AspireManifest>(m => m.Resources["api"].Path == apiProject),
            Arg.Is<EnvironmentConfig>(c => c.ImageTag == "build-42"),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await target.Received(1).PlanAsync(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await target.Received(1).ApplyAsync(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // The Deployify resource itself is not in the manifest it deployed.
        var manifest = target.ReceivedCalls().Select(c => c.GetArguments().FirstOrDefault()).OfType<AspireManifest>().First();
        manifest.Resources.Keys.ShouldBe(["api"]);

        // The deployment was recorded in Deployify's state, as 'deployify apply' would.
        await using var state = new StateSession(_appHost, "staging", config: null);
        var inventory = await state.Inventory.LoadAsync(ct);
        inventory.Services.Select(x => x.Name).ShouldBe(["api"]);
    }

    [Fact]
    public async Task DeployStep_WithoutApproval_FailsBeforeChangingAnything()
    {
        var ct = TestContext.Current.CancellationToken;
        var apiProject = WriteProject("Api");
        await WriteStateAsync(ct);
        var target = FakeTarget();

        var builder = CreateBuilder(["--operation", "publish", "--step", "deployify-deploy"]);
        var logs = new CapturingLoggerProvider();
        builder.Services.AddSingleton<ILoggerProvider>(logs);
        builder.AddProject("api", apiProject);
        builder.AddDeployify().Resource.ConfigureServices =
            s => s.AddSingleton<IDeploymentTargetFactory>(new DeploymentTargetFactory([target]));

        // The host logs the failed step and stops; it doesn't rethrow.
        await RunAsync(builder, ct);

        logs.Messages.ShouldContain(m => m.Contains("Step 'deployify-deploy' failed") && m.Contains("Deployify__AutoApprove"));
        await target.Received(1).PlanAsync(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await target.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default!, ct);
        await target.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default!, ct);
    }

    private IDistributedApplicationBuilder CreateBuilder(string[] args)
        => DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = args,
            ProjectDirectory = _appHost,
            DisableDashboard = true
        });

    private static async Task RunAsync(IDistributedApplicationBuilder builder, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await using var app = builder.Build();
        await app.RunAsync(timeout.Token);
    }

    private string WriteProject(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_appHost, "..", Path.GetFileName(_appHost) + "-" + name)).FullName;
        var path = Path.Combine(dir, name + ".csproj");
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        return path;
    }

    private Task WriteStateAsync(CancellationToken ct)
        => new StateManager(new SecretRegistry()).SaveFileAsync(new DeployifyStateFile
        {
            ActiveEnvironment = "staging",
            Environments = new Dictionary<string, EnvironmentConfig>
            {
                ["staging"] = new() { Target = DeploymentTargetType.Compose }
            }
        }, _appHost, ct);

    private static IDeploymentTarget FakeTarget()
    {
        var target = Substitute.For<IDeploymentTarget>();
        target.TargetType.Returns(DeploymentTargetType.Compose);
        target.DisplayName.Returns("Fake Compose");
        target.Explain(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>())
            .Returns(ci => ci.Arg<AspireManifest>().Resources.Keys.Select(k => new ServiceExplanation { Name = k }).ToList());
        target.GetDependencies(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>()).Returns([]);
        target.PlanAsync(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PlanResult { ToCreate = ["api"] });
        target.ApplyAsync(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<DeploymentResult>)[new DeploymentResult { ServiceName = "api", Success = true }]);
        target.PublishAsync(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return target;
    }
}
