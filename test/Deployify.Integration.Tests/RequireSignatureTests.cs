using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Commands;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.Services;
using Deployify.Core.State;
using Deployify.Tests.Shared;
using Shouldly;
using Spectre.Console;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>
/// <c>--require-signature</c> on <c>apply</c> and <c>ci</c>: every image the environment builds
/// must be pinned by digest and carry a cosign signature its <c>signing</c> settings trust, or
/// nothing is planned or deployed. Images from the manifest that Deployify doesn't build aren't checked.
/// </summary>
[Collection(nameof(ConsoleOutput))]
public sealed class RequireSignatureTests : IDisposable
{
    private const string Registry = "reg.example.com/app";
    private const string ApiDigest = "sha256:" + "aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11";
    private const string WorkerDigest = "sha256:" + "bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22";
    private const string Api = Registry + "/api@" + ApiDigest;
    private const string Worker = Registry + "/worker@" + WorkerDigest;

    private readonly IAnsiConsole _original = AnsiConsole.Console;
    private readonly StringWriter _out = new();

    public RequireSignatureTests()
    {
        AnsiConsole.Console = PlainConsole.Create(_out, width: 300);
    }

    public void Dispose() => AnsiConsole.Console = _original;

    private string Output => _out.ToString();

    private static AspireManifest Manifest() => JsonSerializer.Deserialize<AspireManifest>("""
        { "resources": {
            "api": { "type": "project.v0", "path": "Api/Api.csproj" },
            "worker": { "type": "project.v0", "path": "Worker/Worker.csproj" },
            "redis": { "type": "container.v0", "image": "redis:7" } } }
        """)!;

    private static EnvironmentConfig Config(SigningConfig? signing = null, bool pinned = true)
    {
        var config = new EnvironmentConfig
        {
            ContainerRegistry = Registry,
            ImageTag = "v1",
            Signing = signing ?? new SigningConfig { PublicKey = "env:COSIGN_PUBLIC_KEY" }
        };
        if (pinned)
        {
            config.PublishedImages.Record(Registry + "/api", "v1", ApiDigest, DateTimeOffset.UtcNow);
            config.PublishedImages.Record(Registry + "/worker", "v1", WorkerDigest, DateTimeOffset.UtcNow);
        }

        return config;
    }

    private static Task<bool> BlocksAsync(EnvironmentConfig config, FakeProcessRunner runner)
        => SignatureCheck.BlocksAsync(Manifest(), config, "prod", "/app", TestContext.Current.CancellationToken, new ImageSigner(runner));

    [Fact]
    public async Task Passes_WhenEveryBuiltImageVerifies_ByDigest()
    {
        var runner = new FakeProcessRunner();

        (await BlocksAsync(Config(), runner)).ShouldBeFalse(Output);

        var verified = runner.Calls.Where(c => c.Args[0] == "verify").Select(c => c.Args[^1]).ToList();
        verified.ShouldBe([Api, Worker]); // not redis: Deployify didn't build it
        runner.Calls.First(c => c.Args[0] == "verify").Args.ShouldBe(["verify", "--key", "env://COSIGN_PUBLIC_KEY", Api]);
        Output.ShouldContain($"✓ api: {Api}");
    }

    [Fact]
    public async Task Blocks_AndNamesTheImage_WhenASignatureDoesntVerify()
    {
        var runner = new FakeProcessRunner()
            .When(s => s.Args[0] == "verify" && s.Args[^1] == Worker, _ => new ProcessResult(1, "", "Error: no matching signatures"));

        (await BlocksAsync(Config(), runner)).ShouldBeTrue();

        Output.ShouldContain($"✗ worker: {Worker}: Error: no matching signatures");
        Output.ShouldContain("1 of 2 image(s) have no signature prod trusts");
    }

    [Fact]
    public async Task Blocks_BeforeRunningCosign_WhenAnImageIsntPinned()
    {
        var runner = new FakeProcessRunner();

        (await BlocksAsync(Config(pinned: false), runner)).ShouldBeTrue();

        Output.ShouldContain("--require-signature is set, but 2 image(s) have no published digest");
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Blocks_WhenNothingSaysWhichSignaturesToTrust()
    {
        var runner = new FakeProcessRunner();

        (await BlocksAsync(Config(new SigningConfig { Enabled = true }), runner)).ShouldBeTrue();

        Output.ShouldContain("signing.identity");
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Blocks_WhenCosignIsMissing()
    {
        var runner = new FakeProcessRunner();
        runner.Missing.Add("cosign");

        (await BlocksAsync(Config(), runner)).ShouldBeTrue();

        Output.ShouldContain("cosign isn't installed");
    }

    [Fact]
    public async Task Passes_WithNothingToVerify_WhenNoImageIsBuilt()
    {
        var manifest = JsonSerializer.Deserialize<AspireManifest>("""{ "resources": { "redis": { "type": "container.v0", "image": "redis:7" } } }""")!;
        var runner = new FakeProcessRunner();

        (await SignatureCheck.BlocksAsync(manifest, Config(new SigningConfig()), "prod", "/app",
            TestContext.Current.CancellationToken, new ImageSigner(runner))).ShouldBeFalse();

        runner.Calls.ShouldBeEmpty();
    }

    // ── apply ─────────────────────────────────────────────────────────────────

    private static async Task WriteStateAsync(string dir, bool published)
    {
        await Harness.WriteNomadStateAsync(dir, envName: "prod", registry: Registry, ct: TestContext.Current.CancellationToken);
        await Harness.WriteManifestAsync(dir, ct: TestContext.Current.CancellationToken);
        if (published)
        {
            var images = new PublishedImages();
            images.Record(Registry + "/api", "v1.0.0", ApiDigest, DateTimeOffset.UtcNow);
            images.Record(Registry + "/worker", "v1.0.0", WorkerDigest, DateTimeOffset.UtcNow);
            await using var state = new StateSession(dir, "prod", null);
            await PublishedImageStore.SaveAsync(state, images, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Planning writes a Nomad job file per service; none means apply stopped before it.</summary>
    private static void NoJobsWritten(string dir)
    {
        var output = Path.Combine(dir, "deployify-output");
        (Directory.Exists(output) ? Directory.GetFiles(output, "*.nomad.json", SearchOption.AllDirectories) : []).ShouldBeEmpty();
    }

    [Fact]
    public async Task Apply_StopsBeforePlanning_WhenAnImageIsntPinned()
    {
        using var dir = new TempDir();
        await WriteStateAsync(dir.Path, published: false);

        var code = await CommandRunner.BuildFull().InvokeAsync(
            ["apply", "--project-path", dir.Path, "--require-signature", "--dry-run"], TestContext.Current.CancellationToken);

        code.ShouldBe(1);
        Output.ShouldContain("--require-signature is set, but 2 image(s) have no published digest");
        NoJobsWritten(dir.Path);
    }

    [Fact]
    public async Task Apply_StopsBeforePlanning_WithoutSigningSettings()
    {
        using var dir = new TempDir();
        await WriteStateAsync(dir.Path, published: true);

        var code = await CommandRunner.BuildFull().InvokeAsync(
            ["apply", "--project-path", dir.Path, "--require-signature", "--dry-run"], TestContext.Current.CancellationToken);

        code.ShouldBe(1);
        Output.ShouldContain("prod has no settings to verify image signatures with");
        NoJobsWritten(dir.Path);
    }
}
