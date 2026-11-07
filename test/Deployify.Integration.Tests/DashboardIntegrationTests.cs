using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

public sealed class DashboardIntegrationTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    // ── Plan with dashboard disabled (default) ─────────────────────────────────

    [Fact]
    public async Task Plan_NoDashboard_ByDefault()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        var files = Directory.GetFiles(outputDir, "*.job.hcl").Select(Path.GetFileName);
        files.ShouldNotContain("aspire-dashboard.job.hcl");
    }

    // ── Plan with dashboard enabled ────────────────────────────────────────────

    [Fact]
    public async Task Plan_WritesDashboardHcl_WhenEnabled()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, deployDashboard: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        File.Exists(Path.Combine(outputDir, "aspire-dashboard.job.hcl")).ShouldBeTrue();
    }

    [Fact]
    public async Task Plan_DashboardHcl_ContainsDashboardImage()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, deployDashboard: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var hcl = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-output", "aspire-dashboard.job.hcl"), TestContext.Current.CancellationToken);
        hcl.ShouldContain("mcr.microsoft.com/dotnet/aspire-dashboard");
    }

    [Fact]
    public async Task Plan_DashboardHcl_ContainsOtlpEnvVar()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, deployDashboard: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var hcl = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-output", "aspire-dashboard.job.hcl"), TestContext.Current.CancellationToken);
        hcl.ShouldContain("DASHBOARD__OTLP__ENDPOINT_URL");
    }

    [Fact]
    public async Task Plan_WithDashboard_ProducesOriginalServicesPlus1()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, deployDashboard: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.job.hcl").Length.ShouldBe(4); // 3 services + dashboard
    }

    // ── Generate with dashboard ────────────────────────────────────────────────

    [Fact]
    public async Task Generate_WritesDashboardArtifact_WhenEnabled()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, deployDashboard: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(["generate", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        File.Exists(Path.Combine(outputDir, "aspire-dashboard.job.hcl")).ShouldBeTrue();
    }

    // ── Compose target with dashboard ──────────────────────────────────────────

    [Fact]
    public async Task Plan_Compose_DashboardServicePresent_WhenEnabled()
    {
        using var dir = new TempDir();
        await Harness.WriteComposeStateAsync(dir.Path, deployDashboard: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var composePath = Path.Combine(dir.Path, "deployify-output", "compose.yml");
        File.Exists(composePath).ShouldBeTrue();
        var yml = await File.ReadAllTextAsync(composePath, TestContext.Current.CancellationToken);
        yml.ShouldContain("aspire-dashboard");
        yml.ShouldContain("mcr.microsoft.com/dotnet/aspire-dashboard");
    }

    // ── NomadConsul target with dashboard ──────────────────────────────────────

    [Fact]
    public async Task Plan_NomadConsul_WritesDashboardHcl_WhenEnabled()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path, deployDashboard: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        File.Exists(Path.Combine(outputDir, "aspire-dashboard.job.hcl")).ShouldBeTrue();
    }

    // ── Init with --deploy-dashboard ──────────────────────────────────────────

    [Fact]
    public async Task Init_PersistsDeployDashboard_WhenFlagSet()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync([
            "init", "--project-path", dir.Path, "--non-interactive",
            "--target", "Nomad", "--deploy-dashboard"
        ], TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(Path.Combine(dir.Path, "deployify-state.json"), TestContext.Current.CancellationToken);
        json.ShouldContain("deployDashboard");
    }

    [Fact]
    public async Task Init_DashboardToken_PersistedInStateFile()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync([
            "init", "--project-path", dir.Path, "--non-interactive",
            "--target", "Nomad", "--deploy-dashboard", "--dashboard-token", "my-token"
        ], TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(Path.Combine(dir.Path, "deployify-state.json"), TestContext.Current.CancellationToken);
        json.ShouldContain("my-token");
    }

    // ── CI pipeline with dashboard ─────────────────────────────────────────────

    [Fact]
    public async Task Ci_SkipApply_WithDashboard_ReturnsZero()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, deployDashboard: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync([
            "ci", "--project-path", dir.Path, "--skip-validate", "--skip-apply"
        ], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
    }
}
