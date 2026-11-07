using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

public sealed class ManifestReaderTests
{
    private readonly ManifestReader _sut = new(NullLogger<ManifestReader>.Instance);

    private static async Task<string> WriteManifestAsync(string dir, object obj, CancellationToken ct = default)
    {
        var path = Path.Combine(dir, "manifest.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(obj), ct);
        return path;
    }

    [Fact]
    public async Task ReadAsync_ParsesProjectResources()
    {
        using var dir = new TempDir();
        var path = await WriteManifestAsync(dir.Path, new
        {
            resources = new { api = new { type = "project.v0", path = "Api/Api.csproj" } }
        }, TestContext.Current.CancellationToken);
        var manifest = await _sut.ReadAsync(path, TestContext.Current.CancellationToken);
        manifest.Resources.ShouldContainKey("api");
        manifest.Resources["api"].Type.ShouldBe("project.v0");
    }

    [Fact]
    public async Task ReadAsync_ParsesContainerImage()
    {
        using var dir = new TempDir();
        var path = await WriteManifestAsync(dir.Path, new
        {
            resources = new { redis = new { type = "container.v0", image = "redis:7" } }
        }, TestContext.Current.CancellationToken);
        var manifest = await _sut.ReadAsync(path, TestContext.Current.CancellationToken);
        manifest.Resources["redis"].Image.ShouldBe("redis:7");
    }

    [Fact]
    public async Task ReadAsync_ParsesBindings()
    {
        using var dir = new TempDir();
        var path = await WriteManifestAsync(dir.Path, new
        {
            resources = new
            {
                api = new
                {
                    type = "project.v0",
                    bindings = new { http = new { containerPort = 8080, protocol = "http" } }
                }
            }
        }, TestContext.Current.CancellationToken);
        var manifest = await _sut.ReadAsync(path, TestContext.Current.CancellationToken);
        manifest.Resources["api"].Bindings?.ShouldContainKey("http");
        manifest.Resources["api"].Bindings?["http"].ContainerPort.ShouldBe(8080);
    }

    [Fact]
    public async Task ReadAsync_ParsesEnvVars()
    {
        using var dir = new TempDir();
        var path = await WriteManifestAsync(dir.Path, new
        {
            resources = new
            {
                api = new { type = "project.v0", env = new { ASPNETCORE_ENVIRONMENT = "Production" } }
            }
        }, TestContext.Current.CancellationToken);
        var manifest = await _sut.ReadAsync(path, TestContext.Current.CancellationToken);
        manifest.Resources["api"].Env?.ShouldContainKey("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public async Task ReadAsync_ParsesReferences()
    {
        using var dir = new TempDir();
        var path = await WriteManifestAsync(dir.Path, new
        {
            resources = new
            {
                worker = new { type = "project.v0", references = new[] { new { name = "api" } } }
            }
        }, TestContext.Current.CancellationToken);
        var manifest = await _sut.ReadAsync(path, TestContext.Current.CancellationToken);
        manifest.Resources["worker"].References?.ShouldContain(r => r.Name == "api");
    }

    [Fact]
    public async Task ReadAsync_HandlesMultipleResources()
    {
        using var dir = new TempDir();
        var path = await WriteManifestAsync(dir.Path, new
        {
            resources = new
            {
                api = new { type = "project.v0" },
                worker = new { type = "project.v0" },
                redis = new { type = "container.v0", image = "redis:7" }
            }
        }, TestContext.Current.CancellationToken);
        var manifest = await _sut.ReadAsync(path, TestContext.Current.CancellationToken);
        manifest.Resources.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ReadAsync_ThrowsFileNotFound_WhenMissing()
    {
        await Should.ThrowAsync<FileNotFoundException>(
            () => _sut.ReadAsync("/nonexistent/path/manifest.json", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_ThrowsOnInvalidJson()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "manifest.json");
        await File.WriteAllTextAsync(path, "{ not valid json {{", TestContext.Current.CancellationToken);
        await Should.ThrowAsync<Exception>(
            () => _sut.ReadAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_ReturnsEmptyResources_WhenNoneInFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "manifest.json");
        await File.WriteAllTextAsync(path, "{}", TestContext.Current.CancellationToken);
        var manifest = await _sut.ReadAsync(path, TestContext.Current.CancellationToken);
        manifest.Resources.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_ParsesParameterAndValueTypes()
    {
        using var dir = new TempDir();
        var path = await WriteManifestAsync(dir.Path, new
        {
            resources = new
            {
                cfg = new { type = "parameter.v0" },
                val = new { type = "value.v0" },
                api = new { type = "project.v0" }
            }
        }, TestContext.Current.CancellationToken);
        var manifest = await _sut.ReadAsync(path, TestContext.Current.CancellationToken);
        manifest.Resources.Count.ShouldBe(3);
        manifest.Resources["cfg"].Type.ShouldBe("parameter.v0");
    }
}
