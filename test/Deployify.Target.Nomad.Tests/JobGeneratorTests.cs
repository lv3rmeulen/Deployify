using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

public sealed class JobGeneratorTests
{
    private readonly JobGenerator _sut = new(NullLogger<JobGenerator>.Instance);

    [Fact]
    public async Task Generate_SkipsParametersAndValues()
    {
        var jobs = await _sut.GenerateAsync(Fixtures.ManifestWithIgnoredTypes(), Fixtures.NomadConfig(), TestContext.Current.CancellationToken);
        jobs.ShouldHaveSingleItem().Name.ShouldBe("api");
    }

    [Fact]
    public async Task Generate_SetsImage_FromRegistry_ForProjects()
    {
        var config = Fixtures.NomadConfig();
        config.ContainerRegistry = "registry.example.com/app";
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("api"), config, TestContext.Current.CancellationToken);
        jobs[0].Image.ShouldBe("registry.example.com/app/api");
    }

    [Fact]
    public async Task Generate_SetsImageTag_FromConfig()
    {
        var config = Fixtures.NomadConfig();
        config.ImageTag = "sha-abc";
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService(), config, TestContext.Current.CancellationToken);
        jobs[0].ImageTag.ShouldBe("sha-abc");
    }

    [Fact]
    public async Task Generate_MergesEnvVars()
    {
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.NomadConfig(), TestContext.Current.CancellationToken);
        jobs.Single(j => j.Name == "api").Env
            .ShouldContainKey("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public async Task Generate_PopulatesPorts()
    {
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.NomadConfig(), TestContext.Current.CancellationToken);
        jobs.Single(j => j.Name == "api").Ports.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Generate_PopulatesDependencies()
    {
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.NomadConfig(), TestContext.Current.CancellationToken);
        jobs.Single(j => j.Name == "worker").Dependencies.ShouldContain("api");
        // single item implied;
    }

    [Fact]
    public async Task Generate_RespectsOverride_Count()
    {
        var config = Fixtures.NomadConfig();
        config.ServiceOverrides["api"] = new ServiceOverride { Count = 5 };
        (await _sut.GenerateAsync(Fixtures.SimpleManifest(), config, TestContext.Current.CancellationToken))
            .Single(j => j.Name == "api").Count.ShouldBe(5);
    }

    [Fact]
    public async Task Generate_RespectsOverride_ImageTag()
    {
        var config = Fixtures.NomadConfig();
        config.ServiceOverrides["api"] = new ServiceOverride { ImageTag = "canary" };
        (await _sut.GenerateAsync(Fixtures.SimpleManifest(), config, TestContext.Current.CancellationToken))
            .Single(j => j.Name == "api").ImageTag.ShouldBe("canary");
    }

    [Fact]
    public async Task WriteHcl_CreatesFile_WithJobName()
    {
        using var dir = new TempDir();
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("myservice"), Fixtures.NomadConfig(), TestContext.Current.CancellationToken);
        var path = await _sut.WriteHclAsync(jobs[0], Fixtures.NomadConfig(), dir.Path, TestContext.Current.CancellationToken);
        File.Exists(path).ShouldBeTrue();
        path.ShouldEndWith("myservice.job.hcl");
    }

    [Fact]
    public async Task WriteHcl_ContainsJobName()
    {
        using var dir = new TempDir();
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("myservice"), Fixtures.NomadConfig(), TestContext.Current.CancellationToken);
        var path = await _sut.WriteHclAsync(jobs[0], Fixtures.NomadConfig(), dir.Path, TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldContain("\"myservice\"");
    }

    [Fact]
    public async Task WriteHcl_ContainsDatacenter()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig(dc: "us-east-1");
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService(), config, TestContext.Current.CancellationToken);
        var path = await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldContain("us-east-1");
    }

    [Fact]
    public async Task WriteHcl_ContainsNamespace()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig(ns: "production");
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService(), config, TestContext.Current.CancellationToken);
        var path = await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldContain("\"production\"");
    }

    [Fact]
    public async Task WriteHcl_ContainsEnvVars()
    {
        using var dir = new TempDir();
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.NomadConfig(), TestContext.Current.CancellationToken);
        var path = await _sut.WriteHclAsync(jobs.Single(j => j.Name == "api"), Fixtures.NomadConfig(), dir.Path, TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldContain("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public async Task WriteHcl_GeneratesAllServices()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        foreach (var job in await _sut.GenerateAsync(Fixtures.SimpleManifest(), config, TestContext.Current.CancellationToken))
        {
            await _sut.WriteHclAsync(job, config, dir.Path, TestContext.Current.CancellationToken);
        }

        Directory.GetFiles(dir.Path, "*.hcl").Length.ShouldBe(3);
    }
}
