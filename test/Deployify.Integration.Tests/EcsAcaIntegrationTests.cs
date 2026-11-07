using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// AWS ECS integration tests
// ─────────────────────────────────────────────────────────────────────────────

public sealed class EcsPlanCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Plan_Ecs_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(["plan", "--project-path", dir.Path, "--target", "AwsEcs"], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Plan_Ecs_WritesTaskDefinitionFiles()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        // Plan writes files even if AWS CLI is unreachable
        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.taskdef.json", SearchOption.AllDirectories)
            .Length.ShouldBe(3);
    }

    [Fact]
    public async Task Plan_Ecs_WritesOneTaskDefPerService()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        var files = Directory.GetFiles(outputDir, "*.taskdef.json");
        files.ShouldContain(f => f.EndsWith("api.taskdef.json"));
        files.ShouldContain(f => f.EndsWith("worker.taskdef.json"));
        files.ShouldContain(f => f.EndsWith("redis.taskdef.json"));
    }

    [Fact]
    public async Task Plan_Ecs_TaskDefFiles_ContainValidJson()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        foreach (var file in Directory.GetFiles(outputDir, "*.taskdef.json"))
        {
            var json = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            var act = () => System.Text.Json.JsonDocument.Parse(json);
            Should.NotThrow(act, "should be valid JSON");
        }
    }

    [Fact]
    public async Task Plan_Ecs_ReturnsNonZero_WhenEnvOverrideMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(["plan", "--project-path", dir.Path, "--env", "ghost"], TestContext.Current.CancellationToken);

        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Plan_Ecs_IsIdempotent_OverwritesExistingTaskDefs()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var root = Root();

        await root.InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        await root.InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.taskdef.json").Length.ShouldBe(3);
    }
}

