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
/// With <c>signing.enabled</c>, publish signs every image it pushed, by digest, with
/// <c>cosign sign</c>. The <c>signing</c> settings also say which signatures
/// <c>--require-signature</c> trusts, resolved into <c>cosign verify</c> flags.
/// </summary>
public sealed class SigningTests
{
    private const string Digest = PublishedImagesTests.Digest;
    private const string Image = "reg.example.com/app/api@" + Digest;

    private static AspireManifest Api()
        => JsonSerializer.Deserialize<AspireManifest>("""{ "resources": { "api": { "type": "project.v0", "path": "Api/Api.csproj" } } }""")!;

    private static FakeProcessRunner Runner(string? digest = Digest) => new FakeProcessRunner()
        .When(["-t:PublishContainer"], new ProcessResult(0,
            digest is null ? "" : $$"""{ "Properties": { "GeneratedContainerDigest": "{{digest}}" } }""", ""));

    private static EnvironmentConfig Config(string? registry = "reg.example.com/app", SigningConfig? signing = null,
        SbomConfig? sbom = null) => new()
    {
        ContainerRegistry = registry,
        ImageTag = "v1",
        Signing = signing ?? new SigningConfig { Enabled = true },
        Sbom = sbom ?? new SbomConfig()
    };

    private static Task PublishAsync(FakeProcessRunner runner, EnvironmentConfig config, TempDir dir)
        => new Publisher(NullLogger<Publisher>.Instance) { Runner = runner }
            .PublishAsync(Api(), config, dir.Path, TestContext.Current.CancellationToken);

    private static ProcessSpec[] Cosign(FakeProcessRunner runner, string subcommand)
        => [.. runner.Calls.Where(c => c.FileName == "cosign" && c.Args.Count > 0 && c.Args[0] == subcommand)];

    // ── Publish ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_SignsThePushedDigest_Keyless()
    {
        using var dir = new TempDir();
        var runner = Runner();

        await PublishAsync(runner, Config(), dir);

        Cosign(runner, "sign").ShouldHaveSingleItem().Args.ShouldBe(["sign", "--yes", Image]);
        var order = runner.Calls.Select(c => c.Args.Contains("-t:PublishContainer") ? "publish" : string.Join(' ', c.Args.Take(1))).ToList();
        order.IndexOf("publish").ShouldBeLessThan(order.IndexOf("sign"));
    }

    [Fact]
    public async Task Publish_SignsWithTheKey()
    {
        using var dir = new TempDir();
        var runner = Runner();

        await PublishAsync(runner, Config(signing: new SigningConfig { Enabled = true, Key = "env:COSIGN_KEY" }), dir);

        Cosign(runner, "sign").ShouldHaveSingleItem().Args.ShouldBe(["sign", "--yes", "--key", "env://COSIGN_KEY", Image]);
    }

    [Fact]
    public async Task Publish_SignsBeforeAttesting_AndTheSbomUsesTheSigningKey()
    {
        using var dir = new TempDir();
        var runner = Runner();

        await PublishAsync(runner, Config(
            signing: new SigningConfig { Enabled = true, Key = "awskms:///alias/sign" },
            sbom: new SbomConfig { Enabled = true }), dir);

        var cosign = runner.Calls.Where(c => c.FileName == "cosign" && c.Args[0] != "version").Select(c => c.Args[0]).ToList();
        cosign.ShouldBe(["sign", "attest"]);
        Cosign(runner, "attest").Single().Args.ShouldContain("awskms:///alias/sign");
    }

    [Fact]
    public async Task Publish_TheSbomKeepsItsOwnKey()
    {
        using var dir = new TempDir();
        var runner = Runner();

        await PublishAsync(runner, Config(
            signing: new SigningConfig { Enabled = true, Key = "awskms:///alias/sign" },
            sbom: new SbomConfig { Enabled = true, Key = "awskms:///alias/sbom" }), dir);

        Cosign(runner, "attest").Single().Args.ShouldContain("awskms:///alias/sbom");
        Cosign(runner, "sign").Single().Args.ShouldContain("awskms:///alias/sign");
    }

