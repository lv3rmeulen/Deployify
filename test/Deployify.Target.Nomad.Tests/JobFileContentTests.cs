using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Target.Nomad.Api;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

/// <summary>What the generated job file says (the job API's JSON).</summary>
public sealed class JobFileContentTests
{
    private readonly JobGenerator _sut = new(NullLogger<JobGenerator>.Instance);

    private async Task<NomadJobJson> JobAsync(AspireManifest manifest, EnvironmentConfig config, string? name = null)
    {
        using var dir = new TempDir();
        var jobs = await _sut.GenerateAsync(manifest, config, TestContext.Current.CancellationToken);
        var job = name is null ? jobs[0] : jobs.Single(j => j.Name == name);
        return await NomadJobJson.ReadAsync(await _sut.WriteJobAsync(job, config, dir.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task File_IsAJobRequest_NamedAfterTheJob()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("myapp"), config, TestContext.Current.CancellationToken);

        var path = await _sut.WriteJobAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken);

        System.IO.Path.GetFileName(path).ShouldBe("myapp.nomad.json");
        var job = await NomadJobJson.ReadAsync(path);
        ((string?)job.Job["ID"]).ShouldBe("myapp");
        ((string?)job.Job["Name"]).ShouldBe("myapp");
        ((string?)job.Group["Name"]).ShouldBe("myapp");
        ((string?)job.Job["Meta"]![NomadClient.ManagedMetaKey]).ShouldBe("true");
    }

