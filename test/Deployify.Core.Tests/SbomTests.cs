using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.Services;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

/// <summary>
/// With <c>sbom.enabled</c>, publish generates an SBOM for every image it built with syft and
/// attaches it to the pushed image, by digest, as a cosign attestation.
/// </summary>
public sealed class SbomTests
{
    private const string Digest = PublishedImagesTests.Digest;

    private static AspireManifest Api()
        => JsonSerializer.Deserialize<AspireManifest>("""{ "resources": { "api": { "type": "project.v0", "path": "Api/Api.csproj" } } }""")!;

    private static FakeProcessRunner Runner(string? digest = Digest) => new FakeProcessRunner()
        .When(["-t:PublishContainer"], new ProcessResult(0,
            digest is null ? "" : $$"""{ "Properties": { "GeneratedContainerDigest": "{{digest}}" } }""", ""));

    private static Publisher Publisher(FakeProcessRunner runner) => new(NullLogger<Publisher>.Instance) { Runner = runner };

    private static EnvironmentConfig Config(string? registry = "reg.example.com/app", SbomConfig? sbom = null) => new()
    {
        ContainerRegistry = registry,
        ImageTag = "v1",
        Sbom = sbom ?? new SbomConfig { Enabled = true }
    };

    private static Task PublishAsync(FakeProcessRunner runner, EnvironmentConfig config, TempDir dir)
        => Publisher(runner).PublishAsync(Api(), config, dir.Path, TestContext.Current.CancellationToken);

    private static ProcessSpec[] Calls(FakeProcessRunner runner, string tool, string firstArg)
        => [.. runner.Calls.Where(c => c.FileName == tool && c.Args.Count > 0 && c.Args[0] == firstArg)];

    [Fact]
    public async Task Publish_ScansThePushedDigest_AndAttestsTheSbomToIt()
    {
        using var dir = new TempDir();
        var runner = Runner();

        await PublishAsync(runner, Config(), dir);

        var file = Path.Combine(dir.Path, "deployify-output", "sbom", "api.spdx.json");
        var scan = runner.Calls.Single(c => c.FileName == "syft" && c.Args[0] != "version");
        scan.Args.ShouldBe(["registry:reg.example.com/app/api@" + Digest, "--platform", "linux/amd64", "-o", "spdx-json=" + file]);
        var attest = Calls(runner, "cosign", "attest").ShouldHaveSingleItem();
        attest.Args.ShouldBe(["attest", "--yes", "--type", "spdxjson", "--predicate", file, "reg.example.com/app/api@" + Digest]);

        // The SBOM comes from the pushed image, so it is generated after the push and attested after that.
        var order = runner.Calls.Select(c => c.Args.Contains("-t:PublishContainer") ? "publish" : c.FileName).ToList();
        order.LastIndexOf("publish").ShouldBeLessThan(order.LastIndexOf("syft"));
        order.LastIndexOf("syft").ShouldBeLessThan(order.LastIndexOf("cosign"));
    }

    [Fact]
    public async Task Publish_WithCycloneDxAndAKey_PassesThemToSyftAndCosign()
    {
        using var dir = new TempDir();
        var runner = Runner();

        await PublishAsync(runner, Config(sbom: new SbomConfig { Enabled = true, Format = SbomFormat.CycloneDx, Key = "env:COSIGN_KEY" }), dir);

        var file = Path.Combine(dir.Path, "deployify-output", "sbom", "api.cdx.json");
        runner.Calls.ShouldContain(c => c.FileName == "syft" && c.Args.Contains("cyclonedx-json=" + file));
        Calls(runner, "cosign", "attest").ShouldHaveSingleItem().Args.ShouldBe(
            ["attest", "--yes", "--type", "cyclonedx", "--predicate", file, "--key", "env://COSIGN_KEY", "reg.example.com/app/api@" + Digest]);
    }

