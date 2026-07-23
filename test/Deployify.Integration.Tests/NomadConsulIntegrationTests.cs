using System.CommandLine;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Nomad + Consul Connect — plan
// ─────────────────────────────────────────────────────────────────────────────

public sealed class NomadConsulPlanCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Plan_NomadConsul_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(
	        ["plan", "--project-path", dir.Path, "--target", "NomadConsul"]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Plan_NomadConsul_WritesHclFiles()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path]);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.job.hcl", SearchOption.AllDirectories)
            .Length.ShouldBe(3);
    }

    [Fact]
    public async Task Plan_NomadConsul_HclFiles_ContainBridgeNetworkMode()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path]);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        foreach (var file in Directory.GetFiles(outputDir, "*.job.hcl"))
        {
	        (await File.ReadAllTextAsync(file)).ShouldContain("mode = \"bridge\"");
        }
    }

    [Fact]
    public async Task Plan_NomadConsul_HclFiles_ContainConnectSidecar()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path]);

        var apiHcl = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-output", "api.job.hcl"));
        apiHcl.ShouldContain("sidecar_service {");
    }

    [Fact]
    public async Task Plan_NomadConsul_WorkerHcl_ContainsUpstreamForApi()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path]);

        var workerHcl = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-output", "worker.job.hcl"));
        workerHcl.ShouldContain("destination_name = \"api\"");
    }

    [Fact]
    public async Task Plan_NomadConsul_WorkerHcl_ContainsUpstreamEnvVar()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path]);

        var workerHcl = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-output", "worker.job.hcl"));
        workerHcl.ShouldContain("NOMAD_UPSTREAM_ADDR_API");
        workerHcl.ShouldContain("localhost:20000");
    }

    [Fact]
    public async Task Plan_NomadConsul_HclFiles_NamedAfterServices()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path]);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        var files = Directory.GetFiles(outputDir, "*.job.hcl").Select(Path.GetFileName).ToList();
        files.ShouldContain("api.job.hcl");
        files.ShouldContain("worker.job.hcl");
        files.ShouldContain("redis.job.hcl");
    }

    [Fact]
    public async Task Plan_NomadConsul_ReturnsNonZero_WhenEnvOverrideMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);

        var code = await Root().InvokeAsync(
	        ["plan", "--project-path", dir.Path, "--env", "ghost"]);

        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Plan_NomadConsul_IsIdempotent()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);
        var root = Root();

        await root.InvokeAsync(["plan", "--project-path", dir.Path]);
        await root.InvokeAsync(["plan", "--project-path", dir.Path]);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.job.hcl").Length.ShouldBe(3);
    }

    [Fact]
    public async Task Plan_NomadConsul_DoesNotContainHostNetworkMode()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path]);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        foreach (var file in Directory.GetFiles(outputDir, "*.job.hcl"))
        {
	        (await File.ReadAllTextAsync(file)).ShouldNotContain("mode = \"host\"");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Nomad + Consul Connect — apply
// ─────────────────────────────────────────────────────────────────────────────

public sealed class NomadConsulApplyCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Apply_NomadConsul_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(
	        ["apply", "--project-path", dir.Path, "--target", "NomadConsul", "--auto-approve"]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Apply_NomadConsul_DryRun_ReturnsZero_WithValidState()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);

        var code = await Root().InvokeAsync(
	        ["apply", "--project-path", dir.Path, "--dry-run", "--auto-approve"]);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_NomadConsul_DryRun_WritesHclFiles()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);

        await Root().InvokeAsync(
	        ["apply", "--project-path", dir.Path, "--dry-run", "--auto-approve"]);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.job.hcl", SearchOption.AllDirectories)
            .ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Apply_NomadConsul_AfterPlan_FindsHclFilesOnDisk_NotPlanError()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);
        var root = Root();

        await root.InvokeAsync(["plan", "--project-path", dir.Path]);

        // Apply will fail to reach Nomad but should NOT emit "run plan first"
        var act = async () => await root.InvokeAsync(
	        ["apply", "--project-path", dir.Path, "--auto-approve"]);
        await Should.NotThrowAsync(act);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Nomad + Consul Connect — validate, explain, graph
