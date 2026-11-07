using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
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
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        hcl.TrimStart().ShouldStartWith("job");
    }

    [Fact]
    public async Task Hcl_ContainsDockerDriver()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        hcl.ShouldContain("docker");
    }

    [Fact]
    public async Task Hcl_ContainsPodmanDriver_WhenRuntimeIsPodman()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        config.ContainerRuntime = ContainerRuntime.Podman;
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        hcl.ShouldContain("podman");
        hcl.ShouldNotContain("driver = \"docker\"");
    }

    [Fact]
    public async Task Hcl_ContainsRawExecDriver_WhenModeIsRawExec()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfigWithMode(NomadDeploymentMode.RawExec);
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        hcl.ShouldContain("driver = \"raw_exec\"");
    }

    [Fact]
    public async Task Hcl_UsesCommandNotImage_WhenModeIsRawExec()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfigWithMode(NomadDeploymentMode.RawExec);
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
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
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        hcl.ShouldNotContain("daprio/daprd");
    }

    [Fact]
    public async Task Hcl_ContainsPortLabels_ForEachBinding()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), config);
        var api = jobs.Single(j => j.Name == "api");
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(api, config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
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
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        hcl.ShouldContain("count = 4");
    }

    [Fact]
    public async Task Hcl_ContainsNetworkBlock()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        hcl.ShouldContain("network");
    }

    [Fact]
    public async Task Hcl_ContainsEnvBlock_WhenEnvVarsPresent()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), config);
        var api = jobs.Single(j => j.Name == "api");
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(api, config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        hcl.ShouldContain("env = {");
        hcl.ShouldContain("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public async Task Hcl_DoesNotContainRegistryPassword_OnDisk()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        config.ContainerRegistry = "reg.io/myapp";
        config.TargetConfig["nomad"] = System.Text.Json.JsonSerializer.SerializeToElement(
            new NomadTargetConfig { RegistryUsername = "bob", RegistryPassword = "s3cr3t-\"pw" },
            TargetConfigJsonOptions.Default);
        var jobs = _sut.Generate(Fixtures.SingleService("api"), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        hcl.ShouldContain("auth {");
        hcl.ShouldNotContain("s3cr3t");
        hcl.ShouldContain(Hcl.RegistryPasswordPlaceholder);

        var injected = Hcl.InjectRegistryPassword(hcl, "s3cr3t-\"pw");
        injected.ShouldContain("password = \"s3cr3t-\\\"pw\"");
        injected.ShouldNotContain(Hcl.RegistryPasswordPlaceholder);
    }

    [Fact]
    public async Task Hcl_ContainsImageWithTag()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        config.ContainerRegistry = "reg.io/myapp";
        config.ImageTag = "v2";
        var jobs = _sut.Generate(Fixtures.SingleService("api"), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        hcl.ShouldContain("reg.io/myapp/api:v2");
    }

    [Fact]
    public async Task Hcl_NoEnvBlock_WhenNoEnvVars()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = _sut.Generate(Fixtures.SingleService("worker"), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        hcl.ShouldNotContain("env {");
    }

    [Fact]
    public async Task Hcl_ServiceType_IsService()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        hcl.ShouldContain("\"service\"");
    }

    [Fact]
    public async Task Hcl_ContainsDatacenter()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig(dc: "eu-west-1");
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        hcl.ShouldContain("eu-west-1");
    }

    [Fact]
    public async Task Hcl_ContainsNamespace()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig(ns: "staging");
        var jobs = _sut.Generate(Fixtures.SingleService(), config);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        hcl.ShouldContain("\"staging\"");
    }
}
