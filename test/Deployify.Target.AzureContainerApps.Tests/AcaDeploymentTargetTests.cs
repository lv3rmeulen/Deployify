using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.AzureContainerApps.Tests;

// ── AcaCliClient validation (no network) ─────────────────────────────────────

public sealed class AcaCliClientValidationTests
{
    private AcaCliClient Client(string rg = "test-rg")
        => new(new AcaTargetConfig { ResourceGroup = rg, Location = "eastus" }, NullLogger.Instance);

    [Fact]
    public async Task ValidateAppSpec_ReturnsNoErrors_ForValidSpec()
    {
        using var dir = new AcaFixtures.TempDir();
        var generator = new AcaManifestGenerator();
        var config = AcaFixtures.AcaConfig();
        var acaCfg = new AcaTargetConfig { Location = "eastus", SubscriptionId = "sub-123", EnvironmentName = "env" };
        var apps = await generator.GenerateAsync(AcaFixtures.SingleService("api"), config, acaCfg, TestContext.Current.CancellationToken);
        var path = await generator.WriteAppSpecAsync(apps[0], config, acaCfg, dir.Path, TestContext.Current.CancellationToken);

        var errors = await Client().ValidateAppSpecAsync(path, TestContext.Current.CancellationToken);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task ValidateAppSpec_ReturnsError_WhenNameMissing()
    {
        using var dir = new AcaFixtures.TempDir();
        var path = Path.Combine(dir.Path, "bad.containerapp.json");
        await File.WriteAllTextAsync(path, """{"location":"eastus","type":"Microsoft.App/containerApps","properties":{"environmentId":"x","template":{"containers":[]}}}""", TestContext.Current.CancellationToken);

        var errors = await Client().ValidateAppSpecAsync(path, TestContext.Current.CancellationToken);

        errors.ShouldContain(e => e.Contains("name"));
    }

    [Fact]
    public async Task ValidateAppSpec_ReturnsError_WhenLocationMissing()
    {
        using var dir = new AcaFixtures.TempDir();
        var path = Path.Combine(dir.Path, "bad.containerapp.json");
        await File.WriteAllTextAsync(path, """{"name":"api","properties":{"environmentId":"x","template":{"containers":[]}}}""", TestContext.Current.CancellationToken);

        var errors = await Client().ValidateAppSpecAsync(path, TestContext.Current.CancellationToken);

        errors.ShouldContain(e => e.Contains("location"));
    }

    [Fact]
    public async Task ValidateAppSpec_ReturnsError_WhenPropertiesMissing()
    {
        using var dir = new AcaFixtures.TempDir();
        var path = Path.Combine(dir.Path, "bad.containerapp.json");
        await File.WriteAllTextAsync(path, """{"name":"api","location":"eastus"}""", TestContext.Current.CancellationToken);

        var errors = await Client().ValidateAppSpecAsync(path, TestContext.Current.CancellationToken);

        errors.ShouldContain(e => e.Contains("properties"));
    }

    [Fact]
    public async Task ValidateAppSpec_ReturnsError_WhenEnvironmentIdMissing()
    {
        using var dir = new AcaFixtures.TempDir();
        var path = Path.Combine(dir.Path, "bad.containerapp.json");
        await File.WriteAllTextAsync(path, """{"name":"api","location":"eastus","properties":{"template":{"containers":[]}}}""", TestContext.Current.CancellationToken);

        var errors = await Client().ValidateAppSpecAsync(path, TestContext.Current.CancellationToken);

        errors.ShouldContain(e => e.Contains("environmentId"));
    }

    [Fact]
    public async Task ValidateAppSpec_ReturnsError_WhenInvalidJson()
    {
        using var dir = new AcaFixtures.TempDir();
        var path = Path.Combine(dir.Path, "bad.containerapp.json");
        await File.WriteAllTextAsync(path, "{ not : valid : json", TestContext.Current.CancellationToken);

        var errors = await Client().ValidateAppSpecAsync(path, TestContext.Current.CancellationToken);

        errors.ShouldContain(e => e.Contains("JSON") || e.Contains("Invalid"));
    }
}

// ── AcaDeploymentTarget ───────────────────────────────────────────────────────

public sealed class AcaDeploymentTargetTests
{
    private static AcaDeploymentTarget Target()
        => new(Substitute.For<IPublisher>(), NullLogger<AcaDeploymentTarget>.Instance);

    [Fact]
    public void TargetType_IsAzureContainerApps()
    {
        Target().TargetType.ShouldBe(DeploymentTargetType.AzureContainerApps);
    }

    [Fact]
    public void DisplayName_ContainsAzure()
    {
        Target().DisplayName.ToLower().ShouldContain("azure");
    }

    // ── GenerateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_WritesOneFilePerService()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();

        var paths = await Target().GenerateAsync(AcaFixtures.SimpleManifest(), config, dir.Path, TestContext.Current.CancellationToken);

