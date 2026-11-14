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
/// Dockerfile builds (<c>dockerfile.v0</c>, and <c>container.v1</c> with a <c>build</c> section)
/// are built and pushed by Deployify like projects, and every pushed image's digest is recorded.
/// </summary>
public sealed class DockerfileBuildTests
{
    private const string Digest = PublishedImagesTests.Digest;

    private static AspireManifest Parse(string json) => JsonSerializer.Deserialize<AspireManifest>(json)!;

    // ── Manifest model ──────────────────────────────────────────────────────

    [Fact]
    public void DockerfileV0_IsABuild_WithItsBuildArgs()
    {
        var r = Parse("""
            { "resources": { "web": { "type": "dockerfile.v0", "path": "../web/Dockerfile", "context": "../web",
                                      "buildArgs": { "NODE_VERSION": "22" } } } }
            """).Resources["web"];

        r.DockerfileBuild.ShouldNotBeNull();
        r.DockerfileBuild.Context.ShouldBe("../web");
        r.DockerfileBuild.Dockerfile.ShouldBe("../web/Dockerfile");
        r.DockerfileBuild.Args["NODE_VERSION"].ShouldBe("22");
        r.IsBuiltImage.ShouldBeTrue();
        r.IsDeployable.ShouldBeTrue();
        r.IsProject.ShouldBeFalse();
    }

    [Fact]
    public void ContainerV1_WithABuildSection_IsABuild_EvenWithAnImage()
    {
        var r = Parse("""
            { "resources": { "go": { "type": "container.v1", "image": "goapp:latest",
                                     "build": { "context": "../go", "dockerfile": "../go/Dockerfile.release",
                                                "args": { "GO_VERSION": "1.22" },
                                                "secrets": { "TOKEN": { "type": "env", "value": "{token.value}" } } } } } }
            """).Resources["go"];

        r.DockerfileBuild!.Dockerfile.ShouldBe("../go/Dockerfile.release");
        r.DockerfileBuild.Args["GO_VERSION"].ShouldBe("1.22");
        r.DockerfileBuild.Secrets.ShouldBe(["TOKEN"]);
        r.IsBuiltImage.ShouldBeTrue();
    }

    [Fact]
    public void ContainerWithAnImageOnly_IsNotABuild()
    {
        var r = Parse("""{ "resources": { "cache": { "type": "container.v0", "image": "redis:7" } } }""").Resources["cache"];

        r.DockerfileBuild.ShouldBeNull();
        r.IsBuiltImage.ShouldBeFalse();
        r.IsDeployable.ShouldBeTrue();
    }

    [Fact]
    public void BuildFields_SurviveCopy()
    {
        var r = Parse("""
            { "resources": { "web": { "type": "dockerfile.v0", "path": "w/Dockerfile", "context": "w", "buildArgs": { "A": "1" } } } }
            """).Resources["web"];

        r.Copy().DockerfileBuild.ShouldBe(r.DockerfileBuild, new DockerfileBuildComparer());
    }

