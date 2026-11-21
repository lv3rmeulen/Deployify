using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Shouldly;
using Xunit;

namespace Deployify.Plugins.Tests;

/// <summary>
/// The signed plugin index: reading it only when its signature checks out, and installing a
/// package only when it matches the index's hash and is signed by the publisher the index names.
/// cosign is faked; what is checked is that it is asked the right question and that its answer is obeyed.
/// </summary>
public sealed class PluginIndexTests : IDisposable
{
    private const string Keyless = "https://github.com/acme/deployify-target-example/.github/workflows/release.yml@refs/tags/v1.0.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateTempSubdirectory("deployify-index-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static PluginSigner Signer => new() { Identity = Keyless, Issuer = PluginSigner.GitHubActionsIssuer };

    /// <summary>Fakes cosign: <c>version</c> works, <c>verify-blob</c> answers with <see cref="Verify"/>.</summary>
    private sealed class FakeCosign : IProcessRunner
    {
        public Func<IReadOnlyList<string>, bool> Verify { get; set; } = _ => true;

        public List<IReadOnlyList<string>> Verified { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
        {
            spec.FileName.ShouldBe("cosign");
            if (spec.Args[0] == "version")
            {
                return Task.FromResult(new ProcessResult(0, "v3.1.4", ""));
            }

            spec.Args[0].ShouldBe("verify-blob");
            Verified.Add(spec.Args);
            return Task.FromResult(Verify(spec.Args)
                ? new ProcessResult(0, "", "Verified OK")
                : new ProcessResult(1, "", "Error: none of the expected identities matched what was in the certificate"));
        }
    }

    private static string Sha256(string file) => PluginLockFile.HashFile(file);

    /// <summary>A plugin package for this platform: a script named like the plugin, zipped.</summary>
    private string Package(string id = "example", Action<ZipArchive>? extra = null)
    {
        var folder = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(folder, $"deployify-target-{id}.cmd"), "@echo off\r\nexit /b 0\r\n");
        }
        else
        {
            File.WriteAllText(Path.Combine(folder, $"deployify-target-{id}"), "#!/bin/sh\nexit 0\n");
        }

        File.WriteAllText(Path.Combine(folder, "settings.json"), "{}");
        var zip = Path.Combine(_root, $"deployify-target-{id}-{Guid.NewGuid().ToString("N")[..6]}.zip");
        ZipFile.CreateFromDirectory(folder, zip);
        if (extra is not null)
        {
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
            extra(archive);
        }

