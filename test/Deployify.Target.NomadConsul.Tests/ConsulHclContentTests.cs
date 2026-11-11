using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.NomadConsul.Tests;

public sealed class ConsulHclContentTests
{
    private readonly ConsulJobGenerator _sut = new(NullLogger<ConsulJobGenerator>.Instance);

    private async Task<string> HclForAsync(
        string serviceName,
        NomadConsulTargetConfig? consulCfg = null,
        CancellationToken ct = default)
    {
        using var dir = new Fixtures.TempDir();
        var cfg = consulCfg ?? Fixtures.DefaultConsulCfg();
        var jobs = await _sut.GenerateAsync(
            Fixtures.SingleService(serviceName),
            Fixtures.EnvConfig(),
            cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteHclAsync(jobs[0], Fixtures.EnvConfig(), cfg, dir.Path, ct);
        return await File.ReadAllTextAsync(path, ct);
    }

    // ── Structure ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hcl_StartsWithJobBlock()
    {
        var hcl = await HclForAsync("myapp", ct: TestContext.Current.CancellationToken);
        hcl.TrimStart().ShouldStartWith("job");
    }

    [Fact]
    public async Task Hcl_ContainsJobName()
    {
        var hcl = await HclForAsync("myapp", ct: TestContext.Current.CancellationToken);
        hcl.ShouldContain("\"myapp\"");
    }

    [Fact]
    public async Task Hcl_ContainsTypeService()
    {
        var hcl = await HclForAsync("api", ct: TestContext.Current.CancellationToken);
        hcl.ShouldContain("\"service\"");
    }

    [Fact]
    public async Task Hcl_ContainsNamespace()
    {
        var cfg = Fixtures.DefaultConsulCfg(ns: "staging");
        var hcl = await HclForAsync("api", cfg, TestContext.Current.CancellationToken);
        hcl.ShouldContain("\"staging\"");
    }

    [Fact]
    public async Task Hcl_ContainsDatacenter()
    {
        var cfg = Fixtures.DefaultConsulCfg(dc: "eu-west-1");
        var hcl = await HclForAsync("api", cfg, TestContext.Current.CancellationToken);
        hcl.ShouldContain("eu-west-1");
    }

    [Fact]
    public async Task Hcl_ContainsDockerDriver()
    {
        var hcl = await HclForAsync("api", ct: TestContext.Current.CancellationToken);
        hcl.ShouldContain("\"docker\"");
    }

    [Fact]
    public async Task Hcl_ContainsPodmanDriver_WhenRuntimeIsPodman()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var config = Fixtures.EnvConfig();
        config.ContainerRuntime = ContainerRuntime.Podman;
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("api"), config, cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteHclAsync(jobs[0], config, cfg, dir.Path, TestContext.Current.CancellationToken);
        var hcl = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        hcl.ShouldContain("\"podman\"");
        hcl.ShouldNotContain("\"docker\"");
    }

