using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// GenerateCommand
// ─────────────────────────────────────────────────────────────────────────────

public sealed class GenerateCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Generate_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(["generate", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Generate_ReturnsZero_WithValidStateAndManifest()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(["generate", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task Generate_WritesArtifactFiles()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["generate", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.job.hcl").Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Generate_TwoRunsAtOnce_BothSucceed()
    {
        // Test classes run in parallel and share AnsiConsole. A Spectre spinner around the work
        // made the second concurrent run fail ("interactive functions concurrently").
        var ct = TestContext.Current.CancellationToken;
        using var a = new TempDir();
        using var b = new TempDir();
        foreach (var dir in new[] { a, b })
        {
            await Harness.WriteNomadStateAsync(dir.Path, ct: ct);
            await Harness.WriteManifestAsync(dir.Path, ct: ct);
        }

        var codes = await Task.WhenAll(
            Root().InvokeAsync(["generate", "--project-path", a.Path], ct),
            Root().InvokeAsync(["generate", "--project-path", b.Path], ct));

        codes.ShouldBe([0, 0]);
    }

    [Fact]
    public async Task Generate_ProducesOneFilePerService()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["generate", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.job.hcl").Length.ShouldBe(3);
    }

    [Fact]
    public async Task Generate_ReturnsNonZero_WhenEnvOverrideMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(
            ["generate", "--project-path", dir.Path, "--env", "ghost"], TestContext.Current.CancellationToken);

        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Generate_IsIdempotent()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var root = Root();

        await root.InvokeAsync(["generate", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        await root.InvokeAsync(["generate", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.job.hcl").Length.ShouldBe(3);
    }

    [Fact]
    public async Task Generate_WorksWithKubernetesTarget()
    {
        using var dir = new TempDir();
        await Harness.WriteKubernetesStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(["generate", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.yaml", SearchOption.AllDirectories).Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Generate_WorksWithComposeTarget()
    {
        using var dir = new TempDir();
        await Harness.WriteComposeStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(["generate", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        File.Exists(Path.Combine(outputDir, "compose.yml")).ShouldBeTrue();
    }

    [Fact]
    public async Task Generate_WorksWithNomadConsulTarget()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(["generate", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.job.hcl").Length.ShouldBe(3);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CiCommand
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CiCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Ci_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(["ci", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Ci_SkipApply_ReturnsZero_WithValidStateAndManifest()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(
            ["ci", "--project-path", dir.Path, "--skip-validate", "--skip-apply"], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task Ci_SkipApply_WritesArtifacts()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(
            ["ci", "--project-path", dir.Path, "--skip-validate", "--skip-apply"], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.job.hcl").Length.ShouldBe(3);
    }

    [Fact]
    public async Task Ci_SkipValidate_DoesNotRequireClusterReachable()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, address: "http://127.0.0.1:19999", ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        // With --skip-validate we skip the ping-dependent step
        var code = await Root().InvokeAsync(
            ["ci", "--project-path", dir.Path, "--skip-validate", "--skip-apply"], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task Ci_FailOnChanges_ReturnsNonZero_WhenPlanHasChanges()
    {
        using var dir = new TempDir();
        // Use unreachable Nomad so all services are "new" (ToCreate) in the plan
        await Harness.WriteNomadStateAsync(dir.Path, address: "http://127.0.0.1:19999", ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync([
            "ci", "--project-path", dir.Path,
            "--skip-validate", "--skip-apply", "--fail-on-changes"
        ], TestContext.Current.CancellationToken);

        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Ci_ReturnsNonZero_WhenEnvOverrideMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(
            ["ci", "--project-path", dir.Path, "--env", "ghost"], TestContext.Current.CancellationToken);

        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Ci_WorksWithKubernetesTarget()
    {
        using var dir = new TempDir();
        await Harness.WriteKubernetesStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync([
            "ci", "--project-path", dir.Path, "--skip-validate", "--skip-apply"
        ], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task Ci_WorksWithNomadConsulTarget()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync([
            "ci", "--project-path", dir.Path, "--skip-validate", "--skip-apply"
        ], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// WatchCommand (structural / non-blocking tests only)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class WatchCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Watch_ReturnsNonZero_WhenNoStateFile()
    {
        // With no state file, watch should fail at resolve and exit non-zero immediately.
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(["watch", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public void Watch_Command_IsRegistered()
    {
        var root = Root();
        root.Subcommands.Select(c => c.Name).ShouldContain("watch");
    }

    [Fact]
    public void Watch_Command_HasDebounceOption()
    {
        var root = Root();
        var watch = root.Subcommands.Single(c => c.Name == "watch");
        watch.Options.Select(o => o.Name).ShouldContain("--debounce");
    }

    [Fact]
    public void Watch_Command_HasNoGenerateOption()
    {
        var root = Root();
        var watch = root.Subcommands.Single(c => c.Name == "watch");
        watch.Options.Select(o => o.Name).ShouldContain("--no-generate");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Publisher — Podman socket detection (unit-level, no actual Docker/Podman needed)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class PublisherPodmanDetectionTests
{
    // These tests verify the config model and the runtime resolution logic
    // without actually spawning docker/podman processes.

    [Fact]
    public void ContainerRuntime_DefaultIsDocker()
    {
        new EnvironmentConfig().ContainerRuntime.ShouldBe(ContainerRuntime.Docker);
    }

    [Fact]
    public void ContainerRuntime_CanBeExplicitlySetToPodman()
    {
        var cfg = new EnvironmentConfig { ContainerRuntime = ContainerRuntime.Podman };
        cfg.ContainerRuntime.ShouldBe(ContainerRuntime.Podman);
    }

    [Fact]
    public void ContainerRuntime_Enum_HasDockerAndPodman()
    {
        var values = Enum.GetValues<ContainerRuntime>();
        values.ShouldContain(ContainerRuntime.Docker);
        values.ShouldContain(ContainerRuntime.Podman);
    }

    [Fact]
    public void ContainerRuntime_RoundTripsThrough_Json()
    {
        var state = new DeployifyStateFile
        {
            Environments = { ["prod"] = new EnvironmentConfig { ContainerRuntime = ContainerRuntime.Podman } }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var back = System.Text.Json.JsonSerializer.Deserialize<DeployifyStateFile>(json)!;
        back.Environments["prod"].ContainerRuntime.ShouldBe(ContainerRuntime.Podman);
    }

    [Fact]
    public void ContainerRuntime_Docker_RoundTripsThrough_Json()
    {
        var state = new DeployifyStateFile
        {
            Environments = { ["local"] = new EnvironmentConfig() }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var back = System.Text.Json.JsonSerializer.Deserialize<DeployifyStateFile>(json)!;
        back.Environments["local"].ContainerRuntime.ShouldBe(ContainerRuntime.Docker);
    }

    [Fact]
    public async Task Publisher_DoesNotThrow_WhenPublishingZeroProjectServices()
    {
        // A manifest with only container resources should publish nothing without error.
        var publisher = new Publisher(NullLogger<Publisher>.Instance);
        var manifest = new AspireManifest
        {
            Resources = new Dictionary<string, AspireResource>
            {
                ["redis"] = new() { Type = "container.v0", Image = "redis:7" }
            }
        };
        var config = new EnvironmentConfig { ContainerRuntime = ContainerRuntime.Docker };

        await Should.NotThrowAsync(() =>
            publisher.PublishAsync(manifest, config, "/tmp/fake-apphost", TestContext.Current.CancellationToken));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Generate → CI pipeline integration (end-to-end artifact flow)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class GenerateCiPipelineTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Generate_Then_Ci_SkipApply_Succeeds()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var root = Root();

        // First generate artifacts explicitly
        var genCode = await root.InvokeAsync(["generate", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        genCode.ShouldBe(0);

        // Then run CI (artifacts already present, no regeneration needed from manifest)
        var ciCode = await root.InvokeAsync([
            "ci", "--project-path", dir.Path, "--skip-validate", "--skip-apply"
        ], TestContext.Current.CancellationToken);
        ciCode.ShouldBe(0);
    }

    [Fact]
    public async Task Ci_ArtifactsArePresent_AfterSkipApplyRun()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync([
            "ci", "--project-path", dir.Path, "--skip-validate", "--skip-apply"
        ], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.job.hcl").Length.ShouldBe(3);
    }

    [Fact]
    public async Task Generate_ArtifactsMatchPlan_ArtifactsForNomad()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var root = Root();

        await root.InvokeAsync(["generate", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        await root.InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        var files = Directory.GetFiles(outputDir, "*.job.hcl").Select(Path.GetFileName).ToList();
        files.ShouldContain("api.job.hcl");
        files.ShouldContain("worker.job.hcl");
        files.ShouldContain("redis.job.hcl");
    }
}
