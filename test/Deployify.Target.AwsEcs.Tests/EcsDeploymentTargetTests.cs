using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.AwsEcs.Tests;

public sealed class EcsDeploymentTargetTests
{
    private static EcsDeploymentTarget Target()
        => new(Substitute.For<IPublisher>(), NullLogger<EcsDeploymentTarget>.Instance);

    [Fact]
    public void TargetType_IsAwsEcs()
    {
        Target().TargetType.ShouldBe(DeploymentTargetType.AwsEcs);
    }

    [Fact]
    public void DisplayName_ContainsEcs()
    {
        Target().DisplayName.ToLower().ShouldContain("ecs");
    }

    // ── GenerateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_WritesOneFilePerService()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();

        var paths = await Target().GenerateAsync(EcsFixtures.SimpleManifest(), config, dir.Path, TestContext.Current.CancellationToken);

        paths.Count.ShouldBe(3);
        foreach (var p in paths)
        {
            File.Exists(p).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task GenerateAsync_ProducesTaskDefJsonFiles()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();

        await Target().GenerateAsync(EcsFixtures.SimpleManifest(), config, dir.Path, TestContext.Current.CancellationToken);

        Directory.GetFiles(dir.Path, "*.taskdef.json").Length.ShouldBe(3);
    }

    // ── ValidateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_ReturnsOneResultPerService()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();

        var results = await Target().ValidateAsync(EcsFixtures.SimpleManifest(), config, dir.Path, TestContext.Current.CancellationToken);

        results.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ValidateAsync_ResultsAreValid_ForWellFormedManifest()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();

        var results = await Target().ValidateAsync(EcsFixtures.SimpleManifest(), config, dir.Path, TestContext.Current.CancellationToken);

        foreach (var r in results)
        {
            r.IsValid.ShouldBeTrue();
        }
    }

    // ── PlanAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlanAsync_WritesTaskDefinitionsBeforeDiffingCluster()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();

        // Plan will fail to reach AWS but files should be written first
        try
        {
            await Target().PlanAsync(EcsFixtures.SimpleManifest(), config, dir.Path, TestContext.Current.CancellationToken);
        }
        catch
        {
            /* cluster unreachable in CI */
        }

        Directory.GetFiles(dir.Path, "*.taskdef.json").Length.ShouldBe(3);
    }

    // ── ApplyAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_ReturnsFailure_WhenTaskDefFilesMissing()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();

        var results = await Target().ApplyAsync(EcsFixtures.SimpleManifest(), config, dir.Path, TestContext.Current.CancellationToken);

        foreach (var r in results)
        {
            r.Success.ShouldBeFalse();
            r.Error?.ShouldContain("plan");
        }
    }

    [Fact]
    public async Task ApplyAsync_ReturnsOneResultPerService_WhenFilesAreMissing()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();

        var results = await Target().ApplyAsync(EcsFixtures.SimpleManifest(), config, dir.Path, TestContext.Current.CancellationToken);

        results.Count.ShouldBe(3);
    }

    // ── Explain ───────────────────────────────────────────────────────────────

    [Fact]
    public void Explain_ReturnsOneExplanationPerService()
    {
        var config = EcsFixtures.EcsConfig();
        var explanations = Target().Explain(EcsFixtures.SimpleManifest(), config);
        explanations.Count.ShouldBe(3);
    }

    [Fact]
    public void Explain_SetsServiceNames()
    {
        var config = EcsFixtures.EcsConfig();
        var explanations = Target().Explain(EcsFixtures.SimpleManifest(), config);
        explanations.Select(e => e.Name).ShouldBe(["api", "worker", "redis"], ignoreOrder: true);
    }

    [Fact]
    public void Explain_AdditionalInfo_ContainsClusterAndRegion()
    {
        var config = EcsFixtures.EcsConfig(region: "eu-west-1", cluster: "prod-cluster");
        var explanations = Target().Explain(EcsFixtures.SingleService("api"), config);

        var info = explanations[0].AdditionalInfo;
        info["cluster"].ShouldBe("prod-cluster");
        info["region"].ShouldBe("eu-west-1");
    }

    [Fact]
    public void Explain_AdditionalInfo_ContainsLaunchType()
    {
        var config = EcsFixtures.EcsConfig();
        var explanations = Target().Explain(EcsFixtures.SingleService("api"), config);
        explanations[0].AdditionalInfo["launchType"].ShouldBe("Fargate");
    }

    [Fact]
    public void Explain_PopulatesPorts()
    {
        var config = EcsFixtures.EcsConfig();
        var explanations = Target().Explain(EcsFixtures.SingleService("api", "project.v0", 8080), config);
        explanations[0].Ports.ShouldContain(p => p.Contains("8080"));
    }

    // ── GetDependencies ───────────────────────────────────────────────────────

    [Fact]
    public void GetDependencies_ReturnsDependencyEdges()
    {
        var config = EcsFixtures.EcsConfig();
        var deps = Target().GetDependencies(EcsFixtures.SimpleManifest(), config);
        deps.ShouldContain(d => d.From == "api" && d.To == "worker");
    }

    [Fact]
    public void GetDependencies_ReturnsEmpty_WhenNoDependencies()
    {
        var config = EcsFixtures.EcsConfig();
        var deps = Target().GetDependencies(EcsFixtures.SingleService("api"), config);
        deps.ShouldBeEmpty();
    }
}