        File.WriteAllText(zip + PluginIndex.BundleSuffix, "{\"fake\":\"bundle\"}");
        return zip;
    }

    private PluginIndex IndexWith(string package, string id = "example", string version = "1.0.0", string? sha = null)
        => new()
        {
            Plugins =
            {
                [id] = new PluginIndexEntry
                {
                    Name = "Example Cloud",
                    Publisher = "Acme",
                    Signer = Signer,
                    Versions =
                    [
                        new PluginIndexVersion
                        {
                            Version = version,
                            Protocol = "1.0",
                            Packages = { [PluginFiles.CurrentPlatform] = new PluginPackage { Url = package, Sha256 = sha ?? Sha256(package) } }
                        }
                    ]
                }
            }
        };

    private PluginIndexSource Source => new(Path.Combine(_root, "index.json"), new PluginSigner { Identity = "https://idx", Issuer = "https://issuer" });

    private string PluginsFolder => Path.Combine(_root, "home", ".deployify", "plugins");

    // ---- the index format ----

    [Fact]
    public void Parse_RefusesEntriesThatWouldTrustTheWrongSigner()
    {
        var json = """
            {
              "version": 1,
              "plugins": {
                "compose": { "name": "x", "publisher": "x", "signer": { "identity": "a", "issuer": "b" }, "versions": [] },
                "nosigner": { "name": "x", "publisher": "x", "versions": [] },
                "loose": { "name": "x", "publisher": "x", "signer": { "identityRegexp": "github.com/acme/", "issuer": "b" }, "versions": [] },
                "noissuer": { "name": "x", "publisher": "x", "signer": { "identity": "a" }, "versions": [] },
                "nohash": { "name": "x", "publisher": "x", "signer": { "identity": "a", "issuer": "b" },
                  "versions": [ { "version": "1.0.0", "packages": { "any": { "url": "https://x/p.zip" } } } ] }
              }
            }
            """;

        var ex = Should.Throw<PluginException>(() => PluginIndex.Parse(Encoding.UTF8.GetBytes(json), "test"));

        ex.Message.ShouldContain("compose: 'compose' is a built-in target");
        ex.Message.ShouldContain("nosigner: 'signer' is required");
        ex.Message.ShouldContain("loose: signer: 'identityRegexp' must be anchored");
        ex.Message.ShouldContain("noissuer: signer: needs 'identity'");
        ex.Message.ShouldContain("nohash: 1.0.0: any: 'sha256' must be");
    }

    [Fact]
    public void Parse_RefusesAnotherFormatVersion()
        => Should.Throw<PluginException>(() => PluginIndex.Parse("""{ "version": 2 }"""u8, "test"))
            .Message.ShouldContain("format version 2");

    [Fact]
    public void Latest_IsTheNewestReleaseThatIsntYanked()
    {
        var entry = new PluginIndexEntry
        {
            Versions =
            [
                new() { Version = "1.9.0" },
                new() { Version = "1.10.0", Yanked = "breaks rollbacks" },
                new() { Version = "1.9.1" },
                new() { Version = "2.0.0-beta.1" },
                new() { Version = "1.9.1-rc.1" }
            ]
        };

        entry.Latest()!.Version.ShouldBe("1.9.1");
        entry.Latest(includePrerelease: true)!.Version.ShouldBe("2.0.0-beta.1");
        new PluginIndexEntry { Versions = [new() { Version = "0.1.0-alpha" }] }.Latest()!.Version.ShouldBe("0.1.0-alpha");
    }

    [Fact]
    public void PackageFor_PrefersThePlatformsOwn_ThenAny()
    {
        var version = new PluginIndexVersion
        {
            Packages = { ["any"] = new() { Url = "any.zip" }, ["linux-x64"] = new() { Url = "linux.zip" } }
        };

        version.PackageFor("linux-x64")!.Value.Package.Url.ShouldBe("linux.zip");
        version.PackageFor("win-x64")!.Value.Platform.ShouldBe("any");
        new PluginIndexVersion { Packages = { ["linux-x64"] = new() } }.PackageFor("win-x64").ShouldBeNull();
    }

    [Fact]
    public void Search_MatchesIdNameAndPublisher_ExactIdFirst()
    {
        var index = new PluginIndex
        {
            Plugins =
            {
                ["fly"] = new() { Name = "Fly.io", Publisher = "Acme" },
                ["render"] = new() { Name = "Render", Publisher = "Fly Fans" },
                ["heroku"] = new() { Name = "Heroku", Publisher = "Someone" }
            }
        };

        index.Search("fly").Select(p => p.Key).ShouldBe(["fly", "render"]);
        index.Search(null).Count().ShouldBe(3);
    }

    // ---- which index, signed by whom ----

    [Fact]
    public void Source_DefaultsToThePublicIndex_SignedByItsWorkflow()
    {
        var source = PluginIndexSource.Resolve(null, null, null, null, _ => null);

        source.IsDefault.ShouldBeTrue();
        source.Signer.Identity.ShouldBe("https://github.com/lv3rmeulen/Deployify/.github/workflows/plugin-index.yml@refs/heads/main");
        source.Signer.Issuer.ShouldBe(PluginSigner.GitHubActionsIssuer);
    }

    [Fact]
    public void Source_AnotherIndex_MustSayWhoSignsIt()
    {
        Should.Throw<PluginException>(() => PluginIndexSource.Resolve("https://plugins.acme.dev/index.json", null, null, null, _ => null))
            .Message.ShouldContain("Say who signs the plugin index");

        var source = PluginIndexSource.Resolve(null, null, null, null, name => name switch
        {
            PluginIndexSource.UrlVariable => "https://plugins.acme.dev/index.json",
            PluginIndexSource.IdentityVariable => "https://github.com/acme/plugins/.github/workflows/index.yml@refs/heads/main",
            _ => null
        });
        source.Url.ShouldBe("https://plugins.acme.dev/index.json");
        source.Signer.Issuer.ShouldBe(PluginSigner.GitHubActionsIssuer);
    }

    [Fact]
    public void Source_ThePublicIndexsSignerCantBeReplaced()
        => Should.Throw<PluginException>(() => PluginIndexSource.Resolve(null, "https://evil", null, null, _ => null));

    [Fact]
    public void VerifyArgs_PinTheSigner()
    {
        BlobSignatureVerifier.VerifyArgs("p.zip", "p.zip.sigstore.json", Signer, null).ShouldBe(
            ["verify-blob", "--bundle", "p.zip.sigstore.json", "--certificate-identity", Keyless,
             "--certificate-oidc-issuer", PluginSigner.GitHubActionsIssuer, "p.zip"]);

        BlobSignatureVerifier.VerifyArgs("p.zip", "b", new PluginSigner { IdentityRegexp = "^x$", Issuer = "i" }, null)
            .ShouldContain("--certificate-identity-regexp");

        BlobSignatureVerifier.VerifyArgs("p.zip", "b", new PluginSigner { PublicKey = "-----BEGIN PUBLIC KEY-----" }, "/tmp/k.pub")
            .ShouldBe(["verify-blob", "--bundle", "b", "--key", "/tmp/k.pub", "p.zip"]);
    }

    [Fact]
    public async Task Verify_NeverAsksCosignWithASignerThatAcceptsAnyone()
    {
        var cosign = new FakeCosign();
        var problem = await new BlobSignatureVerifier(cosign).VerifyAsync("f", "b", new PluginSigner { Identity = "x" }, Ct);

        problem.ShouldNotBeNull();
        cosign.Verified.ShouldBeEmpty();
    }

    // ---- reading the index ----

    private async Task<PluginIndexSource> WriteIndexAsync(PluginIndex index, bool signed = true)
    {
        await File.WriteAllTextAsync(Source.Url, index.Serialize(), Ct);
        if (signed)
        {
            await File.WriteAllTextAsync(Source.Url + PluginIndex.BundleSuffix, "{}", Ct);
        }

        return Source;
    }

    [Fact]
    public async Task LoadIndex_ChecksTheIndexSignatureAgainstItsSigner()
    {
        var source = await WriteIndexAsync(IndexWith(Package()));
        var cosign = new FakeCosign();

        var index = await new PluginIndexClient(verifier: new BlobSignatureVerifier(cosign)).LoadAsync(source, Ct);

        index.Find("example").ShouldNotBeNull();
        cosign.Verified.ShouldHaveSingleItem().ShouldContain("https://idx");
    }

    [Fact]
    public async Task LoadIndex_SignedBySomeoneElse_IsNotRead()
    {
        var source = await WriteIndexAsync(IndexWith(Package()));
        var cosign = new FakeCosign { Verify = _ => false };

        var ex = await Should.ThrowAsync<PluginException>(
            () => new PluginIndexClient(verifier: new BlobSignatureVerifier(cosign)).LoadAsync(source, Ct));

        ex.Message.ShouldContain("isn't signed by keyless, https://idx");
    }

    [Fact]
    public async Task LoadIndex_Unsigned_IsNotRead()
    {
        var source = await WriteIndexAsync(IndexWith(Package()), signed: false);

        var ex = await Should.ThrowAsync<PluginException>(
            () => new PluginIndexClient(verifier: new BlobSignatureVerifier(new FakeCosign())).LoadAsync(source, Ct));

        ex.Message.ShouldContain("has no signature");
    }

    [Fact]
    public async Task LoadIndex_Expired_IsNotUsed()
    {
        var index = IndexWith(Package());
        index.Expires = DateTimeOffset.UtcNow.AddDays(-1);
        var source = await WriteIndexAsync(index);

        var ex = await Should.ThrowAsync<PluginException>(
            () => new PluginIndexClient(verifier: new BlobSignatureVerifier(new FakeCosign())).LoadAsync(source, Ct));

        ex.Message.ShouldContain("expired");
    }

    // ---- installing ----

    [Fact]
    public async Task Install_StagesASignedPackage_ThenPutsItWhereTheCatalogFindsIt()
    {
        var package = Package();
        var cosign = new FakeCosign();
        var installer = new PluginInstaller(verifier: new BlobSignatureVerifier(cosign));

        var staged = await installer.StageAsync(IndexWith(package), Source, "example", null, PluginsFolder, ct: Ct);

        // The package is checked against the publisher's signer, not the index's.
        cosign.Verified.ShouldHaveSingleItem().ShouldContain(Keyless);
        staged.Version.ShouldBe("1.0.0");
        Directory.Exists(staged.Destination).ShouldBeFalse();

        await installer.CommitAsync(staged, Ct);

        var found = new PluginCatalog(userDirectory: PluginsFolder, getVariable: _ => null)
            .Find(Core.Models.DeploymentTargetType.Parse("example"));
        found.ShouldNotBeNull();
        found.ExecutablePath.ShouldBe(staged.InstalledEntryPath);
        found.Files.Folder.ShouldBe(staged.Destination);
        found.Files.Platform.ShouldBe(staged.Files.Platform);
        found.Files.ComputeHash().ShouldBe(staged.Hash);
        Directory.Exists(Path.Combine(_root, "home", ".deployify", ".plugin-staging")).ShouldBeFalse();
    }

    [Fact]
    public async Task Install_ReplacesTheInstalledVersion()
    {
        var installer = new PluginInstaller(verifier: new BlobSignatureVerifier(new FakeCosign()));
        await installer.CommitAsync(await installer.StageAsync(IndexWith(Package()), Source, "example", null, PluginsFolder, ct: Ct), Ct);
        File.WriteAllText(Path.Combine(PluginsFolder, "example", "left-over.txt"), "old");

        await installer.CommitAsync(await installer.StageAsync(IndexWith(Package()), Source, "example", null, PluginsFolder, ct: Ct), Ct);

        File.Exists(Path.Combine(PluginsFolder, "example", "left-over.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task Install_PackageThatDoesntMatchTheIndexHash_IsRefusedBeforeItsSignatureIsEvenChecked()
    {
        var cosign = new FakeCosign();
        var installer = new PluginInstaller(verifier: new BlobSignatureVerifier(cosign));

        var ex = await Should.ThrowAsync<PluginException>(
            () => installer.StageAsync(IndexWith(Package(), sha: new string('0', 64)), Source, "example", null, PluginsFolder, ct: Ct));

        ex.Message.ShouldContain("but the index says");
        cosign.Verified.ShouldBeEmpty();
        Directory.Exists(PluginsFolder).ShouldBeFalse();
    }

    [Fact]
    public async Task Install_PackageNotSignedByItsPublisher_IsRefused()
    {
        var installer = new PluginInstaller(verifier: new BlobSignatureVerifier(new FakeCosign { Verify = _ => false }));

        var ex = await Should.ThrowAsync<PluginException>(
            () => installer.StageAsync(IndexWith(Package()), Source, "example", null, PluginsFolder, ct: Ct));

        ex.Message.ShouldContain("isn't signed by its publisher");
        Directory.Exists(PluginsFolder).ShouldBeFalse();
    }

    [Fact]
    public async Task Install_PackageEscapingItsFolder_IsRefused()
    {
        var package = Package(extra: zip =>
        {
            using var writer = new StreamWriter(zip.CreateEntry("../../evil.txt").Open());
            writer.Write("x");
        });
        var installer = new PluginInstaller(verifier: new BlobSignatureVerifier(new FakeCosign()));

        var ex = await Should.ThrowAsync<PluginException>(
            () => installer.StageAsync(IndexWith(package), Source, "example", null, PluginsFolder, ct: Ct));

        ex.Message.ShouldContain("outside its folder");
        Directory.EnumerateFiles(_root, "evil.txt", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Install_PackageWithoutThePlugin_IsRefused()
    {
        var installer = new PluginInstaller(verifier: new BlobSignatureVerifier(new FakeCosign()));

        var ex = await Should.ThrowAsync<PluginException>(
            () => installer.StageAsync(IndexWith(Package(id: "other"), id: "example"), Source, "example", null, PluginsFolder, ct: Ct));

        ex.Message.ShouldContain("has no deployify-target-example executable");
    }

    [Fact]
    public async Task Install_UnknownVersion_SaysWhichThereAre()
    {
        var installer = new PluginInstaller(verifier: new BlobSignatureVerifier(new FakeCosign()));

        var ex = await Should.ThrowAsync<PluginException>(
            () => installer.StageAsync(IndexWith(Package()), Source, "example", "9.9.9", PluginsFolder, ct: Ct));

        ex.Message.ShouldContain("Versions: 1.0.0");
    }

    // ---- building the index ----

    private string Entries(string json, string id = "example")
    {
        var folder = Path.Combine(_root, "entries");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, id + ".json"), json);
        return folder;
    }

    [Fact]
    public async Task Build_VerifiesEachPackage_AndPinsItsHash()
    {
        var package = Package();
        var folder = Entries($$"""
            {
              "name": "Example Cloud", "publisher": "Acme",
              "signer": { "identity": "{{Keyless}}", "issuer": "{{PluginSigner.GitHubActionsIssuer}}" },
              "versions": [ { "version": "1.0.0", "packages": { "{{PluginFiles.CurrentPlatform}}": { "url": "{{package.Replace("\\", "\\\\")}}" } } } ]
            }
            """);
        var cosign = new FakeCosign();

        var result = await new PluginIndexBuilder(verifier: new BlobSignatureVerifier(cosign)) { RequireHttps = false }.BuildAsync(folder, ct: Ct);

        result.Errors.ShouldBeEmpty();
        result.Index.Find("example")!.Versions[0].Packages[PluginFiles.CurrentPlatform].Sha256.ShouldBe(Sha256(package));
        result.Index.Expires.ShouldNotBeNull();
        cosign.Verified.ShouldHaveSingleItem().ShouldContain(Keyless);

        // What it wrote reads back as a valid, published index.
        PluginIndex.Parse(Encoding.UTF8.GetBytes(result.Index.Serialize()), "built").Plugins.Keys.ShouldBe(["example"]);
    }

    [Fact]
    public async Task Build_ReportsBadSignaturesAndLeavesOutPluginsWithoutVersions()
    {
        var folder = Entries($$"""
            {
              "name": "Example Cloud", "publisher": "Acme",
              "signer": { "identity": "{{Keyless}}", "issuer": "{{PluginSigner.GitHubActionsIssuer}}" },
              "versions": [ { "version": "1.0.0", "packages": { "{{PluginFiles.CurrentPlatform}}": { "url": "{{Package().Replace("\\", "\\\\")}}" } } } ]
            }
            """);
        File.WriteAllText(Path.Combine(folder, "later.json"), $$"""
            { "name": "Later", "publisher": "Acme", "signer": { "identity": "{{Keyless}}", "issuer": "i" }, "versions": [] }
            """);

        var result = await new PluginIndexBuilder(verifier: new BlobSignatureVerifier(new FakeCosign { Verify = _ => false }))
            { RequireHttps = false }.BuildAsync(folder, ct: Ct);

        result.Errors.ShouldHaveSingleItem().ShouldContain("example 1.0.0");
        result.Notes.ShouldHaveSingleItem().ShouldContain("later: no versions yet");
        result.Index.Plugins.ShouldBeEmpty();
    }

    [Fact]
    public async Task Build_RequiresHttpsPackageUrls()
    {
        var folder = Entries($$"""
            {
              "name": "Example Cloud", "publisher": "Acme",
              "signer": { "identity": "{{Keyless}}", "issuer": "i" },
              "versions": [ { "version": "1.0.0", "packages": { "any": { "url": "http://example.com/p.zip" } } } ]
            }
            """);

        var result = await new PluginIndexBuilder(verifier: new BlobSignatureVerifier(new FakeCosign())).BuildAsync(folder, ct: Ct);

        result.Errors.ShouldHaveSingleItem().ShouldContain("https://");
    }

    // ---- the lock file records where a plugin came from ----

    [Fact]
    public async Task LockFile_KeepsTheSource()
    {
        var file = new PluginLockFile();
        file.Plugins["example"] = new PluginLock
        {
            Plugin = "example",
            Version = "1.0.0",
            Sha256 = new(StringComparer.Ordinal) { ["any"] = new string('a', 64) },
            Source = new PluginLockSource { Index = PluginIndexSource.DefaultUrl, Signer = "keyless, x, issued by y" }
        };
        await file.SaveAsync(_root, Ct);

        PluginLockFile.Load(_root)!.Find("example")!.Source!.Index.ShouldBe(PluginIndexSource.DefaultUrl);
    }
}
