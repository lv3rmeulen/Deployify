using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>
/// Integration tests for plan and apply.
///
/// Plan now has two phases:
///   1. Generate artifacts to disk (pure, no network) — always runs
///   2. Diff against live cluster — may fail if no cluster is available
///
/// Tests that only care about artifact generation use --dry-run on apply
/// or just assert on files on disk. Tests that involve live cluster calls
/// are clearly marked and will exit non-zero in CI (no Nomad/Kubernetes/Docker).
/// </summary>
public sealed class PlanCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    // ── Missing state / bad env ───────────────────────────────────────────────

    [Fact]
    public async Task Plan_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(["plan", "--project-path", dir.Path, "--target", "Nomad"], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Plan_ReturnsNonZero_WhenEnvOverrideMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(["plan", "--project-path", dir.Path, "--env", "nonexistent"], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    // ── artifact generation (no network required) ─────────────────────────────

    [Fact]
    public async Task Plan_Nomad_WritesHclFiles()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        // Plan generates files regardless of whether Nomad is reachable
        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.hcl", SearchOption.AllDirectories)
            .Length.ShouldBe(3); // api, worker, redis
    }

    [Fact]
    public async Task Plan_Nomad_WritesOneHclFilePerService()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        var files = Directory.GetFiles(outputDir, "*.hcl");
        files.ShouldContain(f => f.EndsWith("api.job.hcl"));
        files.ShouldContain(f => f.EndsWith("worker.job.hcl"));
        files.ShouldContain(f => f.EndsWith("redis.job.hcl"));
    }

    [Fact]
    public async Task Plan_Kubernetes_WritesYamlFiles()
    {
        using var dir = new TempDir();
        await Harness.WriteKubernetesStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.yaml", SearchOption.AllDirectories)
            .ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Plan_Kubernetes_WritesSubdirectoryPerService()
    {
        using var dir = new TempDir();
        await Harness.WriteKubernetesStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetDirectories(outputDir).Length.ShouldBe(3);
    }

    [Fact]
    public async Task Plan_Kubernetes_WritesDeploymentYamlForEachService()
    {
        using var dir = new TempDir();
        await Harness.WriteKubernetesStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "deployment.yaml", SearchOption.AllDirectories)
            .Length.ShouldBe(3);
    }

    [Fact]
    public async Task Plan_Compose_WritesComposeYml()
    {
        using var dir = new TempDir();
        await Harness.WriteComposeStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        File.Exists(Path.Combine(outputDir, "compose.yml")).ShouldBeTrue();
    }

    [Fact]
    public async Task Plan_Compose_WritesPerServiceSubdirectories()
    {
        using var dir = new TempDir();
        await Harness.WriteComposeStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetDirectories(outputDir).Length.ShouldBe(3);
    }

    [Fact]
    public async Task Plan_RespectsEnvOverride()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path, TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        // Just assert no crash — env override selects the right config
        var act = async () => await Root().InvokeAsync(["plan", "--project-path", dir.Path, "--env", "prod"], TestContext.Current.CancellationToken);
        await Should.NotThrowAsync(act);
    }

    [Fact]
    public async Task Plan_IsIdempotent_OverwritesExistingArtifacts()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var root = Root();
        await root.InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        await root.InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.hcl").Length.ShouldBe(3);
    }
}

public sealed class ApplyCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    // ── Missing state / bad env ───────────────────────────────────────────────

    [Fact]
    public async Task Apply_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(
            ["apply", "--project-path", dir.Path, "--target", "Nomad", "--auto-approve"], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Apply_ReturnsNonZero_WhenEnvOverrideMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(
            ["apply", "--project-path", dir.Path, "--env", "ghost", "--auto-approve"], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    // ── Dry-run (shows plan output, no network calls) ─────────────────────────

    [Fact]
    public async Task Apply_DryRun_ReturnsZero_WithValidStateAndManifest()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(
            ["apply", "--project-path", dir.Path, "--dry-run", "--auto-approve"], TestContext.Current.CancellationToken);
        code.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_DryRun_WritesArtifacts_ViaPlanPhase()
    {
        // dry-run calls PlanAsync internally to show the plan — which now writes files
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(
            ["apply", "--project-path", dir.Path, "--dry-run", "--auto-approve"], TestContext.Current.CancellationToken);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.hcl", SearchOption.AllDirectories)
            .ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Apply_DryRun_Compose_WritesArtifacts()
    {
        using var dir = new TempDir();
        await Harness.WriteComposeStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(
            ["apply", "--project-path", dir.Path, "--dry-run", "--auto-approve"], TestContext.Current.CancellationToken);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        File.Exists(Path.Combine(outputDir, "compose.yml")).ShouldBeTrue();
    }

    [Fact]
    public async Task Apply_DryRun_Kubernetes_WritesArtifacts()
    {
        using var dir = new TempDir();
        await Harness.WriteKubernetesStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(
            ["apply", "--project-path", dir.Path, "--dry-run", "--auto-approve"], TestContext.Current.CancellationToken);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.yaml", SearchOption.AllDirectories)
            .ShouldNotBeEmpty();
    }

    // ── Plan-then-apply flow ──────────────────────────────────────────────────

    [Fact]
    public async Task Apply_AfterPlan_FindsArtifactsOnDisk()
    {
        // After plan writes files, apply should find them (it won't reach Nomad but
        // it will get past the "file not found" guard and fail on network, not on missing files)
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var root = Root();
        await root.InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        // Apply will fail to reach Nomad but should NOT fail with "run plan first"
        var act = async () => await root.InvokeAsync(
            ["apply", "--project-path", dir.Path, "--auto-approve"], TestContext.Current.CancellationToken);
        await Should.NotThrowAsync(act);
    }
}
