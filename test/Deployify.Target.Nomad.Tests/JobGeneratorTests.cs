using Deployify.Core.Models;
using Shouldly;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

public sealed class JobGeneratorTests
{
    private readonly JobGenerator _sut = new(NullLogger<JobGenerator>.Instance);

    [Fact]
    public void Generate_SkipsParametersAndValues()
    {
        var jobs = _sut.Generate(Fixtures.ManifestWithIgnoredTypes(), Fixtures.NomadConfig());
        jobs.ShouldHaveSingleItem().Name.ShouldBe("api");
    }

    [Fact]
    public void Generate_SetsImage_FromRegistry_ForProjects()
    {
        var config = Fixtures.NomadConfig();
        config.ContainerRegistry = "registry.example.com/app";
        var jobs = _sut.Generate(Fixtures.SingleService("api"), config);
        jobs[0].Image.ShouldBe("registry.example.com/app/api");
    }

    [Fact]
    public void Generate_SetsImageTag_FromConfig()
    {
        var config = Fixtures.NomadConfig(); config.ImageTag = "sha-abc";
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        jobs[0].ImageTag.ShouldBe("sha-abc");
    }

    [Fact]
    public void Generate_MergesEnvVars()
    {
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.NomadConfig());
        jobs.Single(j => j.Name == "api").Env
            .ShouldContainKey("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public void Generate_PopulatesPorts()
    {
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.NomadConfig());
        jobs.Single(j => j.Name == "api").Ports.Count.ShouldBe(2);
    }

    [Fact]
    public void Generate_PopulatesDependencies()
    {
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.NomadConfig());
        jobs.Single(j => j.Name == "worker").Dependencies.ShouldContain("api");
        // single item implied;
    }

    [Fact]
    public void Generate_RespectsOverride_Count()
    {
        var config = Fixtures.NomadConfig();
        config.ServiceOverrides["api"] = new ServiceOverride { Count = 5 };
        _sut.Generate(Fixtures.SimpleManifest(), config)
            .Single(j => j.Name == "api").Count.ShouldBe(5);
    }

    [Fact]
    public void Generate_RespectsOverride_ImageTag()
    {
        var config = Fixtures.NomadConfig();
        config.ServiceOverrides["api"] = new ServiceOverride { ImageTag = "canary" };
        _sut.Generate(Fixtures.SimpleManifest(), config)
            .Single(j => j.Name == "api").ImageTag.ShouldBe("canary");
    }

    [Fact]
    public async Task WriteHcl_CreatesFile_WithJobName()
    {
        using var dir = new TempDir();
        var jobs = _sut.Generate(Fixtures.SingleService("myservice"), Fixtures.NomadConfig());
        var path = await _sut.WriteHclAsync(jobs[0], Fixtures.NomadConfig(), dir.Path);
        File.Exists(path).ShouldBeTrue();
        path.ShouldEndWith("myservice.job.hcl");
    }

    [Fact]
    public async Task WriteHcl_ContainsJobName()
    {
        using var dir = new TempDir();
        var jobs = _sut.Generate(Fixtures.SingleService("myservice"), Fixtures.NomadConfig());
        var path = await _sut.WriteHclAsync(jobs[0], Fixtures.NomadConfig(), dir.Path);
        (await File.ReadAllTextAsync(path)).ShouldContain("\"myservice\"");
    }

    [Fact]
    public async Task WriteHcl_ContainsDatacenter()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig(dc: "us-east-1");
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var path = await _sut.WriteHclAsync(jobs[0], config, dir.Path);
        (await File.ReadAllTextAsync(path)).ShouldContain("us-east-1");
    }

    [Fact]
    public async Task WriteHcl_ContainsNamespace()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig(ns: "production");
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var path = await _sut.WriteHclAsync(jobs[0], config, dir.Path);
        (await File.ReadAllTextAsync(path)).ShouldContain("\"production\"");
    }

    [Fact]
    public async Task WriteHcl_ContainsEnvVars()
    {
        using var dir = new TempDir();
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.NomadConfig());
        var path = await _sut.WriteHclAsync(jobs.Single(j => j.Name == "api"), Fixtures.NomadConfig(), dir.Path);
        (await File.ReadAllTextAsync(path)).ShouldContain("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public async Task WriteHcl_GeneratesAllServices()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        foreach (var job in _sut.Generate(Fixtures.SimpleManifest(), config))
        {
	        await _sut.WriteHclAsync(job, config, dir.Path);
        }

        Directory.GetFiles(dir.Path, "*.hcl").Length.ShouldBe(3);
    }
}
