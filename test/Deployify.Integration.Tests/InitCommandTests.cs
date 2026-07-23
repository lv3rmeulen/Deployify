using System.CommandLine;
using Deployify.Commands;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

public sealed class InitCommandTests
{
    private static RootCommand Root() => new("t") { InitCommand.Build() };

    // ── Creates state file ────────────────────────────────────────────────────

    [Fact]
    public async Task Init_NonInteractive_Nomad_CreatesStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--env-name local --target Nomad " +
            $"--nomad-address http://localhost:4646 --nomad-namespace default --nomad-datacenter dc1");
        code.ShouldBe(0);
        new StateManager().Exists(dir.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task Init_NonInteractive_Kubernetes_CreatesStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--env-name prod --target Kubernetes " +
            $"--k8s-namespace-prefix myapp --k8s-ingress-domain example.com");
        code.ShouldBe(0);
        new StateManager().Exists(dir.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task Init_NonInteractive_Compose_CreatesStateFile()
    {
        using var dir = new TempDir();
        var code = await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--env-name dev --target Compose " +
            $"--compose-project myapp --compose-restart unless-stopped");
        code.ShouldBe(0);
        new StateManager().Exists(dir.Path).ShouldBeTrue();
    }

    // ── Environment name ──────────────────────────────────────────────────────

    [Fact]
    public async Task Init_SetsActiveEnvironment()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--env-name staging --target Nomad");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.ActiveEnvironment.ShouldBe("staging");
    }

    [Fact]
    public async Task Init_DefaultEnvNameIsLocal()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} --target Nomad");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.ActiveEnvironment.ShouldBe("local");
    }

    // ── Target config ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Init_Nomad_PersistsAddress()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Nomad --nomad-address http://nomad.prod:4646");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        var cfg = sf.Environments["local"];
        cfg.TargetConfig.ShouldContainKey("nomad");
    }

    [Fact]
    public async Task Init_Nomad_PersistsNamespace()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Nomad --nomad-namespace production");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        var raw = sf.Environments["local"].TargetConfig["nomad"]!.ToString()!;
        raw.ShouldContain("production");
    }

    [Fact]
    public async Task Init_Nomad_PersistsDatacenter()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Nomad --nomad-datacenter us-east-1");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        var raw = sf.Environments["local"].TargetConfig["nomad"]!.ToString()!;
        raw.ShouldContain("us-east-1");
    }

    [Fact]
    public async Task Init_Kubernetes_PersistsNamespacePrefix()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Kubernetes --k8s-namespace-prefix myprefix");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        var raw = sf.Environments["local"].TargetConfig["kubernetes"]!.ToString()!;
        raw.ShouldContain("myprefix");
    }

    [Fact]
    public async Task Init_Kubernetes_PersistsIngressDomain()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Kubernetes --k8s-ingress-domain apps.example.com");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        var raw = sf.Environments["local"].TargetConfig["kubernetes"]!.ToString()!;
        raw.ShouldContain("apps.example.com");
    }

    [Fact]
    public async Task Init_Compose_PersistsProjectName()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Compose --compose-project mycoolapp");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        var raw = sf.Environments["local"].TargetConfig["compose"]!.ToString()!;
        raw.ShouldContain("mycoolapp");
    }

    // ── Container registry & image tag ────────────────────────────────────────

    [Fact]
    public async Task Init_PersistsContainerRegistry()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Nomad --container-registry reg.example.com/myapp");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.Environments["local"].ContainerRegistry.ShouldBe("reg.example.com/myapp");
    }

    [Fact]
    public async Task Init_PersistsImageTag()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Nomad --image-tag v2.3.0");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.Environments["local"].ImageTag.ShouldBe("v2.3.0");
    }

    [Fact]
    public async Task Init_DefaultImageTagIsLatest()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} --target Nomad");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.Environments["local"].ImageTag.ShouldBe("latest");
    }

    // ── Output path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Init_PersistsOutputPath()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Nomad --output-path my-output");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.Environments["local"].OutputPath.ShouldBe("my-output");
    }

    // ── Overwrite ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Init_Overwrites_ExistingStateFile()
    {
        using var dir = new TempDir();
        var root = Root();
        await root.InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} --env-name v1 --target Nomad");
        await root.InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} --env-name v2 --target Compose");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.ActiveEnvironment.ShouldBe("v2");
    }

    // ── TargetType is set ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Nomad", DeploymentTargetType.Nomad)]
    [InlineData("Kubernetes", DeploymentTargetType.Kubernetes)]
    [InlineData("Compose", DeploymentTargetType.Compose)]
    public async Task Init_SetsTargetType(string targetStr, DeploymentTargetType expected)
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} --target {targetStr}");
        var sf = await new StateManager().LoadFileAsync(dir.Path);
        sf.Environments["local"].Target.ShouldBe(expected);
    }
}