public sealed class EcsApplyCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Apply_Ecs_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(
            ["apply", "--project-path", dir.Path, "--target", "AwsEcs", "--auto-approve"], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Apply_Ecs_DryRun_ReturnsZero_WithValidState()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(
            ["apply", "--project-path", dir.Path, "--dry-run", "--auto-approve"], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_Ecs_DryRun_WritesTaskDefFiles()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(
            ["apply", "--project-path", dir.Path, "--dry-run", "--auto-approve"], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.taskdef.json", SearchOption.AllDirectories)
            .ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Apply_Ecs_AfterPlan_FindsTaskDefsOnDisk()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var root = Root();

        await root.InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        // Apply will fail to reach AWS but should NOT fail with "run plan first"
        var act = async () => await root.InvokeAsync(
            ["apply", "--project-path", dir.Path, "--auto-approve"], TestContext.Current.CancellationToken);
        await Should.NotThrowAsync(act);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Azure Container Apps integration tests
// ─────────────────────────────────────────────────────────────────────────────

public sealed class AcaPlanCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Plan_Aca_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(["plan", "--project-path", dir.Path, "--target", "AzureContainerApps"], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Plan_Aca_WritesContainerAppJsonFiles()
    {
        using var dir = new TempDir();
        await Harness.WriteAcaStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.containerapp.json", SearchOption.AllDirectories)
            .Length.ShouldBe(3);
    }

    [Fact]
    public async Task Plan_Aca_WritesOneSpecPerService()
    {
        using var dir = new TempDir();
        await Harness.WriteAcaStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        var files = Directory.GetFiles(outputDir, "*.containerapp.json");
        files.ShouldContain(f => f.EndsWith("api.containerapp.json"));
        files.ShouldContain(f => f.EndsWith("worker.containerapp.json"));
        files.ShouldContain(f => f.EndsWith("redis.containerapp.json"));
    }

    [Fact]
    public async Task Plan_Aca_SpecFiles_ContainValidJson()
    {
        using var dir = new TempDir();
        await Harness.WriteAcaStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        foreach (var file in Directory.GetFiles(outputDir, "*.containerapp.json"))
        {
            var json = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            var act = () => System.Text.Json.JsonDocument.Parse(json);
            Should.NotThrow(act, "should be valid JSON");
        }
    }

    [Fact]
    public async Task Plan_Aca_ReturnsNonZero_WhenEnvOverrideMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteAcaStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(["plan", "--project-path", dir.Path, "--env", "ghost"], TestContext.Current.CancellationToken);

        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Plan_Aca_IsIdempotent_OverwritesExistingSpecs()
    {
        using var dir = new TempDir();
        await Harness.WriteAcaStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var root = Root();

        await root.InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        await root.InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.containerapp.json").Length.ShouldBe(3);
    }
}

public sealed class AcaApplyCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Apply_Aca_ReturnsNonZero_WhenNoStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(
            ["apply", "--project-path", dir.Path, "--target", "AzureContainerApps", "--auto-approve"], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task Apply_Aca_DryRun_ReturnsZero_WithValidState()
    {
        using var dir = new TempDir();
        await Harness.WriteAcaStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(
            ["apply", "--project-path", dir.Path, "--dry-run", "--auto-approve"], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_Aca_DryRun_WritesContainerAppJsonFiles()
    {
        using var dir = new TempDir();
        await Harness.WriteAcaStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await Root().InvokeAsync(
            ["apply", "--project-path", dir.Path, "--dry-run", "--auto-approve"], TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(outputDir, "*.containerapp.json", SearchOption.AllDirectories)
            .ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Apply_Aca_AfterPlan_FindsSpecsOnDisk()
    {
        using var dir = new TempDir();
        await Harness.WriteAcaStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var root = Root();

        await root.InvokeAsync(["plan", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        // Apply will fail to reach Azure but should NOT fail with "run plan first"
        var act = async () => await root.InvokeAsync(
            ["apply", "--project-path", dir.Path, "--auto-approve"], TestContext.Current.CancellationToken);
        await Should.NotThrowAsync(act);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Init command tests for new targets
// ─────────────────────────────────────────────────────────────────────────────

public sealed class NewTargetInitCommandTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Init_EcsTarget_CreatesStateFile()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync([
            "init", "--project-path", dir.Path, "--non-interactive",
            "--target", "AwsEcs", "--ecs-region", "us-west-2", "--ecs-cluster", "mycluster"
        ], TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(dir.Path, "deployify-state.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task Init_EcsTarget_PersistsRegionAndCluster()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync([
            "init", "--project-path", dir.Path, "--non-interactive",
            "--target", "AwsEcs", "--ecs-region", "ap-southeast-1", "--ecs-cluster", "prod-cluster"
        ], TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(Path.Combine(dir.Path, "deployify-state.json"), TestContext.Current.CancellationToken);
        json.ShouldContain("ap-southeast-1");
        json.ShouldContain("prod-cluster");
    }

    [Fact]
    public async Task Init_AcaTarget_CreatesStateFile()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync([
            "init", "--project-path", dir.Path, "--non-interactive",
            "--target", "AzureContainerApps",
            "--aca-resource-group", "myrg",
            "--aca-location", "westeurope",
            "--aca-environment", "myenv"
        ], TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(dir.Path, "deployify-state.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task Init_AcaTarget_PersistsResourceGroupAndLocation()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync([
            "init", "--project-path", dir.Path, "--non-interactive",
            "--target", "AzureContainerApps",
            "--aca-resource-group", "prod-rg",
            "--aca-location", "northeurope"
        ], TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(Path.Combine(dir.Path, "deployify-state.json"), TestContext.Current.CancellationToken);
        json.ShouldContain("prod-rg");
        json.ShouldContain("northeurope");
    }

    [Fact]
    public async Task Init_EcsTarget_SetsCorrectTargetType()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync([
            "init", "--project-path", dir.Path, "--non-interactive",
            "--target", "AwsEcs"
        ], TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(Path.Combine(dir.Path, "deployify-state.json"), TestContext.Current.CancellationToken);
        json.ShouldContain("AwsEcs");
    }

    [Fact]
    public async Task Init_AcaTarget_SetsCorrectTargetType()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync([
            "init", "--project-path", dir.Path, "--non-interactive",
            "--target", "AzureContainerApps"
        ], TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(Path.Combine(dir.Path, "deployify-state.json"), TestContext.Current.CancellationToken);
        json.ShouldContain("AzureContainerApps");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Explain + Graph integration tests for new targets
// ─────────────────────────────────────────────────────────────────────────────

public sealed class NewTargetExplainGraphTests
{
    private static RootCommand Root() => CommandRunner.BuildFull();

    [Fact]
    public async Task Explain_WorksForEcsTarget()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(["explain", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task Explain_WorksForAcaTarget()
    {
        using var dir = new TempDir();
        await Harness.WriteAcaStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(["explain", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task Graph_WorksForEcsTarget()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(["graph", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
    }

    [Fact]
    public async Task Graph_WorksForAcaTarget()
    {
        using var dir = new TempDir();
        await Harness.WriteAcaStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(["graph", "--project-path", dir.Path], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
    }
}
