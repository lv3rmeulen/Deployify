using System.CommandLine;
using Deployify.Commands;
using Deployify.Core.Services;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

public sealed class EnvCommandTests
{
    private static RootCommand Root() => new("t") { EnvCommand.Build() };

    // ── env list ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnvList_ReturnsZero_WhenStateExists()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        var code = await Root().InvokeAsync(["env", "list", "--project-path", dir.Path]);
        code.ShouldBe(0);
    }

    [Fact]
    public async Task EnvList_ShowsAllEnvironments()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path);
        // just assert exit code — output goes to AnsiConsole (not easily captured here)
        var code = await Root().InvokeAsync(["env", "list", "--project-path", dir.Path]);
        code.ShouldBe(0);
    }

    // ── env use ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnvUse_SwitchesActiveEnvironment()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path);
        await Root().InvokeAsync(["env", "use", "prod", "--project-path", dir.Path]);
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.ActiveEnvironment.ShouldBe("prod");
    }

    [Fact]
    public async Task EnvUse_ReturnsNonZero_WhenEnvMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        var code = await Root().InvokeAsync(["env", "use", "ghost", "--project-path", dir.Path]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task EnvUse_DoesNotChange_WhenEnvMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, envName: "local");
        await Root().InvokeAsync(["env", "use", "nonexistent", "--project-path", dir.Path]);
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.ActiveEnvironment.ShouldBe("local");
    }

    // ── env add ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnvAdd_AddsNewEnvironment()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        await Root().InvokeAsync(
            $"env add staging --project-path {dir.Path} --non-interactive " +
            $"--target Nomad --nomad-address http://staging:4646");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.Environments.ShouldContainKey("staging");
    }

    [Fact]
    public async Task EnvAdd_ActivatesEnv_WhenFlagSet()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        await Root().InvokeAsync(
            $"env add prod --project-path {dir.Path} --non-interactive " +
            $"--target Nomad --activate");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.ActiveEnvironment.ShouldBe("prod");
    }

    [Fact]
    public async Task EnvAdd_DoesNotActivate_WhenFlagNotSet()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, envName: "local");
        await Root().InvokeAsync(
            $"env add staging --project-path {dir.Path} --non-interactive --target Nomad");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.ActiveEnvironment.ShouldBe("local");
    }

    [Fact]
    public async Task EnvAdd_ReturnsNonZero_WhenEnvAlreadyExists()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, envName: "local");
        var code = await Root().InvokeAsync(
            $"env add local --project-path {dir.Path} --non-interactive --target Nomad");
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task EnvAdd_PersistsKubernetesConfig()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        await Root().InvokeAsync(
            $"env add k8s-prod --project-path {dir.Path} --non-interactive " +
            $"--target Kubernetes --k8s-namespace-prefix myapp --k8s-ingress-domain apps.example.com");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        var cfg = sf.Environments["k8s-prod"];
        cfg.Target.ShouldBe(Core.Models.DeploymentTargetType.Kubernetes);
        cfg.TargetConfig.ShouldContainKey("kubernetes");
    }

    [Fact]
    public async Task EnvAdd_PersistsComposeConfig()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        await Root().InvokeAsync(
            $"env add compose-dev --project-path {dir.Path} --non-interactive " +
            $"--target Compose --compose-project mycompose");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        var cfg = sf.Environments["compose-dev"];
        cfg.Target.ShouldBe(Core.Models.DeploymentTargetType.Compose);
        cfg.TargetConfig.ShouldContainKey("compose");
    }

    [Fact]
    public async Task EnvAdd_PersistsContainerRegistry()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        await Root().InvokeAsync(
            $"env add staging --project-path {dir.Path} --non-interactive " +
            $"--target Nomad --container-registry reg.io/myapp");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.Environments["staging"].ContainerRegistry.ShouldBe("reg.io/myapp");
    }

    // ── env set ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnvSet_UpdatesContainerRegistry()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        await Root().InvokeAsync(
            $"env set local --project-path {dir.Path} --container-registry new.reg.io/app");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.Environments["local"].ContainerRegistry.ShouldBe("new.reg.io/app");
    }

    [Fact]
    public async Task EnvSet_UpdatesImageTag()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        await Root().InvokeAsync(
            $"env set local --project-path {dir.Path} --image-tag v9.9.9");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.Environments["local"].ImageTag.ShouldBe("v9.9.9");
    }

    [Fact]
    public async Task EnvSet_ReturnsNonZero_WhenEnvMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        var code = await Root().InvokeAsync(
            $"env set ghost --project-path {dir.Path} --image-tag v1");
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task EnvSet_ReturnsZero_WhenNoChangesSpecified()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        // set with no mutation options — should exit 0 but print "no changes"
        var code = await Root().InvokeAsync(
            $"env set local --project-path {dir.Path}");
        code.ShouldBe(0);
    }

    [Fact]
    public async Task EnvSet_DoesNotModifyOtherEnvironments()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path);
        await Root().InvokeAsync(
            $"env set local --project-path {dir.Path} --image-tag changed");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.Environments["prod"].ImageTag.ShouldNotBe("changed");
    }

    // ── env remove ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnvRemove_RemovesEnvironment()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path);
        await Root().InvokeAsync(["env", "remove", "prod", "--project-path", dir.Path, "--force"]);
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.Environments.ShouldNotContainKey("prod");
    }

    [Fact]
    public async Task EnvRemove_ReturnsNonZero_WhenEnvMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        var code = await Root().InvokeAsync(["env", "remove", "ghost", "--project-path", dir.Path, "--force"]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task EnvRemove_ReturnsNonZero_WhenOnlyEnv()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path);
        var code = await Root().InvokeAsync(["env", "remove", "local", "--project-path", dir.Path, "--force"]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task EnvRemove_ReturnsNonZero_WhenRemovingActiveEnv()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path); // active = local
        var code = await Root().InvokeAsync(["env", "remove", "local", "--project-path", dir.Path, "--force"]);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task EnvRemove_DoesNotRemoveActive_LeavesOthersIntact()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path);
        await Root().InvokeAsync(["env", "remove", "local", "--project-path", dir.Path, "--force"]);
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.Environments.ShouldContainKey("local"); // unchanged — active, can't remove
    }
}