    [Fact]
    public async Task Publish_StopsBeforeBuilding_WhenCosignIsMissing()
    {
        using var dir = new TempDir();
        var runner = Runner();
        runner.Missing.Add("cosign");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => PublishAsync(runner, Config(), dir));

        ex.Message.ShouldContain("cosign");
        runner.Calls.ShouldNotContain(c => c.Args.Contains("-t:PublishContainer"));
    }

    [Fact]
    public async Task Publish_StopsBeforeBuilding_WhenTheKeyFileIsMissing()
    {
        using var dir = new TempDir();
        var runner = Runner();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            PublishAsync(runner, Config(signing: new SigningConfig { Enabled = true, Key = "cosign.key" }), dir));

        ex.Message.ShouldContain(Path.Combine(dir.Path, "cosign.key"));
        runner.Calls.ShouldNotContain(c => c.Args.Contains("-t:PublishContainer"));
    }

    [Fact]
    public async Task Publish_SignsWithAKeyFile_RelativeToTheAppHost()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "cosign.key"), "key", TestContext.Current.CancellationToken);
        var runner = Runner();

        await PublishAsync(runner, Config(signing: new SigningConfig { Enabled = true, Key = "cosign.key" }), dir);

        Cosign(runner, "sign").Single().Args.ShouldBe(["sign", "--yes", "--key", Path.Combine(dir.Path, "cosign.key"), Image]);
    }

    [Fact]
    public async Task Publish_FailsWithoutSigningByTag_WhenTheDigestIsUnknown()
    {
        using var dir = new TempDir();
        var runner = Runner(digest: null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => PublishAsync(runner, Config(), dir));

        ex.Message.ShouldContain("api");
        Cosign(runner, "sign").ShouldBeEmpty();
    }

    [Fact]
    public async Task Publish_WithoutARegistry_SignsNothing_AndDoesntNeedCosign()
    {
        using var dir = new TempDir();
        var runner = Runner();
        runner.Missing.Add("cosign");

        await PublishAsync(runner, Config(registry: null), dir);

        runner.Calls.ShouldNotContain(c => c.FileName == "cosign");
    }

    [Fact]
    public async Task Publish_StopsWithTheError_WhenSigningFails()
    {
        using var dir = new TempDir();
        var runner = Runner().When(["sign"], new ProcessResult(1, "", "DENIED"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => PublishAsync(runner, Config(), dir));

        ex.Message.ShouldContain("DENIED");
    }

    [Fact]
    public async Task Publish_DoesntSign_WhenDisabled()
    {
        using var dir = new TempDir();
        var runner = Runner();

        await PublishAsync(runner, Config(signing: new SigningConfig { Key = "awskms:///alias/sign" }), dir);

        runner.Calls.ShouldNotContain(c => c.FileName == "cosign");
    }

    // ── Which signatures are trusted ──────────────────────────────────────────

    [Fact]
    public void Policy_PrefersThePublicKey()
    {
        var baseDir = Path.GetFullPath("apphost");
        var policy = ImageSigner.ResolvePolicy(
            new SigningConfig { Key = "cosign.key", PublicKey = "cosign.pub", Identity = "ignored" }, baseDir, "prod");

        policy.Arguments.ShouldBe(["--key", Path.Combine(baseDir, "cosign.pub")]);
        ImageSigner.VerifyArgs(Image, policy).ShouldBe(["verify", "--key", Path.Combine(baseDir, "cosign.pub"), Image]);
    }

    [Fact]
    public void Policy_VerifiesWithAKmsSigningKey()
        => ImageSigner.ResolvePolicy(new SigningConfig { Key = "awskms:///alias/sign" }, "/app", "prod")
            .Arguments.ShouldBe(["--key", "awskms:///alias/sign"]);

    [Fact]
    public void Policy_ReadsAnEnvPublicKey()
        => ImageSigner.ResolvePolicy(new SigningConfig { PublicKey = "env:COSIGN_PUBLIC_KEY" }, "/app", "prod")
            .Arguments.ShouldBe(["--key", "env://COSIGN_PUBLIC_KEY"]);

    [Theory]
    [InlineData("cosign.key")]
    [InlineData("env:COSIGN_PRIVATE_KEY")]
    public void Policy_AsksForThePublicKey_OfAPrivateSigningKey(string key)
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            ImageSigner.ResolvePolicy(new SigningConfig { Key = key }, "/app", "prod"));

        ex.Message.ShouldContain("signing.publicKey");
    }

    [Fact]
    public void Policy_Keyless_PinsTheIdentityAndIssuer()
    {
        var policy = ImageSigner.ResolvePolicy(new SigningConfig
        {
            Identity = "https://github.com/org/app/.github/workflows/release.yml@refs/heads/main",
            Issuer = "https://token.actions.githubusercontent.com"
        }, "/app", "prod");

        policy.Arguments.ShouldBe([
            "--certificate-identity", "https://github.com/org/app/.github/workflows/release.yml@refs/heads/main",
            "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com"
        ]);
    }

    [Fact]
    public void Policy_Keyless_TakesRegularExpressions()
        => ImageSigner.ResolvePolicy(new SigningConfig { IdentityRegexp = "^https://github.com/org/", IssuerRegexp = "github" }, "/app", "prod")
            .Arguments.ShouldBe(["--certificate-identity-regexp", "^https://github.com/org/", "--certificate-oidc-issuer-regexp", "github"]);

    [Theory]
    [InlineData(null, null)]
    [InlineData("someone", null)]
    [InlineData(null, "https://token.actions.githubusercontent.com")]
    public void Policy_Keyless_NeedsBothAnIdentityAndAnIssuer(string? identity, string? issuer)
    {
        // Either alone would accept a signature from anyone the other allows.
        var ex = Should.Throw<InvalidOperationException>(() =>
            ImageSigner.ResolvePolicy(new SigningConfig { Enabled = true, Identity = identity, Issuer = issuer }, "/app", "prod"));

        ex.Message.ShouldContain("prod");
        ex.Message.ShouldContain("signing.identity");
    }

    [Fact]
    public async Task Verify_ReturnsNull_ForAGoodSignature_AndCosignsLastLineOtherwise()
    {
        var policy = ImageSigner.ResolvePolicy(new SigningConfig { PublicKey = "env:PUB" }, "/app", "prod");
        var runner = new FakeProcessRunner().When(["verify"], new ProcessResult(0, "[{}]", ""));
        (await new ImageSigner(runner).VerifyAsync(Image, policy, TestContext.Current.CancellationToken)).ShouldBeNull();
        runner.Calls.Single().Args.ShouldBe(["verify", "--key", "env://PUB", Image]);

        runner.When(["verify"], new ProcessResult(1, "", "Verifying...\nError: no signatures found\n"));
        (await new ImageSigner(runner).VerifyAsync(Image, policy, TestContext.Current.CancellationToken))
            .ShouldBe("Error: no signatures found");
    }

    [Fact]
    public void SigningConfig_ReadsFromTheStateFile_AndDefaultsToOff()
    {
        var config = JsonSerializer.Deserialize<EnvironmentConfig>(
            """{ "signing": { "enabled": true, "key": "awskms:///alias/sign", "publicKey": "cosign.pub", "identityRegexp": "x", "issuer": "y" } }""",
            TargetConfigJsonOptions.Default)!;

        config.Signing.Enabled.ShouldBeTrue();
        config.Signing.Key.ShouldBe("awskms:///alias/sign");
        config.Signing.PublicKey.ShouldBe("cosign.pub");
        config.Signing.IdentityRegexp.ShouldBe("x");
        config.Signing.Issuer.ShouldBe("y");
        new EnvironmentConfig().Signing.Enabled.ShouldBeFalse();
        ImageSigner.HasVerificationSettings(new SigningConfig()).ShouldBeFalse();
        ImageSigner.HasVerificationSettings(new SigningConfig { Issuer = "y" }).ShouldBeTrue();
    }
}
