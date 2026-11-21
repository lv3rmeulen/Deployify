using System.CommandLine;
using System.Text.Json;
using System.Threading.Tasks;
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
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(["env", "list", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        code.ShouldBe(0);
    }

    [Fact]
    public async Task EnvList_ShowsAllEnvironments()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path, TestContext.Current.CancellationToken);
        // just assert exit code — output goes to AnsiConsole (not easily captured here)
        var code = await Root().InvokeAsync(["env", "list", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        code.ShouldBe(0);
    }

    // ── env use ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnvUse_SwitchesActiveEnvironment()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path, TestContext.Current.CancellationToken);
        await Root().InvokeAsync(["env", "use", "prod", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.ActiveEnvironment.ShouldBe("prod");
    }

    [Fact]
    public async Task EnvUse_ReturnsNonZero_WhenEnvMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(["env", "use", "ghost", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task EnvUse_DoesNotChange_WhenEnvMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, envName: "local", ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(["env", "use", "nonexistent", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.ActiveEnvironment.ShouldBe("local");
    }

    // ── env add ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnvAdd_AddsNewEnvironment()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(
            $"env add staging --project-path {dir.Path} --non-interactive " +
            $"--target Nomad --nomad-address http://staging:4646", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.Environments.ShouldContainKey("staging");
    }

    [Fact]
    public async Task EnvAdd_ActivatesEnv_WhenFlagSet()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(
            $"env add prod --project-path {dir.Path} --non-interactive " +
            $"--target Nomad --activate", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.ActiveEnvironment.ShouldBe("prod");
    }

    [Fact]
    public async Task EnvAdd_DoesNotActivate_WhenFlagNotSet()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, envName: "local", ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(
            $"env add staging --project-path {dir.Path} --non-interactive --target Nomad", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.ActiveEnvironment.ShouldBe("local");
    }

    [Fact]
    public async Task EnvAdd_ReturnsNonZero_WhenEnvAlreadyExists()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, envName: "local", ct: TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(
            $"env add local --project-path {dir.Path} --non-interactive --target Nomad", TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task EnvAdd_PersistsKubernetesConfig()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(
            $"env add kubernetes-prod --project-path {dir.Path} --non-interactive " +
            $"--target Kubernetes --kubernetes-namespace-prefix myapp --kubernetes-ingress-domain apps.example.com", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        var cfg = sf.Environments["kubernetes-prod"];
        cfg.Target.ShouldBe(Core.Models.DeploymentTargetType.Kubernetes);
        cfg.TargetConfig.ShouldContainKey("kubernetes");
    }

    [Fact]
    public async Task EnvAdd_PersistsComposeConfig()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(
            $"env add compose-dev --project-path {dir.Path} --non-interactive " +
            $"--target Compose --compose-project mycompose", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        var cfg = sf.Environments["compose-dev"];
        cfg.Target.ShouldBe(Core.Models.DeploymentTargetType.Compose);
        cfg.TargetConfig.ShouldContainKey("compose");
    }

    [Fact]
    public async Task EnvAdd_PersistsContainerRegistry()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(
            $"env add staging --project-path {dir.Path} --non-interactive " +
            $"--target Nomad --container-registry reg.io/myapp", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.Environments["staging"].ContainerRegistry.ShouldBe("reg.io/myapp");
    }

    // ── env set ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnvSet_UpdatesContainerRegistry()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(
            $"env set local --project-path {dir.Path} --container-registry new.reg.io/app", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.Environments["local"].ContainerRegistry.ShouldBe("new.reg.io/app");
    }

    [Fact]
    public async Task EnvSet_UpdatesImageTag()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(
            $"env set local --project-path {dir.Path} --image-tag v9.9.9", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.Environments["local"].ImageTag.ShouldBe("v9.9.9");
    }

    [Fact]
    public async Task EnvSet_ReturnsNonZero_WhenEnvMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(
            $"env set ghost --project-path {dir.Path} --image-tag v1", TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task EnvSet_ReturnsZero_WhenNoChangesSpecified()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        // set with no mutation options — should exit 0 but print "no changes"
        var code = await Root().InvokeAsync(
            $"env set local --project-path {dir.Path}", TestContext.Current.CancellationToken);
        code.ShouldBe(0);
    }

    [Fact]
    public async Task EnvSet_DoesNotModifyOtherEnvironments()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path, TestContext.Current.CancellationToken);
        await Root().InvokeAsync(
            $"env set local --project-path {dir.Path} --image-tag changed", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.Environments["prod"].ImageTag.ShouldNotBe("changed");
    }

    // ── env remove ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnvRemove_RemovesEnvironment()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path, TestContext.Current.CancellationToken);
        await Root().InvokeAsync(["env", "remove", "prod", "--project-path", dir.Path, "--force"], TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.Environments.ShouldNotContainKey("prod");
    }

    [Fact]
    public async Task EnvRemove_ReturnsNonZero_WhenEnvMissing()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(["env", "remove", "ghost", "--project-path", dir.Path, "--force"], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task EnvRemove_ReturnsNonZero_WhenOnlyEnv()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(["env", "remove", "local", "--project-path", dir.Path, "--force"], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task EnvRemove_ReturnsNonZero_WhenRemovingActiveEnv()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path, TestContext.Current.CancellationToken); // active = local
        var code = await Root().InvokeAsync(["env", "remove", "local", "--project-path", dir.Path, "--force"], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task EnvRemove_DoesNotRemoveActive_LeavesOthersIntact()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path, TestContext.Current.CancellationToken);
        await Root().InvokeAsync(["env", "remove", "local", "--project-path", dir.Path, "--force"], TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.Environments.ShouldContainKey("local"); // unchanged — active, can't remove
    }

    // ── layered environments ──────────────────────────────────────────────────

    [Fact]
    public async Task EnvAdd_WithExtends_InheritsTheBase_AndStoresOnlyTheDifferences()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path, TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(
            $"env add canary --project-path {dir.Path} --extends prod --image-tag canary", TestContext.Current.CancellationToken);
        code.ShouldBe(0);

        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        var canary = sf.Environments["canary"];
        canary.Extends.ShouldBe("prod");
        canary.ImageTag.ShouldBe("canary");
        canary.Target.ShouldBe(sf.Environments["prod"].Target);
        canary.ContainerRegistry.ShouldBe(sf.Environments["prod"].ContainerRegistry);
        canary.TargetConfig.ShouldContainKey("nomad");

        var saved = System.Text.Json.Nodes.JsonNode.Parse(
            await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(dir.Path, "deployify-state.json"), TestContext.Current.CancellationToken))!;
        saved["Environments"]!["canary"]!.AsObject().Count.ShouldBe(2); // Extends and ImageTag
    }

    [Fact]
    public async Task EnvAdd_WithExtends_RefusesAMissingBase()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path, TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(
            $"env add canary --project-path {dir.Path} --extends ghost", TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
        (await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken)).Environments.ShouldNotContainKey("canary");
    }

    [Fact]
    public async Task EnvAdd_WithExtends_RefusesTargetSpecificFlags()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path, TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(
            $"env add canary --project-path {dir.Path} --extends prod --nomad-address http://x:4646", TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task EnvAdd_WithoutTargetOrExtends_Fails()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path, TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(
            $"env add canary --project-path {dir.Path} --non-interactive", TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
    }

    [Fact]
    public async Task EnvUse_RefusesAnAbstractEnvironment()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path, TestContext.Current.CancellationToken);
        (await Root().InvokeAsync(
            $"env add shared --project-path {dir.Path} --extends prod --abstract", TestContext.Current.CancellationToken)).ShouldBe(0);

        var code = await Root().InvokeAsync(["env", "use", "shared", "--project-path", dir.Path], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
        (await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken)).ActiveEnvironment.ShouldBe("local");
    }

    [Fact]
    public async Task EnvRemove_RefusesABaseOthersExtend()
    {
        using var dir = new TempDir();
        await Harness.WriteMultiEnvStateAsync(dir.Path, TestContext.Current.CancellationToken);
        await Root().InvokeAsync($"env add canary --project-path {dir.Path} --extends prod", TestContext.Current.CancellationToken);

        var code = await Root().InvokeAsync(["env", "remove", "prod", "--project-path", dir.Path, "--force"], TestContext.Current.CancellationToken);
        code.ShouldNotBe(0);
        (await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken)).Environments.ShouldContainKey("prod");
    }

    // ── env list address column ───────────────────────────────────────────────

    [Theory]
    [InlineData("Address")]
    [InlineData("address")]
    [InlineData("ADDRESS")]
    public void DisplayAddress_IgnoresTheCaseOfTheKey(string key)
    {
        var config = new Deployify.Core.Models.EnvironmentConfig
        {
            Target = Deployify.Core.Models.DeploymentTargetType.Nomad,
            TargetConfig =
            {
                ["nomad"] = System.Text.Json.JsonSerializer.SerializeToElement(
                    new System.Collections.Generic.Dictionary<string, string> { [key] = "http://nomad:4646" })
            }
        };

        EnvCommand.GetDisplayAddress(config).ShouldBe("http://nomad:4646");
    }

    [Fact]
    public void DisplayAddress_ShowsWhatEnvAddWrites()
    {
        var config = new Deployify.Core.Models.EnvironmentConfig
        {
            Target = Deployify.Core.Models.DeploymentTargetType.Nomad,
            TargetConfig = EnvCommand.BuildTargetConfig(Deployify.Core.Models.DeploymentTargetType.Nomad,
                new EnvCommand.TargetSettings(NomadAddress: "http://nomad:4646"))
        };
        config.TargetConfig["nomad"] = System.Text.Json.JsonSerializer.SerializeToElement(config.TargetConfig["nomad"]);

        EnvCommand.GetDisplayAddress(config).ShouldBe("http://nomad:4646");
    }

    // ── env set ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnvSet_SavesNomadSettings()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, envName: "local", ct: TestContext.Current.CancellationToken);
        var code = await Root().InvokeAsync(
            $"env set local --project-path {dir.Path} --nomad-address http://new:4646 --nomad-register-services false",
            TestContext.Current.CancellationToken);
        code.ShouldBe(0);

        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        var nomad = ((System.Text.Json.JsonElement)sf.Environments["local"].TargetConfig["nomad"]!)
            .Deserialize<Deployify.Target.Nomad.NomadTargetConfig>(Deployify.Core.Models.TargetConfigJsonOptions.Default)!;
        nomad.Address.ShouldBe("http://new:4646");
        nomad.RegisterServices.ShouldBeFalse();
    }

    [Fact]
    public async Task EnvSet_EmptyString_RemovesAValue_AndOtherSettingsApply()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, envName: "local", ct: TestContext.Current.CancellationToken);
        await Root().InvokeAsync(
            ["env", "set", "local", "--project-path", dir.Path, "--dapr-components-path", "./components", "--dashboard-port", "19000"],
            TestContext.Current.CancellationToken);
        await Root().InvokeAsync(
            ["env", "set", "local", "--project-path", dir.Path, "--dapr-components-path", "", "--deploy-dapr", "true", "--image-tag", "v2"],
            TestContext.Current.CancellationToken);

        var env = (await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken)).Environments["local"];
        env.Dapr.ComponentsPath.ShouldBeNull();
        env.DeployDapr.ShouldBeTrue();
        env.Dashboard.Port.ShouldBe(19000);
        env.ImageTag.ShouldBe("v2");
    }
}
