using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.NomadConsul.Tests;

public sealed class NomadConsulDeploymentTargetTests
{
    private static NomadConsulDeploymentTarget Target()
        => new(Substitute.For<IPublisher>(), NullLogger<NomadConsulDeploymentTarget>.Instance,
            NullLoggerFactory.Instance);

    // ── Identity ──────────────────────────────────────────────────────────────

    [Fact]
    public void TargetType_IsNomadConsul()
    {
        Target().TargetType.ShouldBe(DeploymentTargetType.NomadConsul);
    }

    [Fact]
    public void DisplayName_ContainsNomadAndConsul()
    {
        var name = Target().DisplayName;
        name.ToLower().ShouldContain("nomad");
        name.ToLower().ShouldContain("consul");
    }

    // ── GenerateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_WritesOneHclFilePerService()
    {
        using var dir = new Fixtures.TempDir();
        var paths = await Target().GenerateAsync(
            Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        paths.Count.ShouldBe(3);
        foreach (var p in paths)
        {
            File.Exists(p).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task GenerateAsync_FilesEndWith_JobHcl()
    {
        using var dir = new Fixtures.TempDir();
        var paths = await Target().GenerateAsync(
            Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        foreach (var p in paths)
        {
            p.ShouldEndWith(".job.hcl");
        }
    }

    [Fact]
    public async Task GenerateAsync_ProducesFiles_NamedAfterServices()
    {
        using var dir = new Fixtures.TempDir();
        await Target().GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        var files = Directory.GetFiles(dir.Path, "*.job.hcl")
            .Select(Path.GetFileName).ToList();
        files.ShouldContain("api.job.hcl");
        files.ShouldContain("worker.job.hcl");
        files.ShouldContain("redis.job.hcl");
    }

    [Fact]
    public async Task GenerateAsync_HclFiles_ContainBridgeNetworkMode()
    {
        using var dir = new Fixtures.TempDir();
        await Target().GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        foreach (var file in Directory.GetFiles(dir.Path, "*.job.hcl"))
        {
            (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).ShouldContain("mode = \"bridge\"");
        }
    }

    [Fact]
    public async Task GenerateAsync_HclFiles_ContainConnectBlock()
    {
        using var dir = new Fixtures.TempDir();
        // Only services with ports get a service+connect block; api has port 8080
        await Target().GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        var apiHcl = await File.ReadAllTextAsync(Path.Combine(dir.Path, "api.job.hcl"), TestContext.Current.CancellationToken);
        apiHcl.ShouldContain("connect {");
    }

    [Fact]
    public async Task GenerateAsync_WorkerHcl_ContainsUpstreamForApi()
    {
        using var dir = new Fixtures.TempDir();
        await Target().GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        var workerHcl = await File.ReadAllTextAsync(Path.Combine(dir.Path, "worker.job.hcl"), TestContext.Current.CancellationToken);
        workerHcl.ShouldContain("destination_name = \"api\"");
    }

    [Fact]
    public async Task GenerateAsync_IsIdempotent()
    {
        using var dir = new Fixtures.TempDir();
        var target = Target();
        await target.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);
        await target.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        Directory.GetFiles(dir.Path, "*.job.hcl").Length.ShouldBe(3);
    }

    // ── ValidateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_ReturnsOneResultPerService()
    {
        using var dir = new Fixtures.TempDir();
        var results = await Target().ValidateAsync(
            Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        results.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ValidateAsync_ArtifactNames_EndWith_JobHcl()
    {
        using var dir = new Fixtures.TempDir();
        var results = await Target().ValidateAsync(
            Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        foreach (var r in results)
        {
            r.ArtifactName.ShouldEndWith(".job.hcl");
        }
    }

    [Fact]
    public async Task ValidateAsync_IsValid_WhenNomadUnreachable()
    {
        // Nomad is not running in test environment; validate should not throw
        // and should treat connectivity failure as "no errors" (offline validation)
        using var dir = new Fixtures.TempDir();
        var act = async () => await Target().ValidateAsync(
            Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        await Should.NotThrowAsync(act);
    }

    // ── PlanAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlanAsync_WritesHclFilesBeforeQueryingCluster()
    {
        using var dir = new Fixtures.TempDir();
        // Nomad unreachable — plan catches the error and assumes all-new
        await Target().PlanAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        Directory.GetFiles(dir.Path, "*.job.hcl").Length.ShouldBe(3);
    }

    [Fact]
    public async Task PlanAsync_ReturnsAllServicesAsToCreate_WhenClusterUnreachable()
    {
        using var dir = new Fixtures.TempDir();
        // Use an address guaranteed to be unreachable so running = [] and all services are ToCreate
        var cfg = Fixtures.EnvConfig(consulCfg: Fixtures.DefaultConsulCfg(
            nomadAddr: "http://127.0.0.1:19999"));
        var result = await Target().PlanAsync(Fixtures.SimpleManifest(), cfg, dir.Path, TestContext.Current.CancellationToken);

        result.ToCreate.Count.ShouldBe(3);
        result.ToDelete.ShouldBeEmpty();
    }

    [Fact]
    public async Task PlanAsync_DoesNotThrow_WhenNomadUnreachable()
    {
        using var dir = new Fixtures.TempDir();
        var act = async () => await Target().PlanAsync(
            Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        await Should.NotThrowAsync(act);
    }

    // ── ApplyAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_ReturnsOneResultPerService()
    {
        using var dir = new Fixtures.TempDir();
        var results = await Target().ApplyAsync(
            Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        results.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ApplyAsync_ReturnsFailure_WithClearMessage_WhenHclFileMissing()
    {
        using var dir = new Fixtures.TempDir();
        var results = await Target().ApplyAsync(
            Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        foreach (var r in results)
        {
            r.Success.ShouldBeFalse();
            r.Error?.ShouldContain("plan");
        }
    }

    [Fact]
    public async Task ApplyAsync_AfterPlan_FindsHclFilesOnDisk()
    {
        using var dir = new Fixtures.TempDir();
        var target = Target();
        await target.PlanAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        // Apply will fail to reach Nomad, but should NOT fail with "run plan first"
        var results = await target.ApplyAsync(
            Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        foreach (var r in results)
        {
            r.Error?.ShouldNotContain("plan");
        }
    }

    [Fact]
    public async Task ApplyAsync_DoesNotThrow_WhenNomadUnreachable_AndFilesExist()
    {
        using var dir = new Fixtures.TempDir();
        var target = Target();
        await target.PlanAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        var act = async () => await target.ApplyAsync(
            Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        await Should.NotThrowAsync(act);
    }

    // ── Explain ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Explain_ReturnsOneExplanationPerService()
    {
        var explanations = await Target().ExplainAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), TestContext.Current.CancellationToken);
        explanations.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Explain_SetsServiceNames()
    {
        var explanations = await Target().ExplainAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), TestContext.Current.CancellationToken);
        explanations.Select(e => e.Name).ShouldBe(["api", "worker", "redis"], ignoreOrder: true);
    }

    [Fact]
    public async Task Explain_AdditionalInfo_ContainsNetworkMode()
    {
        var explanations = await Target().ExplainAsync(
            Fixtures.SingleService("api"), Fixtures.EnvConfig(), TestContext.Current.CancellationToken);

        explanations[0].AdditionalInfo["networkMode"].ShouldContain("bridge");
    }

    [Fact]
    public async Task Explain_AdditionalInfo_ContainsConsulAddress()
    {
        var cfg = Fixtures.EnvConfig(consulCfg: Fixtures.DefaultConsulCfg(
            consulAddr: "http://consul.prod:8500"));

        var explanations = await Target().ExplainAsync(Fixtures.SingleService("api"), cfg, TestContext.Current.CancellationToken);

        explanations[0].AdditionalInfo["consulAddress"].ShouldBe("http://consul.prod:8500");
    }

    [Fact]
    public async Task Explain_AdditionalInfo_ShowsUpstreams_ForWorker()
    {
        var explanations = await Target().ExplainAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), TestContext.Current.CancellationToken);
        var worker = explanations.Single(e => e.Name == "worker");
        worker.AdditionalInfo["upstreams"].ShouldContain("api");
    }

    [Fact]
    public async Task Explain_AdditionalInfo_ShowsNone_WhenNoUpstreams()
    {
        var explanations = await Target().ExplainAsync(
            Fixtures.SingleService("api"), Fixtures.EnvConfig(), TestContext.Current.CancellationToken);

        explanations[0].AdditionalInfo["upstreams"].ShouldBe("none");
    }

    [Fact]
    public async Task Explain_AdditionalInfo_IngressGateway_Disabled_WhenNotConfigured()
    {
        var explanations = await Target().ExplainAsync(
            Fixtures.SingleService("api"), Fixtures.EnvConfig(), TestContext.Current.CancellationToken);

        explanations[0].AdditionalInfo["ingressGateway"].ShouldBe("disabled");
    }

    [Fact]
    public async Task Explain_AdditionalInfo_IngressGateway_ShowsName_WhenEnabled()
    {
        var cfg = Fixtures.EnvConfig(consulCfg: Fixtures.DefaultConsulCfg(ingress: true));
        cfg.TargetConfig["nomadconsul"] = System.Text.Json.JsonSerializer
            .Deserialize<System.Text.Json.JsonElement>(
                System.Text.Json.JsonSerializer.Serialize(
                    Fixtures.DefaultConsulCfg(ingress: true)));

        var explanations = await Target().ExplainAsync(
            Fixtures.SingleService("api", "project.v0", 8080, "http", external: true), cfg, TestContext.Current.CancellationToken);

        explanations[0].AdditionalInfo["ingressGateway"].ShouldNotBe("disabled");
    }

    // ── GetDependencies ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetDependencies_ReturnsDependencyEdges()
    {
        var deps = await Target().GetDependenciesAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), TestContext.Current.CancellationToken);
        deps.ShouldContain(d => d.From == "api" && d.To == "worker");
    }

    [Fact]
    public async Task GetDependencies_ReturnsEmpty_WhenNoDependencies()
    {
        var deps = await Target().GetDependenciesAsync(Fixtures.SingleService("api"), Fixtures.EnvConfig(), TestContext.Current.CancellationToken);
        deps.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetDependencies_ReturnMultipleEdges_ForDependencyChain()
    {
        var deps = await Target().GetDependenciesAsync(
            Fixtures.ManifestWithDependencyChain(), Fixtures.EnvConfig(), TestContext.Current.CancellationToken);

        deps.Count.ShouldBe(3); // frontend→api, api→redis, api→db
        deps.ShouldContain(d => d.From == "api" && d.To == "frontend");
        deps.ShouldContain(d => d.From == "redis" && d.To == "api");
        deps.ShouldContain(d => d.From == "db" && d.To == "api");
    }

    // ── DiffAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DiffAsync_DoesNotThrow_WhenNomadUnreachable()
    {
        using var dir = new Fixtures.TempDir();
        var act = async () => await Target().DiffAsync(
            Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        await Should.NotThrowAsync(act);
    }

    // ── Config deserialization ────────────────────────────────────────────────

    [Fact]
    public async Task GetConfig_UsesDefaults_WhenTargetConfigEmpty()
    {
        // If no "nomadconsul" key exists, target should fall back to defaults
        var config = new EnvironmentConfig { Target = DeploymentTargetType.NomadConsul };
        var act = async () => await Target().ExplainAsync(Fixtures.SingleService("api"), config);
        await Should.NotThrowAsync(act);
    }

    [Fact]
    public async Task GetConfig_ReadsNomadAndConsulAddresses()
    {
        var consulCfg = new NomadConsulTargetConfig
        {
            NomadAddress = "http://nomad.prod:4646",
            ConsulAddress = "http://consul.prod:8500"
        };
        var config = Fixtures.EnvConfig(consulCfg: consulCfg);

        var explanations = await Target().ExplainAsync(Fixtures.SingleService("api"), config, TestContext.Current.CancellationToken);

        explanations[0].AdditionalInfo["consulAddress"].ShouldBe("http://consul.prod:8500");
    }
}
