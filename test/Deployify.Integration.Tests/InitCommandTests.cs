using System.CommandLine;
using System.Threading.Tasks;
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
            $"--nomad-address http://localhost:4646 --nomad-namespace default --nomad-datacenter dc1", TestContext.Current.CancellationToken);
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
            $"--kubernetes-namespace-prefix myapp --kubernetes-ingress-domain example.com", TestContext.Current.CancellationToken);
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
            $"--compose-project myapp --compose-restart unless-stopped", TestContext.Current.CancellationToken);
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
            $"--env-name staging --target Nomad", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.ActiveEnvironment.ShouldBe("staging");
    }

    [Fact]
    public async Task Init_DefaultEnvNameIsLocal()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} --target Nomad", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.ActiveEnvironment.ShouldBe("local");
    }

    // ── Target config ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Init_Nomad_PersistsAddress()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Nomad --nomad-address http://nomad.prod:4646", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        var cfg = sf.Environments["local"];
        cfg.TargetConfig.ShouldContainKey("nomad");
    }

    [Fact]
    public async Task Init_Nomad_PersistsNamespace()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Nomad --nomad-namespace production", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        var raw = sf.Environments["local"].TargetConfig["nomad"]!.ToString()!;
        raw.ShouldContain("production");
    }

    [Fact]
    public async Task Init_Nomad_PersistsDatacenter()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Nomad --nomad-datacenter us-east-1", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        var raw = sf.Environments["local"].TargetConfig["nomad"]!.ToString()!;
        raw.ShouldContain("us-east-1");
    }

    [Fact]
    public async Task Init_Kubernetes_PersistsNamespacePrefix()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Kubernetes --kubernetes-namespace-prefix myprefix", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        var raw = sf.Environments["local"].TargetConfig["kubernetes"]!.ToString()!;
        raw.ShouldContain("myprefix");
    }

    [Fact]
    public async Task Init_Kubernetes_PersistsIngressDomain()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Kubernetes --kubernetes-ingress-domain apps.example.com", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        var raw = sf.Environments["local"].TargetConfig["kubernetes"]!.ToString()!;
        raw.ShouldContain("apps.example.com");
    }

    [Fact]
    public async Task Init_Compose_PersistsProjectName()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Compose --compose-project mycoolapp", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
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
            $"--target Nomad --container-registry reg.example.com/myapp", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.Environments["local"].ContainerRegistry.ShouldBe("reg.example.com/myapp");
    }

    [Fact]
    public async Task Init_PersistsImageTag()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Nomad --image-tag v2.3.0", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.Environments["local"].ImageTag.ShouldBe("v2.3.0");
    }

    [Fact]
    public async Task Init_DefaultImageTagIsLatest()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} --target Nomad", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.Environments["local"].ImageTag.ShouldBe("latest");
    }

    // ── Output path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Init_PersistsOutputPath()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} " +
            $"--target Nomad --output-path my-output", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.Environments["local"].OutputPath.ShouldBe("my-output");
    }

    // ── Overwrite ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Init_Overwrites_ExistingStateFile()
    {
        using var dir = new TempDir();
        var root = Root();
        await root.InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} --env-name v1 --target Nomad", TestContext.Current.CancellationToken);
        await root.InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} --env-name v2 --target Compose", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.ActiveEnvironment.ShouldBe("v2");
    }

    // ── TargetType is set ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Nomad", DeploymentTargetType.Names.Nomad)]
    [InlineData("Kubernetes", DeploymentTargetType.Names.Kubernetes)]
    [InlineData("Compose", DeploymentTargetType.Names.Compose)]
    [InlineData("argocd", DeploymentTargetType.Names.ArgoCd)]
    [InlineData("example", "example")]
    public async Task Init_SetsTargetType(string targetStr, string expected)
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            $"init --non-interactive --project-path {dir.Path} --target {targetStr}", TestContext.Current.CancellationToken);
        var sf = await new StateManager().LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        sf.Environments["local"].Target.ShouldBe(DeploymentTargetType.Parse(expected));
    }
}