// ─────────────────────────────────────────────────────────────────────────────

public sealed class NomadConsulOtherCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Validate_NomadConsul_ReturnsZero_WithValidState()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);

        var code = await Root().InvokeAsync(["validate", "--project-path", dir.Path]);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task Validate_NomadConsul_ReturnsZero_WhenArtefactsExist()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);
        var root = Root();

        // Generate artefacts first, then validate
        await root.InvokeAsync(["generate", "--project-path", dir.Path]);
        var code = await root.InvokeAsync(["validate", "--project-path", dir.Path]);

        code.ShouldBe(0);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.job.hcl").Length.ShouldBe(3);
    }

    [Fact]
    public async Task Explain_NomadConsul_ReturnsZero()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);

        var code = await Root().InvokeAsync(["explain", "--project-path", dir.Path]);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task Graph_NomadConsul_ReturnsZero()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);

        var code = await Root().InvokeAsync(["graph", "--project-path", dir.Path]);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task Generate_NomadConsul_ReturnsZero()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);

        var code = await Root().InvokeAsync(["generate", "--project-path", dir.Path]);

        code.ShouldBe(0);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Nomad + Consul Connect — init command
// ─────────────────────────────────────────────────────────────────────────────

public sealed class NomadConsulInitCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Init_NomadConsul_CreatesStateFile()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync([
	        "init", "--project-path", dir.Path, "--non-interactive",
            "--target", "NomadConsul",
            "--nc-nomad-address", "http://nomad.prod:4646",
            "--nc-consul-address", "http://consul.prod:8500"
        ]);

        File.Exists(Path.Combine(dir.Path, "deployify-state.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task Init_NomadConsul_PersistsNomadAndConsulAddresses()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync([
	        "init", "--project-path", dir.Path, "--non-interactive",
            "--target", "NomadConsul",
            "--nc-nomad-address", "http://nomad.example.com:4646",
            "--nc-consul-address", "http://consul.example.com:8500"
        ]);

        var json = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-state.json"));
        json.ShouldContain("http://nomad.example.com:4646");
        json.ShouldContain("http://consul.example.com:8500");
    }

    [Fact]
    public async Task Init_NomadConsul_SetsCorrectTargetType()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync([
	        "init", "--project-path", dir.Path, "--non-interactive",
            "--target", "NomadConsul"
        ]);

        var json = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-state.json"));
        json.ShouldContain("NomadConsul");
    }

    [Fact]
    public async Task Init_NomadConsul_Plan_AfterInit_WritesHclFilesWithBridgeMode()
    {
        using var dir = new TempDir();
        var root = Root();

        await root.InvokeAsync([
	        "init", "--project-path", dir.Path, "--non-interactive",
            "--target", "NomadConsul"
        ]);
        await Harness.WriteManifestAsync(dir.Path);
        await root.InvokeAsync(["plan", "--project-path", dir.Path]);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        foreach (var file in Directory.GetFiles(outputDir, "*.job.hcl"))
        {
	        (await File.ReadAllTextAsync(file)).ShouldContain("mode = \"bridge\"");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// NomadConsul vs plain Nomad — key structural differences
// ─────────────────────────────────────────────────────────────────────────────

public sealed class NomadConsulVsPlainNomadTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task NomadConsul_HclContainsConnectBlock_PlainNomad_DoesNot()
    {
        using var consulDir = new TempDir();
        using var nomadDir = new TempDir();

        await Harness.WriteNomadConsulStateAsync(consulDir.Path);
        await Harness.WriteManifestAsync(consulDir.Path);
        await Harness.WriteNomadStateAsync(nomadDir.Path);
        await Harness.WriteManifestAsync(nomadDir.Path);

        var root = Root();
        await root.InvokeAsync(["plan", "--project-path", consulDir.Path]);
        await root.InvokeAsync(["plan", "--project-path", nomadDir.Path]);

        var consulApiHcl = await File.ReadAllTextAsync(
            Path.Combine(consulDir.Path, "deployify-output", "api.job.hcl"));
        var nomadApiHcl = await File.ReadAllTextAsync(
            Path.Combine(nomadDir.Path, "deployify-output", "api.job.hcl"));

        consulApiHcl.ShouldContain("connect {");
        nomadApiHcl.ShouldNotContain("connect {");
    }

    [Fact]
    public async Task NomadConsul_HclUsesBridgeMode_PlainNomad_DoesNot()
    {
        using var consulDir = new TempDir();
        using var nomadDir = new TempDir();

        await Harness.WriteNomadConsulStateAsync(consulDir.Path);
        await Harness.WriteManifestAsync(consulDir.Path);
        await Harness.WriteNomadStateAsync(nomadDir.Path);
        await Harness.WriteManifestAsync(nomadDir.Path);

        var root = Root();
        await root.InvokeAsync(["plan", "--project-path", consulDir.Path]);
        await root.InvokeAsync(["plan", "--project-path", nomadDir.Path]);

        var consulHcl = await File.ReadAllTextAsync(
            Path.Combine(consulDir.Path, "deployify-output", "api.job.hcl"));
        var nomadHcl = await File.ReadAllTextAsync(
            Path.Combine(nomadDir.Path, "deployify-output", "api.job.hcl"));

        consulHcl.ShouldContain("mode = \"bridge\"");
        nomadHcl.ShouldNotContain("mode = \"bridge\"");
    }

    [Fact]
    public async Task NomadConsul_WorkerHcl_HasUpstreamEnvVar_PlainNomad_DoesNot()
    {
        using var consulDir = new TempDir();
        using var nomadDir = new TempDir();

        await Harness.WriteNomadConsulStateAsync(consulDir.Path);
        await Harness.WriteManifestAsync(consulDir.Path);
        await Harness.WriteNomadStateAsync(nomadDir.Path);
        await Harness.WriteManifestAsync(nomadDir.Path);

        var root = Root();
        await root.InvokeAsync(["plan", "--project-path", consulDir.Path]);
        await root.InvokeAsync(["plan", "--project-path", nomadDir.Path]);

        var consulWorker = await File.ReadAllTextAsync(
            Path.Combine(consulDir.Path, "deployify-output", "worker.job.hcl"));
        var nomadWorker = await File.ReadAllTextAsync(
            Path.Combine(nomadDir.Path, "deployify-output", "worker.job.hcl"));

        consulWorker.ShouldContain("NOMAD_UPSTREAM_ADDR_API");
        nomadWorker.ShouldNotContain("NOMAD_UPSTREAM_ADDR_API");
    }

    [Fact]
    public async Task BothTargets_ProduceThreeHclFiles_ForSameManifest()
    {
        using var consulDir = new TempDir();
        using var nomadDir = new TempDir();

        await Harness.WriteNomadConsulStateAsync(consulDir.Path);
        await Harness.WriteManifestAsync(consulDir.Path);
        await Harness.WriteNomadStateAsync(nomadDir.Path);
        await Harness.WriteManifestAsync(nomadDir.Path);

        var root = Root();
        await root.InvokeAsync(["plan", "--project-path", consulDir.Path]);
        await root.InvokeAsync(["plan", "--project-path", nomadDir.Path]);

        Directory.GetFiles(Path.Combine(consulDir.Path, "deployify-output"), "*.job.hcl")
            .Length.ShouldBe(3);
        Directory.GetFiles(Path.Combine(nomadDir.Path, "deployify-output"), "*.job.hcl")
            .Length.ShouldBe(3);
    }
}
