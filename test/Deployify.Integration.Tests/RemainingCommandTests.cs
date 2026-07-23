using System.CommandLine;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

public sealed class ValidateCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Validate_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(["validate", "--project-path", dir.Path]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Validate_ReturnsNonZero_WhenEnvOverrideMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        var code = await Root().InvokeAsync(["validate", "--project-path", dir.Path, "--env", "ghost"]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Validate_ReturnsNonZero_WhenPingFails()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, address: "http://127.0.0.1:19999");
        await Harness.WriteManifestAsync(dir.Path);
        var code = await Root().InvokeAsync(["validate", "--project-path", dir.Path]);
        code.ShouldNotBe(0);
    }
}

public sealed class DiffCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Diff_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(["diff", "--project-path", dir.Path]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Diff_ReturnsNonZero_WhenEnvOverrideMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        var code = await Root().InvokeAsync(["diff", "--project-path", dir.Path, "--env", "ghost"]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Diff_DoesNotThrow_WithValidStateAndManifest()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);
        var act = async () => await Root().InvokeAsync(["diff", "--project-path", dir.Path]);
        await Should.NotThrowAsync(act);
    }
}

public sealed class ExplainCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Explain_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(["explain", "--project-path", dir.Path]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Explain_ReturnsZero_WithValidStateAndManifest()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);
        var code = await Root().InvokeAsync(["explain", "--project-path", dir.Path]);
        code.ShouldBe(0);
    }

    [Fact]
    public async Task Explain_ServiceFilter_ReturnsZero()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);
        var code = await Root().InvokeAsync(["explain", "--project-path", dir.Path, "--service", "api"]);
        code.ShouldBe(0);
    }

    [Fact]
    public async Task Explain_UnknownServiceFilter_ReturnsZero()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);
        var code = await Root().InvokeAsync(["explain", "--project-path", dir.Path, "--service", "nonexistent"]);
        code.ShouldBe(0);
    }

    [Fact]
    public async Task Explain_ReturnsNonZero_WhenEnvOverrideMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        var code = await Root().InvokeAsync(["explain", "--project-path", dir.Path, "--env", "ghost"]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Explain_WorksForKubernetesTarget()
    {
        using var dir = new TempDir();
        await Harness.WriteK8SStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);
        var code = await Root().InvokeAsync(["explain", "--project-path", dir.Path]);
        code.ShouldBe(0);
    }

    [Fact]
    public async Task Explain_WorksForComposeTarget()
    {
        using var dir = new TempDir();
        await Harness.WriteComposeStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);
        var code = await Root().InvokeAsync(["explain", "--project-path", dir.Path]);
        code.ShouldBe(0);
    }
}

public sealed class GraphCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Graph_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(["graph", "--project-path", dir.Path]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Graph_ReturnsZero_WithValidStateAndManifest()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);
        var code = await Root().InvokeAsync(["graph", "--project-path", dir.Path]);
        code.ShouldBe(0);
    }

    [Fact]
    public async Task Graph_ReturnsNonZero_WhenEnvOverrideMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        var code = await Root().InvokeAsync(["graph", "--project-path", dir.Path, "--env", "ghost"]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Graph_ReturnsZero_WhenNoServiceDependencies()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.CreateDirectory(outputDir);
        var noDepManifest = System.Text.Json.JsonSerializer.Serialize(new
        {
            resources = new
            {
                api = new { type = "project.v0" },
                redis = new { type = "container.v0", image = "redis:7" }
            }
        });
        await File.WriteAllTextAsync(Path.Combine(outputDir, "manifest.json"), noDepManifest);
        var code = await Root().InvokeAsync(["graph", "--project-path", dir.Path]);
        code.ShouldBe(0);
    }
}

public sealed class PublishCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Publish_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(["publish", "--project-path", dir.Path]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Publish_DryRun_ReturnsZero_WithValidState()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);
        var code = await Root().InvokeAsync(["publish", "--project-path", dir.Path, "--dry-run"]);
        code.ShouldBe(0);
    }

    [Fact]
    public async Task Publish_DryRun_WithImageTagOverride_ReturnsZero()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        await Harness.WriteManifestAsync(dir.Path);
        var code = await Root().InvokeAsync(
	        ["publish", "--project-path", dir.Path, "--dry-run", "--image-tag", "sha-abc123"]);
        code.ShouldBe(0);
    }

    [Fact]
    public async Task Publish_ReturnsNonZero_WhenEnvOverrideMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        var code = await Root().InvokeAsync(
	        ["publish", "--project-path", dir.Path, "--env", "ghost", "--dry-run"]);
        code.ShouldNotBe(0);
    }
}