    [Fact]
    public void BuildPaths_AreMadeAbsolute_FromTheManifestFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "app");
        var manifestDir = Path.Combine(root, "AppHost", "deployify-output");
        var manifest = Parse("""
            { "resources": {
                "web": { "type": "dockerfile.v0", "path": "../../web/Dockerfile", "context": "../../web" },
                "go":  { "type": "container.v1", "build": { "context": "../../go", "dockerfile": "../../go/Dockerfile" } },
                "api": { "type": "project.v0", "path": "../../Api/Api.csproj" } } }
            """).WithBuildPathsRelativeTo(manifestDir);

        manifest.Resources["web"].DockerfileBuild!.Context.ShouldBe(Path.Combine(root, "web"));
        manifest.Resources["web"].DockerfileBuild!.Dockerfile.ShouldBe(Path.Combine(root, "web", "Dockerfile"));
        manifest.Resources["go"].DockerfileBuild!.Context.ShouldBe(Path.Combine(root, "go"));
        manifest.Resources["api"].Path.ShouldBe("../../Api/Api.csproj"); // project paths are resolved by the publisher
    }

    // ── Build command ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("linux", "x64", "linux/amd64")]
    [InlineData("linux", "arm64", "linux/arm64")]
    [InlineData("linux", "arm", "linux/arm/v7")]
    [InlineData("Linux", "X86", "linux/386")]
    [InlineData("windows", "x64", "windows/amd64")]
    public void Platform_MapsDotnetNames(string os, string arch, string expected)
        => DockerfileBuildCommands.Platform(os, arch).ShouldBe(expected);

    [Fact]
    public void BuildArgs_AreOneListItemEach_InAStableOrder()
    {
        var build = new DockerfileBuild("/src/web", "/src/web/Dockerfile", new System.Collections.Generic.Dictionary<string, string>
        {
            ["B"] = "two words", ["A"] = "1"
        }, []);

        DockerfileBuildCommands.BuildArgs(build, "reg/web:v1", "linux", "x64").ShouldBe(
        [
            "build", "--platform", "linux/amd64", "-f", "/src/web/Dockerfile", "-t", "reg/web:v1",
            "--build-arg", "A=1", "--build-arg", "B=two words", "/src/web"
        ]);
    }

    [Fact]
    public void EnsureBuildable_RefusesBuildSecrets()
        => Should.Throw<InvalidOperationException>(() => DockerfileBuildCommands.EnsureBuildable("go",
                new DockerfileBuild("/x", "/x/Dockerfile", new System.Collections.Generic.Dictionary<string, string>(), ["TOKEN"])))
            .Message.ShouldContain("build secrets (TOKEN)");

    [Fact]
    public void EnsureBuildable_RefusesBuildArgsThatReferToResources()
        => Should.Throw<InvalidOperationException>(() => DockerfileBuildCommands.EnsureBuildable("go",
                new DockerfileBuild("/x", "/x/Dockerfile", new System.Collections.Generic.Dictionary<string, string> { ["V"] = "{ver.value}" }, [])))
            .Message.ShouldContain("(V)");

    [Fact]
    public void EnsureBuildable_RefusesAMissingDockerfile()
    {
        using var dir = new TempDir();
        Should.Throw<InvalidOperationException>(() => DockerfileBuildCommands.EnsureBuildable("web",
                new DockerfileBuild(dir.Path, Path.Combine(dir.Path, "Dockerfile"), new System.Collections.Generic.Dictionary<string, string>(), [])))
            .Message.ShouldContain("does not exist");
    }

    // ── Publisher ───────────────────────────────────────────────────────────

    private static (AspireManifest Manifest, TempDir Dir) WebApp(string extra = "")
    {
        var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "web"));
        File.WriteAllText(Path.Combine(dir.Path, "web", "Dockerfile"), "FROM scratch\n");
        var manifest = Parse($$"""
            { "resources": { "web": { "type": "dockerfile.v0", "path": "web/Dockerfile", "context": "web",
                                      "buildArgs": { "MODE": "prod" } } {{extra}} } }
            """).WithBuildPathsRelativeTo(dir.Path);
        return (manifest, dir);
    }

    private static Publisher Publisher(FakeProcessRunner runner) => new(NullLogger<Publisher>.Instance)
    {
        Runner = runner,
        Time = new FixedTime(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero))
    };

    [Fact]
    public async Task Publisher_BuildsAndPushesADockerfile_AndRecordsTheDigest()
    {
        var (manifest, dir) = WebApp();
        using var _ = dir;
        var runner = new FakeProcessRunner()
            .When(["push"], new ProcessResult(0, $"v1: digest: {Digest} size: 1234\n", ""));
        var config = new EnvironmentConfig { ContainerRegistry = "reg.example.com/app", ImageTag = "v1" };

        await Publisher(runner).PublishAsync(manifest, config, dir.Path, TestContext.Current.CancellationToken);

        var build = runner.Single("build");
        build.FileName.ShouldBe("docker");
        build.Args.ShouldBe(
        [
            "build", "--platform", "linux/amd64", "-f", Path.Combine(dir.Path, "web", "Dockerfile"),
            "-t", "reg.example.com/app/web:v1", "--build-arg", "MODE=prod", Path.Combine(dir.Path, "web")
        ]);
        runner.Single("push").Args.ShouldBe(["push", "reg.example.com/app/web:v1"]);
        config.PublishedImages.TryGetDigest("reg.example.com/app/web", "v1", out var digest).ShouldBeTrue();
        digest.ShouldBe(Digest);
    }

    [Fact]
    public async Task Publisher_KeepsADockerfileBuildLocal_WithoutARegistry()
    {
        var (manifest, dir) = WebApp();
        using var _ = dir;
        var runner = new FakeProcessRunner();
        var config = new EnvironmentConfig { ImageTag = "v1" };

        await Publisher(runner).PublishAsync(manifest, config, dir.Path, TestContext.Current.CancellationToken);

        runner.Single("build").Args.ShouldContain("web:v1");
        runner.Calls.ShouldNotContain(c => c.Args.Contains("push"));
        config.PublishedImages.HasChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task Publisher_WithPodman_ReadsTheDigestFromTheDigestFile()
    {
        var (manifest, dir) = WebApp();
        using var _ = dir;
        var runner = new FakeProcessRunner().When(spec => spec.Args.Contains("--digestfile"), spec =>
        {
            File.WriteAllText(spec.Args[spec.Args.ToList().IndexOf("--digestfile") + 1], Digest + "\n");
            return new ProcessResult(0, "", "");
        });
        var config = new EnvironmentConfig { ContainerRegistry = "reg", ImageTag = "v1", ContainerRuntime = ContainerRuntime.Podman };

        await Publisher(runner).PublishAsync(manifest, config, dir.Path, TestContext.Current.CancellationToken);

        runner.Single("build").FileName.ShouldBe("podman");
        config.PublishedImages.TryGetDigest("reg/web", "v1", out var digest).ShouldBeTrue();
        digest.ShouldBe(Digest);
    }

    [Fact]
    public async Task Publisher_StopsBeforeBuildingAnything_WhenABuildUsesSecrets()
    {
        using var dir = new TempDir();
        var manifest = Parse("""
            { "resources": {
                "api": { "type": "project.v0", "path": "Api/Api.csproj" },
                "go": { "type": "container.v1", "build": { "context": "go", "dockerfile": "go/Dockerfile",
                         "secrets": { "TOKEN": { "type": "env" } } } } } }
            """);
        var runner = new FakeProcessRunner();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Publisher(runner).PublishAsync(manifest, new EnvironmentConfig(), dir.Path, TestContext.Current.CancellationToken));

        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Publisher_RecordsAProjectsDigest_FromDotnetPublish()
    {
        using var dir = new TempDir();
        var manifest = Parse("""{ "resources": { "api": { "type": "project.v0", "path": "Api/Api.csproj" } } }""");
        var runner = new FakeProcessRunner().When(["-t:PublishContainer"], new ProcessResult(0,
            $$"""{ "Properties": { "GeneratedContainerDigest": "{{Digest}}", "GeneratedContainerNames": "" } }""", ""));
        var config = new EnvironmentConfig { ContainerRegistry = "reg.example.com/app", ImageTag = "v1" };

        await Publisher(runner).PublishAsync(manifest, config, dir.Path, TestContext.Current.CancellationToken);

        var publish = runner.Single("-t:PublishContainer");
        publish.Args.ShouldContain("-getProperty:GeneratedContainerDigest");
        publish.Args.ShouldContain("-getProperty:GeneratedContainerNames");
        config.PublishedImages.TryGetDigest("reg.example.com/app/api", "v1", out var digest).ShouldBeTrue();
        digest.ShouldBe(Digest);
    }

    [Fact]
    public async Task Publisher_RecordsNoDigest_ForALocalProjectImage()
    {
        using var dir = new TempDir();
        var manifest = Parse("""{ "resources": { "api": { "type": "project.v0", "path": "Api/Api.csproj" } } }""");
        var runner = new FakeProcessRunner().When(["-t:PublishContainer"], new ProcessResult(0,
            $$"""{ "Properties": { "GeneratedContainerDigest": "{{Digest}}" } }""", ""));
        var config = new EnvironmentConfig { ImageTag = "v1" };

        await Publisher(runner).PublishAsync(manifest, config, dir.Path, TestContext.Current.CancellationToken);

        config.PublishedImages.HasChanges.ShouldBeFalse();
    }

    [Theory]
    [InlineData("v1: digest: " + Digest + " size: 1234", Digest)]
    [InlineData("The push refers to repository [reg/web]\nabc: Pushed\nv1: digest: " + Digest + " size: 9", Digest)]
    [InlineData("pushed, no digest here", null)]
    public void ReadPushDigest(string output, string? expected)
        => Deployify.Core.Services.Publisher.ReadPushDigest(output).ShouldBe(expected);

    [Theory]
    [InlineData("""{ "Properties": { "GeneratedContainerDigest": "" } }""", null)]
    [InlineData("error MSB4018: boom\n{ \"Properties\": { \"GeneratedContainerDigest\": \"" + Digest + "\" } }", Digest)]
    public void ReadSdkDigest(string output, string? expected)
        => Deployify.Core.Services.Publisher.ReadSdkDigest(output).ShouldBe(expected);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DockerfileBuildComparer : System.Collections.Generic.IEqualityComparer<DockerfileBuild?>
    {
        public bool Equals(DockerfileBuild? x, DockerfileBuild? y)
            => x is not null && y is not null && x.Context == y.Context && x.Dockerfile == y.Dockerfile &&
               x.Args.OrderBy(a => a.Key).SequenceEqual(y.Args.OrderBy(a => a.Key)) && x.Secrets.SequenceEqual(y.Secrets);

        public int GetHashCode(DockerfileBuild? obj) => 0;
    }
}