    [Fact]
    public async Task Hcl_ContainsImageWithTag()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var config = Fixtures.EnvConfig(registry: "reg.io/app", tag: "v2");
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("api"), config, cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteHclAsync(jobs[0], config, cfg, dir.Path, TestContext.Current.CancellationToken);
        var hcl = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        hcl.ShouldContain("reg.io/app/api:v2");
    }

    [Fact]
    public async Task Hcl_ContainsCount()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var config = Fixtures.EnvConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride { Count = 3 };
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("myapp"), config, cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteHclAsync(jobs[0], config, cfg, dir.Path, TestContext.Current.CancellationToken);
        var hcl = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        hcl.ShouldContain("count = 3");
    }

    // ── Bridge networking ─────────────────────────────────────────────────────

    [Fact]
    public async Task Hcl_NetworkMode_IsBridge()
    {
        var hcl = await HclForAsync("api", ct: TestContext.Current.CancellationToken);
        hcl.ShouldContain("mode = \"bridge\"");
    }

    [Fact]
    public async Task Hcl_DoesNotContainHostMode()
    {
        var hcl = await HclForAsync("api", ct: TestContext.Current.CancellationToken);
        hcl.ShouldNotContain("mode = \"host\"");
    }

    [Fact]
    public async Task Hcl_ContainsPortBlock_WithContainerPort()
    {
        var hcl = await HclForAsync("api", ct: TestContext.Current.CancellationToken);
        hcl.ShouldContain("to = 8080");
    }

    [Fact]
    public async Task Hcl_ContainsPortLabel()
    {
        var hcl = await HclForAsync("api", ct: TestContext.Current.CancellationToken);
        hcl.ShouldContain("\"http\"");
    }

    // ── Consul service + Connect ───────────────────────────────────────────────

    [Fact]
    public async Task Hcl_ContainsServiceBlock()
    {
        var hcl = await HclForAsync("api", ct: TestContext.Current.CancellationToken);
        hcl.ShouldContain("service {");
    }

    [Fact]
    public async Task Hcl_ContainsServiceName()
    {
        var hcl = await HclForAsync("myapi", ct: TestContext.Current.CancellationToken);
        hcl.ShouldContain("name = \"myapi\"");
    }

    [Fact]
    public async Task Hcl_ContainsConnectBlock()
    {
        var hcl = await HclForAsync("api", ct: TestContext.Current.CancellationToken);
        hcl.ShouldContain("connect {");
    }

    [Fact]
    public async Task Hcl_ContainsSidecarService()
    {
        var hcl = await HclForAsync("api", ct: TestContext.Current.CancellationToken);
        hcl.ShouldContain("sidecar_service {");
    }

    // ── Upstreams ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hcl_ContainsUpstreamBlock_WhenDependencyExists()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var worker = jobs.Single(j => j.Name == "worker");
        var path = await _sut.WriteHclAsync(worker, Fixtures.EnvConfig(), cfg, dir.Path, TestContext.Current.CancellationToken);
        var hcl = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        hcl.ShouldContain("upstreams {");
    }

    [Fact]
    public async Task Hcl_ContainsDestinationName_ForEachDependency()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var worker = jobs.Single(j => j.Name == "worker");
        var path = await _sut.WriteHclAsync(worker, Fixtures.EnvConfig(), cfg, dir.Path, TestContext.Current.CancellationToken);
        var hcl = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        hcl.ShouldContain("destination_name = \"api\"");
    }

    [Fact]
    public async Task Hcl_ContainsLocalBindPort_ForUpstream()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg(upstreamBase: 20000);
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var worker = jobs.Single(j => j.Name == "worker");
        var path = await _sut.WriteHclAsync(worker, Fixtures.EnvConfig(), cfg, dir.Path, TestContext.Current.CancellationToken);
        var hcl = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        hcl.ShouldContain("local_bind_port = 20000");
    }

    [Fact]
    public async Task Hcl_ContainsMultipleUpstreams_ForMultipleDependencies()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg(upstreamBase: 20000);
        var jobs = await _sut.GenerateAsync(Fixtures.ManifestWithDependencyChain(), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var api = jobs.Single(j => j.Name == "api");
        var path = await _sut.WriteHclAsync(api, Fixtures.EnvConfig(), cfg, dir.Path, TestContext.Current.CancellationToken);
        var hcl = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        hcl.ShouldContain("destination_name = \"redis\"");
        hcl.ShouldContain("destination_name = \"db\"");
        hcl.ShouldContain("local_bind_port = 20000");
        hcl.ShouldContain("local_bind_port = 20001");
    }

    [Fact]
    public async Task Hcl_NoUpstreamBlock_WhenNoDependencies()
    {
        var hcl = await HclForAsync("api", ct: TestContext.Current.CancellationToken);
        hcl.ShouldNotContain("upstreams {");
    }

    [Fact]
    public async Task Hcl_ContainsServiceBlock_WhenNoPorts_ButHasUpstreams()
    {
        // worker has no bindings but depends on api — should still get a service+connect block
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var worker = jobs.Single(j => j.Name == "worker");
        var path = await _sut.WriteHclAsync(worker, Fixtures.EnvConfig(), cfg, dir.Path, TestContext.Current.CancellationToken);
        var hcl = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        hcl.ShouldContain("service {");
        hcl.ShouldContain("connect {");
        hcl.ShouldContain("sidecar_service {");
    }

    [Fact]
    public async Task Hcl_OmitsPortLine_WhenServiceHasNoBindings()
    {
        // worker has no bindings, so the service block should NOT contain a port = line
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var worker = jobs.Single(j => j.Name == "worker");
        var path = await _sut.WriteHclAsync(worker, Fixtures.EnvConfig(), cfg, dir.Path, TestContext.Current.CancellationToken);
        var hcl = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        hcl.ShouldNotContain("port = \"");
    }

    [Fact]
    public async Task Hcl_ContainsPortLine_WhenServiceHasBindings()
    {
        var hcl = await HclForAsync("api", ct: TestContext.Current.CancellationToken);
        hcl.ShouldContain("port = \"http\"");
    }

    [Fact]
    public async Task Hcl_NoServiceBlock_WhenNoPorts_AndNoUpstreams()
    {
        // A container with no bindings and no dependencies gets no service block at all
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var manifest = new AspireManifest
        {
            Resources = new Dictionary<string, AspireResource>
            {
                ["cache"] = new() { Type = "container.v0", Image = "redis:7" }
            }
        };
        var jobs = await _sut.GenerateAsync(manifest, Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteHclAsync(jobs[0], Fixtures.EnvConfig(), cfg, dir.Path, TestContext.Current.CancellationToken);
        var hcl = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        hcl.ShouldNotContain("service {");
        hcl.ShouldNotContain("connect {");
    }

    // ── Env vars ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hcl_ContainsEnvBlock_WhenEnvVarsPresent()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var api = jobs.Single(j => j.Name == "api");
        var path = await _sut.WriteHclAsync(api, Fixtures.EnvConfig(), cfg, dir.Path, TestContext.Current.CancellationToken);
        var hcl = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        hcl.ShouldContain("env = {");
        hcl.ShouldContain("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public async Task Hcl_ContainsUpstreamEnvVar()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg(upstreamBase: 20000);
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var worker = jobs.Single(j => j.Name == "worker");
        var path = await _sut.WriteHclAsync(worker, Fixtures.EnvConfig(), cfg, dir.Path, TestContext.Current.CancellationToken);
        var hcl = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        hcl.ShouldContain("NOMAD_UPSTREAM_ADDR_API");
        hcl.ShouldContain("localhost:20000");
    }

    // ── Resources ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hcl_ContainsResourceBlock_WithCpuAndMemory()
    {
        var hcl = await HclForAsync("api", ct: TestContext.Current.CancellationToken);
        hcl.ShouldContain("resources {");
        // TODO: also ShouldContain("cpu = 256");
        // TODO: also ShouldContain("memory = 256");
    }

    [Fact]
    public async Task Hcl_ResourcesCpu_ReflectsConfig()
    {
        var cfg = Fixtures.DefaultConsulCfg();
        cfg.DefaultCpu = 512;
        cfg.DefaultMemory = 1024;
        var hcl = await HclForAsync("api", cfg, TestContext.Current.CancellationToken);
        hcl.ShouldContain("cpu = 512");
        hcl.ShouldContain("memory = 1024");
    }

    // ── Ingress Gateway ───────────────────────────────────────────────────────

    private async Task<string> GatewayHclAsync(NomadConsulTargetConfig cfg, CancellationToken ct = default)
    {
        using var dir = new Fixtures.TempDir();
        var jobs = await _sut.GenerateAsync(
            Fixtures.SingleService("api", "project.v0", 8080, "http", external: true),
            Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var gw = jobs.Single(j => j.IsIngressGateway);
        var path = await _sut.WriteHclAsync(gw, Fixtures.EnvConfig(), cfg, dir.Path, ct);
        return await File.ReadAllTextAsync(path, ct);
    }

    [Fact]
    public async Task Hcl_ContainsIngressGatewayBlock_WhenEnabled()
    {
        var cfg = Fixtures.DefaultConsulCfg(ingress: true);
        var hcl = await GatewayHclAsync(cfg, TestContext.Current.CancellationToken);
        hcl.ShouldContain("gateway {");
        hcl.ShouldContain("ingress {");
    }

    [Fact]
    public async Task Hcl_NoIngressGatewayBlock_WhenDisabled()
    {
        var cfg = Fixtures.DefaultConsulCfg(ingress: false);
        var hcl = await HclForAsync("api", cfg, TestContext.Current.CancellationToken);
        hcl.ShouldNotContain("gateway {");
    }

    [Fact]
    public async Task Hcl_IngressGateway_ContainsGatewayName()
    {
        var cfg = Fixtures.DefaultConsulCfg(ingress: true);
        cfg.IngressGatewayName = "my-igw";
        var hcl = await GatewayHclAsync(cfg, TestContext.Current.CancellationToken);
        hcl.ShouldContain("\"my-igw\"");
    }

    [Fact]
    public async Task Hcl_IngressGateway_ContainsListenerPort()
    {
        var cfg = Fixtures.DefaultConsulCfg(ingress: true);
        var hcl = await GatewayHclAsync(cfg, TestContext.Current.CancellationToken);
        hcl.ShouldContain("port = 8080");
    }

    // ── File output ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteHcl_CreatesFile_WithJobName()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("myservice"), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteHclAsync(jobs[0], Fixtures.EnvConfig(), cfg, dir.Path, TestContext.Current.CancellationToken);
        File.Exists(path).ShouldBeTrue();
        path.ShouldEndWith("myservice.job.hcl");
    }

    [Fact]
    public async Task WriteHcl_GeneratesAllServices()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var config = Fixtures.EnvConfig();
        foreach (var job in await _sut.GenerateAsync(Fixtures.SimpleManifest(), config, cfg, TestContext.Current.CancellationToken))
        {
            await _sut.WriteHclAsync(job, config, cfg, dir.Path, TestContext.Current.CancellationToken);
        }

        Directory.GetFiles(dir.Path, "*.hcl").Length.ShouldBe(3);
    }

    [Fact]
    public async Task WriteHcl_IsIdempotent()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var config = Fixtures.EnvConfig();
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("api"), config, cfg, TestContext.Current.CancellationToken);
        var path1 = await _sut.WriteHclAsync(jobs[0], config, cfg, dir.Path, TestContext.Current.CancellationToken);
        var path2 = await _sut.WriteHclAsync(jobs[0], config, cfg, dir.Path, TestContext.Current.CancellationToken);
        path1.ShouldBe(path2);
        Directory.GetFiles(dir.Path, "*.hcl").Length.ShouldBe(1);
    }
}