    [Fact]
    public async Task Publish_WithoutARegistry_ScansTheLocalImage_AndAttestsNothing()
    {
        using var dir = new TempDir();
        var runner = Runner();
        runner.Missing.Add("cosign");

        await PublishAsync(runner, Config(registry: null), dir);

        runner.Calls.ShouldContain(c => c.FileName == "syft" && c.Args[0] == "docker:api:v1");
        Calls(runner, "cosign", "attest").ShouldBeEmpty();
    }

    [Fact]
    public async Task Publish_DoesNotAttest_WhenTheDigestIsUnknown()
    {
        using var dir = new TempDir();
        var runner = Runner(digest: null);

        await PublishAsync(runner, Config(), dir);

        runner.Calls.ShouldContain(c => c.FileName == "syft" && c.Args[0] == "registry:reg.example.com/app/api:v1");
        Calls(runner, "cosign", "attest").ShouldBeEmpty();
    }

    [Fact]
    public async Task Publish_WithAttestOff_OnlyWritesTheSbom()
    {
        using var dir = new TempDir();
        var runner = Runner();
        runner.Missing.Add("cosign");

        await PublishAsync(runner, Config(sbom: new SbomConfig { Enabled = true, Attest = false }), dir);

        runner.Calls.ShouldContain(c => c.FileName == "syft" && c.Args[0].StartsWith("registry:", StringComparison.Ordinal));
        runner.Calls.ShouldNotContain(c => c.FileName == "cosign");
    }

    [Theory]
    [InlineData("syft")]
    [InlineData("cosign")]
    public async Task Publish_StopsBeforeBuilding_WhenAToolIsMissing(string tool)
    {
        using var dir = new TempDir();
        var runner = Runner();
        runner.Missing.Add(tool);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => PublishAsync(runner, Config(), dir));

        ex.Message.ShouldContain(tool);
        runner.Calls.ShouldNotContain(c => c.Args.Contains("-t:PublishContainer"));
    }

    [Fact]
    public async Task Publish_StopsWithTheError_WhenCosignFails()
    {
        using var dir = new TempDir();
        var runner = Runner().When(["attest"], new ProcessResult(1, "", "UNAUTHORIZED"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => PublishAsync(runner, Config(), dir));

        ex.Message.ShouldContain("UNAUTHORIZED");
    }

    [Fact]
    public async Task Publish_RunsNoSbomTools_WhenDisabled()
    {
        using var dir = new TempDir();
        var runner = Runner();

        await PublishAsync(runner, Config(sbom: new SbomConfig()), dir);

        runner.Calls.ShouldNotContain(c => c.FileName == "syft" || c.FileName == "cosign");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("env:COSIGN_KEY", "env://COSIGN_KEY")]
    [InlineData("env://COSIGN_KEY", "env://COSIGN_KEY")]
    [InlineData("awskms:///alias/deployify", "awskms:///alias/deployify")]
    [InlineData("k8s://ci/cosign", "k8s://ci/cosign")]
    public void ResolveKey(string? key, string? expected)
        => SbomAttester.ResolveKey(key, "/app").ShouldBe(expected);

    [Fact]
    public void ResolveKey_ResolvesAKeyFileAgainstTheAppHostDirectory()
    {
        var baseDir = Path.GetFullPath("apphost");
        SbomAttester.ResolveKey("keys/cosign.key", baseDir).ShouldBe(Path.Combine(baseDir, "keys", "cosign.key"));
    }

    [Fact]
    public void SbomConfig_ReadsFromTheStateFile_AndDefaultsToOff()
    {
        var config = JsonSerializer.Deserialize<EnvironmentConfig>(
            """{ "sbom": { "enabled": true, "format": "CycloneDx", "key": "cosign.key" } }""", TargetConfigJsonOptions.Default)!;

        config.Sbom.Enabled.ShouldBeTrue();
        config.Sbom.Format.ShouldBe(SbomFormat.CycloneDx);
        config.Sbom.Attest.ShouldBeTrue();
        config.Sbom.Key.ShouldBe("cosign.key");
        new EnvironmentConfig().Sbom.Enabled.ShouldBeFalse();
    }
}