    [Fact]
    public async Task Write_RemovesTheJobsOldHclFile()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("myapp"), config, TestContext.Current.CancellationToken);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(dir.Path, "myapp.job.hcl"), "job \"myapp\" {}", TestContext.Current.CancellationToken);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(dir.Path, "other.job.hcl"), "job \"other\" {}", TestContext.Current.CancellationToken);

        await _sut.WriteJobAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken);

        System.IO.File.Exists(System.IO.Path.Combine(dir.Path, "myapp.job.hcl")).ShouldBeFalse();
        System.IO.File.Exists(System.IO.Path.Combine(dir.Path, "other.job.hcl")).ShouldBeTrue();
    }

    [Fact]
    public async Task Driver_IsDocker()
        => ((string?)(await JobAsync(Fixtures.SingleService(), Fixtures.NomadConfig())).Task()["Driver"]).ShouldBe("docker");

    [Fact]
    public async Task Driver_IsPodman_WhenRuntimeIsPodman()
    {
        var config = Fixtures.NomadConfig();
        config.ContainerRuntime = ContainerRuntime.Podman;

        ((string?)(await JobAsync(Fixtures.SingleService(), config)).Task()["Driver"]).ShouldBe("podman");
    }

    [Fact]
    public async Task Driver_IsRawExec_WhenModeIsRawExec()
        => ((string?)(await JobAsync(Fixtures.SingleService(), Fixtures.NomadConfigWithMode(NomadDeploymentMode.RawExec))).Task()["Driver"])
            .ShouldBe("raw_exec");

    [Fact]
    public async Task RawExec_UsesCommandNotImage()
    {
        var job = await JobAsync(Fixtures.SingleService(), Fixtures.NomadConfigWithMode(NomadDeploymentMode.RawExec));

        job.Config().ContainsKey("command").ShouldBeTrue();
        job.Config().ContainsKey("image").ShouldBeFalse();
    }

    [Fact]
    public async Task RawExec_HasNoDaprSidecar()
    {
        var config = Fixtures.NomadConfigWithMode(NomadDeploymentMode.RawExec);
        config.DeployDapr = true;

        (await JobAsync(Fixtures.SingleService(), config)).Text.ShouldNotContain("daprio/daprd");
    }

    [Fact]
    public async Task Network_HasAStaticPortPerBinding()
    {
        var job = await JobAsync(Fixtures.SimpleManifest(), Fixtures.NomadConfig(), "api");

        ((string?)job.Network!["Mode"]).ShouldBe("host");
        job.PortLabels.ShouldBe(["http", "https"]);
        job.Network["ReservedPorts"]!.AsArray().ShouldAllBe(p => (int)p!["Value"]! > 0);
    }

    [Fact]
    public async Task Count_ComesFromTheServiceOverride()
    {
        var config = Fixtures.NomadConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride { Count = 4 };

        ((int?)(await JobAsync(Fixtures.SingleService("myapp"), config)).Group["Count"]).ShouldBe(4);
    }

    [Fact]
    public async Task Env_HoldsTheServicesVariables()
    {
        var env = (await JobAsync(Fixtures.SimpleManifest(), Fixtures.NomadConfig(), "api")).Env();

        env.ShouldNotBeNull();
        ((string?)env["ASPNETCORE_ENVIRONMENT"]).ShouldBe("Production");
    }

    [Fact]
    public async Task Env_IsLeftOut_WhenThereAreNoVariables()
        => (await JobAsync(Fixtures.SingleService("worker"), Fixtures.NomadConfig())).Env().ShouldBeNull();

    [Fact]
    public async Task Env_KeepsValuesExactly_WithoutHclEscaping()
    {
        var manifest = Fixtures.SingleService("api");
        manifest.Resources["api"].Env = new() { ["V"] = "a \"b\" ${c} %{d} \\e" };

        ((string?)(await JobAsync(manifest, Fixtures.NomadConfig())).Env()!["V"]).ShouldBe("a \"b\" ${c} %{d} \\e");
    }

    [Fact]
    public async Task RegistryPassword_IsAPlaceholderOnDisk()
    {
        var config = Fixtures.NomadConfig();
        config.ContainerRegistry = "reg.io/myapp";
        config.TargetConfig["nomad"] = JsonSerializer.SerializeToElement(
            new NomadTargetConfig { RegistryUsername = "bob", RegistryPassword = "s3cr3t-\"pw" },
            TargetConfigJsonOptions.Default);

        var job = await JobAsync(Fixtures.SingleService("api"), config);

        var auth = job.Config()["auth"]!.AsArray().Single()!;
        ((string?)auth["username"]).ShouldBe("bob");
        ((string?)auth["password"]).ShouldBe(NomadJobFile.RegistryPasswordPlaceholder);
        ((string?)auth["server_address"]).ShouldBe("https://reg.io");
        job.Text.ShouldNotContain("s3cr3t");
    }

    [Fact]
    public async Task RegistryPassword_IsSwappedIn_WhenTheFileIsRead()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        config.ContainerRegistry = "reg.io/myapp";
        config.TargetConfig["nomad"] = JsonSerializer.SerializeToElement(
            new NomadTargetConfig { RegistryUsername = "bob", RegistryPassword = "s3cr3t-\"pw" },
            TargetConfigJsonOptions.Default);
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("api"), config, TestContext.Current.CancellationToken);
        var path = await _sut.WriteJobAsync(jobs[0], config, dir.Path, TestContext.Current.CancellationToken);

        var (id, job) = await NomadJobFile.ReadAsync(path, "s3cr3t-\"pw", TestContext.Current.CancellationToken);

        id.ShouldBe("api");
        ((string?)job["TaskGroups"]![0]!["Tasks"]![0]!["Config"]!["auth"]![0]!["password"]).ShouldBe("s3cr3t-\"pw");
        job.ToJsonString().ShouldNotContain(NomadJobFile.RegistryPasswordPlaceholder);
    }

    [Fact]
    public async Task Image_HasTheTag()
    {
        var config = Fixtures.NomadConfig();
        config.ContainerRegistry = "reg.io/myapp";
        config.ImageTag = "v2";

        ((string?)(await JobAsync(Fixtures.SingleService("api"), config)).Config()["image"]).ShouldBe("reg.io/myapp/api:v2");
    }

    [Fact]
    public async Task Type_IsService()
        => ((string?)(await JobAsync(Fixtures.SingleService(), Fixtures.NomadConfig())).Job["Type"]).ShouldBe("service");

    [Fact]
    public async Task Datacenters_ComeFromConfig()
        => NomadJobJson.Strings((await JobAsync(Fixtures.SingleService(), Fixtures.NomadConfig(dc: "eu-west-1"))).Job["Datacenters"])
            .ShouldBe(["eu-west-1"]);

    [Fact]
    public async Task Namespace_ComesFromConfig()
        => ((string?)(await JobAsync(Fixtures.SingleService(), Fixtures.NomadConfig(ns: "staging"))).Job["Namespace"]).ShouldBe("staging");

    [Fact]
    public async Task Durations_AreNanoseconds()
    {
        var job = await JobAsync(Fixtures.SingleService(), Fixtures.NomadConfig());

        ((long?)job.Group["Update"]!["MinHealthyTime"]).ShouldBe(NomadJobJson.Seconds(30));
        ((long?)job.Group["Update"]!["HealthyDeadline"]).ShouldBe(NomadJobJson.Seconds(600));
        ((long?)job.Group["Update"]!["ProgressDeadline"]).ShouldBe(NomadJobJson.Seconds(900));
        ((long?)job.Check!["Interval"]).ShouldBe(NomadJobJson.Seconds(10));
        ((long?)job.Check["Timeout"]).ShouldBe(NomadJobJson.Seconds(2));
        ((long?)job.Check["CheckRestart"]!["Grace"]).ShouldBe(NomadJobJson.Seconds(30));
    }
}
