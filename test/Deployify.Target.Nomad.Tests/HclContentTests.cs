using Deployify.Core.Models;
using Shouldly;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

public sealed class HclContentTests
{
    private readonly JobGenerator _sut = new(NullLogger<JobGenerator>.Instance);

    [Fact]
    public async Task Hcl_StartsWithJobBlock()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = _sut.Generate(Fixtures.SingleService("myapp"), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path));
        hcl.TrimStart().ShouldStartWith("job");
    }

    [Fact]
    public async Task Hcl_ContainsDockerDriver()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path));
        hcl.ShouldContain("docker");
    }

    [Fact]
    public async Task Hcl_ContainsPodmanDriver_WhenRuntimeIsPodman()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        config.ContainerRuntime = ContainerRuntime.Podman;
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path));
        hcl.ShouldContain("podman");
        hcl.ShouldNotContain("driver = \"docker\"");
    }

    [Fact]
    public async Task Hcl_ContainsRawExecDriver_WhenModeIsRawExec()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfigWithMode(NomadDeploymentMode.RawExec);
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path));
        hcl.ShouldContain("driver = \"raw_exec\"");
    }

    [Fact]
    public async Task Hcl_UsesCommandNotImage_WhenModeIsRawExec()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfigWithMode(NomadDeploymentMode.RawExec);
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path));
        hcl.ShouldContain("command =");
        hcl.ShouldNotContain("image =");
    }

    [Fact]
    public async Task Hcl_NoDaprSidecar_WhenModeIsRawExec()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfigWithMode(NomadDeploymentMode.RawExec);
        config.DeployDapr = true;
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path));
        hcl.ShouldNotContain("daprio/daprd");
    }

    [Fact]
    public async Task Hcl_ContainsHttpAndHttpsProtocols()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), config);
        var api = jobs.Single(j => j.Name == "api");
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(api, config, dir.Path));
        hcl.ShouldContain("\"http\"");
        hcl.ShouldContain("\"https\"");
    }

    [Fact]
    public async Task Hcl_ContainsCount()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride { Count = 4 };
        var jobs = _sut.Generate(Fixtures.SingleService("myapp"), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path));
        hcl.ShouldContain("count = 4");
    }

    [Fact]
    public async Task Hcl_ContainsNetworkBlock()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path));
        hcl.ShouldContain("network");
    }

    [Fact]
    public async Task Hcl_ContainsEnvBlock_WhenEnvVarsPresent()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), config);
        var api = jobs.Single(j => j.Name == "api");
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(api, config, dir.Path));
        hcl.ShouldContain("env {");
        hcl.ShouldContain("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public async Task Hcl_ContainsImageWithTag()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        config.ContainerRegistry = "reg.io/myapp";
        config.ImageTag = "v2";
        var jobs = _sut.Generate(Fixtures.SingleService("api"), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path));
        hcl.ShouldContain("reg.io/myapp/api:v2");
    }

    [Fact]
    public async Task Hcl_NoEnvBlock_WhenNoEnvVars()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = _sut.Generate(Fixtures.SingleService("worker"), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path));
        hcl.ShouldNotContain("env {");
    }

    [Fact]
    public async Task Hcl_ServiceType_IsService()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path));
        hcl.ShouldContain("\"service\"");
    }

    [Fact]
    public async Task Hcl_ContainsDatacenter()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig(dc: "eu-west-1");
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path));
        hcl.ShouldContain("eu-west-1");
    }

    [Fact]
    public async Task Hcl_ContainsNamespace()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig(ns: "staging");
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path));
        hcl.ShouldContain("\"staging\"");
    }
}