        paths.Count.ShouldBe(3);
        foreach (var p in paths)
        {
            File.Exists(p).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task GenerateAsync_ProducesContainerAppJsonFiles()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();

        await Target().GenerateAsync(AcaFixtures.SimpleManifest(), config, dir.Path, TestContext.Current.CancellationToken);

        Directory.GetFiles(dir.Path, "*.containerapp.json").Length.ShouldBe(3);
    }

    // ── ValidateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_ReturnsOneResultPerService()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();

        var results = await Target().ValidateAsync(AcaFixtures.SimpleManifest(), config, dir.Path, TestContext.Current.CancellationToken);

        results.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ValidateAsync_ResultsAreValid_ForWellFormedManifest()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();

        var results = await Target().ValidateAsync(AcaFixtures.SimpleManifest(), config, dir.Path, TestContext.Current.CancellationToken);

        foreach (var r in results)
        {
            r.IsValid.ShouldBeTrue();
        }
    }

    // ── PlanAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlanAsync_WritesAppSpecsBeforeDiffingAzure()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();

        try
        {
            await Target().PlanAsync(AcaFixtures.SimpleManifest(), config, dir.Path, TestContext.Current.CancellationToken);
        }
        catch
        {
            /* az CLI not available in CI */
        }

        Directory.GetFiles(dir.Path, "*.containerapp.json").Length.ShouldBe(3);
    }

    // ── ApplyAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_ReturnsFailure_WhenSpecFilesMissing()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();

        var results = await Target().ApplyAsync(AcaFixtures.SimpleManifest(), config, dir.Path, TestContext.Current.CancellationToken);

        foreach (var r in results)
        {
            r.Success.ShouldBeFalse();
            r.Error?.ShouldContain("plan");
        }
    }

    [Fact]
    public async Task ApplyAsync_ReturnsOneResultPerService_WhenFilesAreMissing()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();

        var results = await Target().ApplyAsync(AcaFixtures.SimpleManifest(), config, dir.Path, TestContext.Current.CancellationToken);

        results.Count.ShouldBe(3);
    }

    // ── Explain ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Explain_ReturnsOneExplanationPerService()
    {
        var config = AcaFixtures.AcaConfig();
        var explanations = await Target().ExplainAsync(AcaFixtures.SimpleManifest(), config, TestContext.Current.CancellationToken);
        explanations.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Explain_SetsServiceNames()
    {
        var config = AcaFixtures.AcaConfig();
        var explanations = await Target().ExplainAsync(AcaFixtures.SimpleManifest(), config, TestContext.Current.CancellationToken);
        explanations.Select(e => e.Name).ShouldBe(["api", "worker", "redis"], ignoreOrder: true);
    }

    [Fact]
    public async Task Explain_AdditionalInfo_ContainsResourceGroupAndLocation()
    {
        var config = AcaFixtures.AcaConfig(resourceGroup: "prod-rg", location: "westeurope");
        var explanations = await Target().ExplainAsync(AcaFixtures.SingleService("api"), config, TestContext.Current.CancellationToken);

        var info = explanations[0].AdditionalInfo;
        info["resourceGroup"].ShouldBe("prod-rg");
        info["location"].ShouldBe("westeurope");
    }

    [Fact]
    public async Task Explain_AdditionalInfo_ContainsReplicaRange()
    {
        var config = AcaFixtures.AcaConfig();
        var explanations = await Target().ExplainAsync(AcaFixtures.SingleService("api"), config, TestContext.Current.CancellationToken);

        var info = explanations[0].AdditionalInfo;
        info.ShouldContainKey("minReplicas");
        info.ShouldContainKey("maxReplicas");
    }

    [Fact]
    public async Task Explain_AdditionalInfo_ContainsCpuAndMemory()
    {
        var config = AcaFixtures.AcaConfig();
        var explanations = await Target().ExplainAsync(AcaFixtures.SingleService("api"), config, TestContext.Current.CancellationToken);

        var info = explanations[0].AdditionalInfo;
        info["cpu"].ShouldContain("cores");
        info["memory"].ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Explain_PopulatesPorts()
    {
        var config = AcaFixtures.AcaConfig();
        var explanations = await Target().ExplainAsync(AcaFixtures.SingleService("api", "project.v0", 8080), config, TestContext.Current.CancellationToken);
        explanations[0].Ports.ShouldContain(p => p.Contains("8080"));
    }

    // ── GetDependencies ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetDependencies_ReturnsDependencyEdges()
    {
        var config = AcaFixtures.AcaConfig();
        var deps = await Target().GetDependenciesAsync(AcaFixtures.SimpleManifest(), config, TestContext.Current.CancellationToken);
        deps.ShouldContain(d => d.From == "api" && d.To == "worker");
    }

    [Fact]
    public async Task GetDependencies_ReturnsEmpty_WhenNoDependencies()
    {
        var config = AcaFixtures.AcaConfig();
        var deps = await Target().GetDependenciesAsync(AcaFixtures.SingleService("api"), config, TestContext.Current.CancellationToken);
        deps.ShouldBeEmpty();
    }
}
