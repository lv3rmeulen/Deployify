using System;
using System.IO;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

public sealed class ArtifactStoreTests
{
    private readonly JobGenerator _sut = new(NullLogger<JobGenerator>.Instance);

    // ── HCL artifact block ────────────────────────────────────────────────────

    [Fact]
    public async Task Hcl_ContainsArtifactBlock_WhenArtifactUrlSet()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfigWithMode(NomadDeploymentMode.RawExec);
        config.ServiceOverrides["myapp"] = new ServiceOverride
        {
            ArtifactUrl = "https://artifacts.example.com/api-v1.0.0.zip"
        };
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(
            await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        hcl.ShouldContain("artifact {");
        hcl.ShouldContain("source = \"https://artifacts.example.com/api-v1.0.0.zip\"");
        hcl.ShouldContain("destination = \"local/\"");
        // go-getter takes the archive format here; "true" leaves the zip packed.
        hcl.ShouldContain("archive = \"zip\"");
    }

    [Fact]
    public async Task Hcl_NoArtifactBlock_WhenArtifactUrlNotSet()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfigWithMode(NomadDeploymentMode.RawExec);
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(
            await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        hcl.ShouldNotContain("artifact {");
    }

    [Fact]
    public async Task Hcl_CommandUsesLocalPath_WhenArtifactUrlSet()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfigWithMode(NomadDeploymentMode.RawExec);
        config.ServiceOverrides["myapp"] = new ServiceOverride
        {
            ArtifactUrl = "https://artifacts.example.com/api-v1.0.0.zip"
        };
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(
            await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        hcl.ShouldContain("command = \"local/myapp\"");
    }

    [Fact]
    public async Task Hcl_ArtifactBlock_NotEmitted_ForDockerMode()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride
        {
            ArtifactUrl = "https://artifacts.example.com/api-v1.0.0.zip"
        };
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(
            await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        // Docker mode should use image = not artifact + command =
        hcl.ShouldNotContain("artifact {");
        hcl.ShouldContain("image =");
    }

    [Fact]
    public void Hcl_ArtifactUrl_ReadFromServiceOverrides()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfigWithMode(NomadDeploymentMode.RawExec);
        config.ServiceOverrides["myapp"] = new ServiceOverride
        {
            ArtifactUrl = "s3::https://s3.us-east-1.amazonaws.com/mybucket/api-v2.0.0.zip"
        };
        var jobs = _sut.Generate(Fixtures.SingleService(), config);

        jobs[0].ArtifactUrl.ShouldBe(
            "s3::https://s3.us-east-1.amazonaws.com/mybucket/api-v2.0.0.zip");
    }

    // ── ArtifactStoreConfig ───────────────────────────────────────────────────

    [Fact]
    public void ArtifactStoreFactory_CreatesHttpStore()
    {
        var cfg = new ArtifactStoreConfig { Type = ArtifactStoreType.Http, HttpBaseUrl = "http://artifacts.example.com" };
        var store = ArtifactStoreFactory.Create(cfg, NullLogger.Instance);
        store.ShouldBeOfType<HttpArtifactStore>();
    }

    [Fact]
    public void ArtifactStoreFactory_CreatesS3Store()
    {
        var cfg = new ArtifactStoreConfig { Type = ArtifactStoreType.S3, S3Bucket = "mybucket" };
        var store = ArtifactStoreFactory.Create(cfg, NullLogger.Instance);
        store.ShouldBeOfType<S3ArtifactStore>();
    }

    [Fact]
    public void ArtifactStoreFactory_CreatesAzureBlobStore()
    {
        var cfg = new ArtifactStoreConfig { Type = ArtifactStoreType.AzureBlob };
        var store = ArtifactStoreFactory.Create(cfg, NullLogger.Instance);
        store.ShouldBeOfType<AzureBlobArtifactStore>();
    }

    [Fact]
    public void ArtifactStoreFactory_CreatesNexusStore()
    {
        var cfg = new ArtifactStoreConfig { Type = ArtifactStoreType.Nexus };
        var store = ArtifactStoreFactory.Create(cfg, NullLogger.Instance);
        store.ShouldBeOfType<NexusArtifactStore>();
    }

    [Fact]
    public void ArtifactStoreFactory_CreatesArtifactoryStore()
    {
        var cfg = new ArtifactStoreConfig { Type = ArtifactStoreType.Artifactory };
        var store = ArtifactStoreFactory.Create(cfg, NullLogger.Instance);
        store.ShouldBeOfType<ArtifactoryArtifactStore>();
    }

    [Fact]
    public void ArtifactStoreFactory_CreatesGcsStore()
    {
        var cfg = new ArtifactStoreConfig { Type = ArtifactStoreType.Gcs };
        var store = ArtifactStoreFactory.Create(cfg, NullLogger.Instance);
        store.ShouldBeOfType<GcsArtifactStore>();
    }

    [Fact]
    public void ArtifactStoreFactory_Throws_ForUnknownType()
    {
        var cfg = new ArtifactStoreConfig { Type = (ArtifactStoreType)99 };
        Should.Throw<NotSupportedException>(() =>
            ArtifactStoreFactory.Create(cfg, NullLogger.Instance));
    }

    // ── HttpArtifactStore validation ──────────────────────────────────────────

    [Fact]
    public async Task HttpStore_Throws_WhenBaseUrlNotSet()
    {
        var store = new HttpArtifactStore(new ArtifactStoreConfig(), NullLogger.Instance);
        await Should.ThrowAsync<InvalidOperationException>(() =>
            store.UploadAsync("api", "v1", "fake.zip", TestContext.Current.CancellationToken));
    }

    // ── S3 URL format ─────────────────────────────────────────────────────────

    [Fact]
    public void S3Store_UrlFormat_ContainsBucketAndKey()
    {
        // The URL Nomad uses for S3: s3::https://s3.{region}.amazonaws.com/{bucket}/{key}
        var region = "us-east-1";
        var bucket = "mybucket";
        var key = "api-v1.0.0.zip";
        var url = $"s3::https://s3.{region}.amazonaws.com/{bucket}/{key}";
        url.ShouldStartWith("s3::");
        url.ShouldContain(bucket);
        url.ShouldContain(key);
    }

    // ── ServiceOverride ArtifactUrl round-trip ────────────────────────────────

    [Fact]
    public void ServiceOverride_ArtifactUrl_RoundTrips_ThroughJson()
    {
        var state = new DeployifyStateFile
        {
            Environments =
            {
                ["prod"] = new EnvironmentConfig
                {
                    ServiceOverrides =
                    {
                        ["api"] = new ServiceOverride { ArtifactUrl = "https://example.com/api-v1.zip" }
                    }
                }
            }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var back = System.Text.Json.JsonSerializer.Deserialize<DeployifyStateFile>(json)!;
        back.Environments["prod"].ServiceOverrides["api"].ArtifactUrl
            .ShouldBe("https://example.com/api-v1.zip");
    }
}
