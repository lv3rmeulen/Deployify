using System;
using System.CommandLine;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Deployify.Commands;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Shouldly;
using Spectre.Console;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>
/// ci builds and pushes project images between plan and apply, and tags them with the commit
/// SHA when the configured tag is "latest", so each build deploys a new image and a rollback
/// can return to the previous one.
/// </summary>
[Collection(nameof(ConsoleOutput))]
public sealed class CiPublishTests : IDisposable
{
    private const string Sha = "3f2a9c1e5b7d4e8a9c217d4e5f6a8b90c1d2e3f4";

    private readonly IAnsiConsole _original = AnsiConsole.Console;
    private readonly StringWriter _out = new();

    public CiPublishTests()
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(_out), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors
        });
        console.Profile.Width = 300;
        AnsiConsole.Console = console;
    }

    public void Dispose() => AnsiConsole.Console = _original;

    private string Output => _out.ToString();

    private static EnvironmentConfig Config(string tag) => new() { ImageTag = tag };

    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
    {
        var map = new Dictionary<string, string>();
        foreach (var (name, value) in vars)
        {
            map[name] = value;
        }

        return name => map.GetValueOrDefault(name);
    }

    /// <summary>A folder that is not a git repository, so only CI variables can supply a SHA.</summary>
    private static FakeProcessRunner NoGit()
        => new FakeProcessRunner().When(["rev-parse", "HEAD"], new ProcessResult(128, "", "fatal: not a git repository"));

    private static Task<bool> SetAsync(EnvironmentConfig config, string? tagOverride, bool publishes, bool allowMutable,
        Func<string, string?> env, IProcessRunner? runner = null)
        => CiCommand.SetImageTagAsync(config, tagOverride, publishes, allowMutable, "/repo",
            TestContext.Current.CancellationToken, env, runner ?? NoGit());

    // ── Tag selection ───────────────────────────────────────────────────────

    [Fact]
    public async Task Publishing_ReplacesLatest_WithTheCommitSha()
    {
        var config = Config("latest");

        (await SetAsync(config, null, publishes: true, allowMutable: false, Env(("GITHUB_SHA", Sha)))).ShouldBeTrue();

        config.ImageTag.ShouldBe(Sha);
        Output.ShouldContain($"Image tag: {Sha} (commit SHA from GITHUB_SHA)");
    }

    [Fact]
    public async Task Publishing_ReplacesAnEmptyTag_WithTheCommitSha()
    {
        var config = Config("");

        (await SetAsync(config, null, publishes: true, allowMutable: false, Env(("CI_COMMIT_SHA", Sha)))).ShouldBeTrue();

        config.ImageTag.ShouldBe(Sha);
    }

    [Fact]
    public async Task Publishing_KeepsAConfiguredImmutableTag()
    {
        var config = Config("1.4.2");

        (await SetAsync(config, null, publishes: true, allowMutable: false, Env(("GITHUB_SHA", Sha)))).ShouldBeTrue();

        config.ImageTag.ShouldBe("1.4.2");
    }

    [Fact]
    public async Task ImageTagOption_WinsOverTheCommitSha()
    {
        var config = Config("latest");

        (await SetAsync(config, "release-42", publishes: true, allowMutable: false, Env(("GITHUB_SHA", Sha)))).ShouldBeTrue();

        config.ImageTag.ShouldBe("release-42");
    }

    [Fact]
    public async Task NotPublishing_LeavesTheConfiguredTag_SoDriftChecksCompareWithWhatIsDeployed()
    {
        var config = Config("latest");

        (await SetAsync(config, null, publishes: false, allowMutable: false, Env(("GITHUB_SHA", Sha)))).ShouldBeTrue();

        config.ImageTag.ShouldBe("latest");
    }

    [Fact]
    public async Task NotPublishing_StillAppliesTheImageTagOption()
    {
        var config = Config("latest");

        (await SetAsync(config, Sha, publishes: false, allowMutable: false, Env())).ShouldBeTrue();

        config.ImageTag.ShouldBe(Sha);
    }

    [Fact]
    public async Task Publishing_StopsWithLatest_WhenNoShaIsAvailable()
    {
        var config = Config("latest");

        (await SetAsync(config, null, publishes: true, allowMutable: false, Env())).ShouldBeFalse();

        Output.ShouldContain("no commit SHA is available");
        Output.ShouldContain("--image-tag <tag>");
        Output.ShouldContain("--allow-mutable-tag");
    }

    [Fact]
    public async Task Publishing_KeepsLatest_WithAllowMutableTag()
    {
        var config = Config("latest");

        (await SetAsync(config, null, publishes: true, allowMutable: true, Env())).ShouldBeTrue();

        config.ImageTag.ShouldBe("latest");
        Output.ShouldContain("a rollback can't restore the previous image");
    }

    [Fact]
    public async Task Publishing_StopsWithAnExplicitLatestTag_WithoutAllowMutableTag()
    {
        var config = Config("1.4.2");

        (await SetAsync(config, "latest", publishes: true, allowMutable: false, Env(("GITHUB_SHA", Sha)))).ShouldBeFalse();

        Output.ShouldContain("--image-tag latest is overwritten by every build");
    }

    // ── Pipeline ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SkipApply_SkipsPublishAndApply()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await CommandRunner.BuildFull().InvokeAsync(
            ["ci", "--project-path", dir.Path, "--skip-validate", "--skip-apply"], TestContext.Current.CancellationToken);

        code.ShouldBe(0);
        Output.ShouldContain("4/5 Publish — skipped");
        Output.ShouldContain("5/5 Apply — skipped");
    }

    [Fact]
    public async Task PublishFailure_StopsBeforeApply()
    {
        // The manifest's projects don't exist on disk, so publishing fails wherever the test runs.
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, address: "http://127.0.0.1:19999", tag: "v1.0.0",
            ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        var code = await CommandRunner.BuildFull().InvokeAsync(
            ["ci", "--project-path", dir.Path, "--skip-validate", "--auto-approve"], TestContext.Current.CancellationToken);

        code.ShouldBe(1);
        Output.ShouldContain("4/5 Publish... ✗");
        Output.ShouldNotContain("5/5 Apply");
    }

    [Fact]
    public async Task NoPublish_SkipsThePublishStep()
    {
        using var dir = new TempDir();
        await Harness.WriteNomadStateAsync(dir.Path, address: "http://127.0.0.1:19999", tag: "v1.0.0",
            ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir.Path, ct: TestContext.Current.CancellationToken);

        await CommandRunner.BuildFull().InvokeAsync(
            ["ci", "--project-path", dir.Path, "--skip-validate", "--auto-approve", "--no-publish"],
            TestContext.Current.CancellationToken);

        Output.ShouldContain("4/5 Publish — skipped (--no-publish)");
        Output.ShouldContain("5/5 Apply");
    }
}
