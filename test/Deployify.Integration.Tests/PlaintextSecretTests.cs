using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Commands;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>
/// Secret flags take env:NAME references; literal values are refused unless
/// --allow-plaintext-secrets is passed, and state migrate-secrets cleans up older files.
/// </summary>
public sealed class PlaintextSecretTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static RootCommand Root() => new("t") { InitCommand.Build(), EnvCommand.Build(), StateCommand.Build() };

    private static string StatePath(string dir) => Path.Combine(dir, "deployify-state.json");

    private static Task<DeployifyStateFile> LoadAsync(string dir) => new StateManager().LoadFileAsync(dir, Ct);

    // ── init ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("--nomad-token")]
    [InlineData("--nomad-registry-password")]
    [InlineData("--dashboard-token")]
    public async Task Init_LiteralSecretFlag_IsRefusedAndNothingIsWritten(string flag)
    {
        using var dir = new TempDir();

        var code = await Root().InvokeAsync(
            ["init", "--non-interactive", "--project-path", dir.Path, "--target", "Nomad", flag, "s3cret"], Ct);

        code.ShouldBe(1);
        File.Exists(StatePath(dir.Path)).ShouldBeFalse();
    }

    [Fact]
    public async Task Init_ConsulTargetLiteralTokens_AreRefused()
    {
        using var dir = new TempDir();

        var code = await Root().InvokeAsync(
            ["init", "--non-interactive", "--project-path", dir.Path, "--target", "NomadConsul", "--nc-consul-token", "s3cret"], Ct);

        code.ShouldBe(1);
    }

    [Fact]
    public async Task Init_EnvReference_IsStored()
    {
        using var dir = new TempDir();

        var code = await Root().InvokeAsync(
            ["init", "--non-interactive", "--project-path", dir.Path, "--target", "Nomad", "--nomad-token", "env:MY_NOMAD"], Ct);

        code.ShouldBe(0);
        (await File.ReadAllTextAsync(StatePath(dir.Path), Ct)).ShouldContain("env:MY_NOMAD");
        StateFileSecrets.FindLiteral(await LoadAsync(dir.Path)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Init_AllowPlaintextSecrets_KeepsTheOldBehavior()
    {
        using var dir = new TempDir();

        var code = await Root().InvokeAsync(
            ["init", "--non-interactive", "--project-path", dir.Path, "--target", "Nomad",
             "--nomad-token", "s3cret", "--allow-plaintext-secrets"], Ct);

        code.ShouldBe(0);
        StateFileSecrets.FindLiteral(await LoadAsync(dir.Path)).ShouldContain("local.nomad.Token");
    }

    // ── env ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnvSet_LiteralToken_IsRefused()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(["init", "--non-interactive", "--project-path", dir.Path, "--target", "Nomad"], Ct);
        var before = await File.ReadAllTextAsync(StatePath(dir.Path), Ct);

        var code = await Root().InvokeAsync(
            ["env", "set", "local", "--project-path", dir.Path, "--nomad-token", "s3cret"], Ct);

        code.ShouldBe(1);
        (await File.ReadAllTextAsync(StatePath(dir.Path), Ct)).ShouldBe(before);
    }

    [Fact]
    public async Task EnvSet_EmptyDashboardToken_StillRemovesIt()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(
            ["init", "--non-interactive", "--project-path", dir.Path, "--target", "Nomad", "--dashboard-token", "env:DASH"], Ct);

        var code = await Root().InvokeAsync(
            ["env", "set", "local", "--project-path", dir.Path, "--dashboard-token", ""], Ct);

        code.ShouldBe(0);
        (await LoadAsync(dir.Path)).Environments["local"].Dashboard.BrowserToken.ShouldBeNull();
    }

    [Fact]
    public async Task EnvAdd_LiteralToken_IsRefused()
    {
        using var dir = new TempDir();
        await Root().InvokeAsync(["init", "--non-interactive", "--project-path", dir.Path, "--target", "Nomad"], Ct);

        var code = await Root().InvokeAsync(
            ["env", "add", "prod", "--non-interactive", "--project-path", dir.Path, "--target", "Nomad", "--nomad-token", "s3cret"], Ct);

        code.ShouldBe(1);
        (await LoadAsync(dir.Path)).Environments.ShouldNotContainKey("prod");
    }

    // ── state migrate-secrets ─────────────────────────────────────────────────

    private static async Task<TempDir> StateWithLiteralTokenAsync()
    {
        var dir = new TempDir();
        await Root().InvokeAsync(
            ["init", "--non-interactive", "--project-path", dir.Path, "--target", "Nomad",
             "--nomad-token", "s3cret", "--allow-plaintext-secrets"], Ct);
        return dir;
    }

    [Fact]
    public async Task MigrateSecrets_ReplacesLiteralsWithReferences()
    {
        using var dir = await StateWithLiteralTokenAsync();

        var code = await Root().InvokeAsync(["state", "migrate-secrets", "--project-path", dir.Path], Ct);

        code.ShouldBe(0);
        var json = await File.ReadAllTextAsync(StatePath(dir.Path), Ct);
        json.ShouldNotContain("s3cret");
        json.ShouldContain("env:NOMAD_TOKEN");
        StateFileSecrets.FindLiteral(await LoadAsync(dir.Path)).ShouldBeEmpty();
    }

    [Fact]
    public async Task MigrateSecrets_DryRun_ChangesNothing()
    {
        using var dir = await StateWithLiteralTokenAsync();
        var before = await File.ReadAllTextAsync(StatePath(dir.Path), Ct);

        var code = await Root().InvokeAsync(["state", "migrate-secrets", "--project-path", dir.Path, "--dry-run"], Ct);

        code.ShouldBe(0);
        (await File.ReadAllTextAsync(StatePath(dir.Path), Ct)).ShouldBe(before);
    }

    [Fact]
    public async Task MigrateSecrets_WithoutAStateFile_Fails()
    {
        using var dir = new TempDir();

        (await Root().InvokeAsync(["state", "migrate-secrets", "--project-path", dir.Path], Ct)).ShouldBe(1);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("NOMAD_TOKEN", true)]
    [InlineData("env:MY_TOKEN", true)]
    [InlineData("_x1", true)]
    [InlineData("1ABC", false)]
    [InlineData("MY-TOKEN", false)]
    [InlineData("", false)]
    public void IsValidVariableName(string name, bool valid)
        => SecretPrompts.IsValidVariableName(name).ShouldBe(valid);
}
