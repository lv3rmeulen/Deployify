using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

public sealed class DaprIntegrationTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    // ── Dapr disabled by default ──────────────────────────────────────────────

    [Fact]
    public async Task Plan_Nomad_NoDaprSidecar_ByDefault()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        foreach (var file in Directory.GetFiles(outputDir, "*.job.hcl"))
        {
            (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).ShouldNotContain("daprio/daprd");
        }
    }

    // ── Nomad + Dapr ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_Nomad_WritesDaprSidecarTask_WhenEnabled()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, deployDapr: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var apiHcl = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-output", "api.job.hcl"), TestContext.Current.CancellationToken);
        apiHcl.ShouldContain("daprio/daprd");
        apiHcl.ShouldContain("api-dapr");
        apiHcl.ShouldContain("lifecycle");
        apiHcl.ShouldContain("sidecar = true");
    }

    [Fact]
    public async Task Plan_Nomad_DaprSidecar_ContainsDaprHttpPort()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, deployDapr: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var apiHcl = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-output", "api.job.hcl"), TestContext.Current.CancellationToken);
        apiHcl.ShouldContain("--dapr-http-port");
    }

    [Fact]
    public async Task Plan_Nomad_DaprSidecar_ContainsAppId()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, deployDapr: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var apiHcl = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-output", "api.job.hcl"), TestContext.Current.CancellationToken);
        apiHcl.ShouldContain("--app-id");
        apiHcl.ShouldContain("\"api\"");
    }

    [Fact]
    public async Task Plan_Nomad_DaprEnabled_StillProducesCorrectFileCount()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, deployDapr: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        Directory.GetFiles(Path.Combine(dir.Path, "deployify-output"), "*.job.hcl")
            .Length.ShouldBe(3); // Still 3 files — sidecar is a task inside the same job
    }

    // ── NomadConsul + Dapr ────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_NomadConsul_WritesDaprSidecarTask_WhenEnabled()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadConsulStateAsync(dir.Path, deployDapr: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var apiHcl = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-output", "api.job.hcl"), TestContext.Current.CancellationToken);
        apiHcl.ShouldContain("daprio/daprd");
        apiHcl.ShouldContain("sidecar = true");
    }

    // ── Compose + Dapr ────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_Compose_WritesDaprSidecarService_WhenEnabled()
    {
        using var dir = new TempDir();
        await Harness.WriteComposeStateAsync(dir.Path, deployDapr: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var yml = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-output", "compose.yml"), TestContext.Current.CancellationToken);
        yml.ShouldContain("daprio/daprd");
        yml.ShouldContain("api-dapr");
    }

    [Fact]
    public async Task Plan_Compose_DaprSidecar_UsesNetworkModeService()
    {
        using var dir = new TempDir();
        await Harness.WriteComposeStateAsync(dir.Path, deployDapr: true, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var yml = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-output", "compose.yml"), TestContext.Current.CancellationToken);
        yml.ShouldContain("network_mode: service:api");
    }

    // ── Init --deploy-dapr ────────────────────────────────────────────────────

    [Fact]
    public async Task Init_PersistsDeployDapr_WhenFlagSet()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync([
            "init", "--project-path", dir.Path, "--non-interactive",
            "--target", "Nomad", "--deploy-dapr"
        ], TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-state.json"), TestContext.Current.CancellationToken);
        json.ShouldContain("deployDapr");
    }

    [Fact]
    public async Task Init_DaprVersion_PersistedInStateFile()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync([
            "init", "--project-path", dir.Path, "--non-interactive",
            "--target", "Nomad", "--deploy-dapr", "--dapr-version", "1.13"
        ], TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(
            Path.Combine(dir.Path, "deployify-state.json"), TestContext.Current.CancellationToken);
        json.ShouldContain("1.13");
    }

    // ── ManifestReader — unescaped ] fix ──────────────────────────────────────

    [Fact]
    public async Task ManifestReader_HandlesTrailingCommas()
    {
        using var dir = new TempDir();
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.CreateDirectory(outputDir);
        // Manifest with trailing comma — allowed by AllowTrailingCommas
        await File.WriteAllTextAsync(Path.Combine(outputDir, "manifest.json"), """
            {
              "resources": {
                "api": {
                  "type": "project.v0",
                  "path": "Api/Api.csproj",
                }
              },
            }
            """, TestContext.Current.CancellationToken);

        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(
            ["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task ManifestReader_HandlesUnknownTopLevelProperties()
    {
        using var dir = new TempDir();
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.CreateDirectory(outputDir);
        // Manifest with $schema and extra unknown properties
        await File.WriteAllTextAsync(Path.Combine(outputDir, "manifest.json"), """
            {
              "$schema": "https://json.schemastore.org/aspire-8.0.json",
              "resources": {
                "api": {
                  "type": "project.v0",
                  "path": "Api/Api.csproj",
                  "inputs": { "someInput": { "type": "string" } }
                }
              }
            }
            """, TestContext.Current.CancellationToken);

        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(
            ["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
    }
}
