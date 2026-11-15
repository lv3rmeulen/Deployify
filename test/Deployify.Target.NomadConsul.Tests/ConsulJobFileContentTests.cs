using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.NomadConsul.Tests;

/// <summary>What the generated Consul Connect job file says (the job API's JSON).</summary>
public sealed class ConsulJobFileContentTests
{
    private readonly ConsulJobGenerator _sut = new(NullLogger<ConsulJobGenerator>.Instance);

    private static System.Threading.CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<NomadJobJson> JobAsync(
        AspireManifest manifest, string? name = null, NomadConsulTargetConfig? cfg = null, EnvironmentConfig? config = null)
    {
        using var dir = new Fixtures.TempDir();
        cfg ??= Fixtures.DefaultConsulCfg();
        config ??= Fixtures.EnvConfig();
        var jobs = await _sut.GenerateAsync(manifest, config, cfg, Ct);
        var job = name is null ? jobs[0] : jobs.Single(j => j.Name == name);
        return await NomadJobJson.ReadAsync(await _sut.WriteJobAsync(job, config, cfg, dir.Path, Ct));
    }

    private Task<NomadJobJson> SingleAsync(string service, NomadConsulTargetConfig? cfg = null, EnvironmentConfig? config = null)
        => JobAsync(Fixtures.SingleService(service), cfg: cfg, config: config);

    // ── Structure ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Job_IsNamedAfterTheService()
    {
        var job = await SingleAsync("myapp");

        ((string?)job.Job["ID"]).ShouldBe("myapp");
        ((string?)job.Group["Name"]).ShouldBe("myapp");
        ((string?)job.Task()["Name"]).ShouldBe("myapp");
    }

    [Fact]
    public async Task Type_IsService() => ((string?)(await SingleAsync("api")).Job["Type"]).ShouldBe("service");

    [Fact]
    public async Task Namespace_ComesFromConfig()
        => ((string?)(await SingleAsync("api", Fixtures.DefaultConsulCfg(ns: "staging"))).Job["Namespace"]).ShouldBe("staging");

    [Fact]
    public async Task Datacenters_ComeFromConfig()
        => NomadJobJson.Strings((await SingleAsync("api", Fixtures.DefaultConsulCfg(dc: "eu-west-1"))).Job["Datacenters"])
            .ShouldBe(["eu-west-1"]);

    [Fact]
    public async Task Driver_IsDocker() => ((string?)(await SingleAsync("api")).Task()["Driver"]).ShouldBe("docker");

    [Fact]
    public async Task Driver_IsPodman_WhenRuntimeIsPodman()
    {
        var config = Fixtures.EnvConfig();
        config.ContainerRuntime = ContainerRuntime.Podman;

        var job = await SingleAsync("api", config: config);

        job.Tasks.ShouldAllBe(t => (string?)t!["Driver"] == "podman");
    }

    [Fact]
    public async Task Image_HasTheTag()
        => ((string?)(await SingleAsync("api", config: Fixtures.EnvConfig(registry: "reg.io/app", tag: "v2"))).Config()["image"])
            .ShouldBe("reg.io/app/api:v2");

    [Fact]
    public async Task Count_ComesFromTheServiceOverride()
    {
        var config = Fixtures.EnvConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride { Count = 3 };

        ((int?)(await SingleAsync("myapp", config: config)).Group["Count"]).ShouldBe(3);
    }

    // ── Bridge networking ─────────────────────────────────────────────────────

    [Fact]
    public async Task Network_IsBridge() => ((string?)(await SingleAsync("api")).Network!["Mode"]).ShouldBe("bridge");

    [Fact]
    public async Task Port_MapsToTheContainerPort_WithoutAStaticHostPort()
    {
        var network = (await SingleAsync("api")).Network!;

        network.ContainsKey("ReservedPorts").ShouldBeFalse();
        var port = network["DynamicPorts"]!.AsArray().Single()!;
        ((string?)port["Label"]).ShouldBe("http");
        ((int?)port["To"]).ShouldBe(8080);
    }

    [Fact]
    public async Task Ports_AreListedInTheDriverConfig()
        => NomadJobJson.Strings((await SingleAsync("api")).Config()["ports"]).ShouldBe(["http"]);

    // ── Consul service + Connect ──────────────────────────────────────────────

    [Fact]
    public async Task Service_IsNamedAfterTheJob_AndRegistersTheContainerPort()
    {
        var service = (await SingleAsync("myapi")).Service!;

        ((string?)service["Name"]).ShouldBe("myapi");
        ((string?)service["PortLabel"]).ShouldBe("8080");
    }

    [Fact]
    public async Task Service_HasAConnectSidecar()
        => (await SingleAsync("api")).Service!["Connect"]!["SidecarService"].ShouldNotBeNull();

    [Fact]
    public async Task TcpCheck_UsesThePortLabel()
    {
        var check = (await SingleAsync("api")).Check!;

        ((string?)check["Type"]).ShouldBe("tcp");
        ((string?)check["PortLabel"]).ShouldBe("http");
        check.ContainsKey("Expose").ShouldBeFalse();
    }

    [Fact]
    public async Task HttpCheck_IsExposedThroughTheSidecar()
    {
        var cfg = Fixtures.DefaultConsulCfg();
        cfg.HealthCheckType = "http";
        cfg.HealthCheckPath = "/healthz";

        var check = (await SingleAsync("api", cfg)).Check!;

        ((string?)check["Type"]).ShouldBe("http");
        ((string?)check["Path"]).ShouldBe("/healthz");
        ((bool?)check["Expose"]).ShouldBe(true);
        check.ContainsKey("PortLabel").ShouldBeFalse();
    }

    // ── Upstreams ─────────────────────────────────────────────────────────────

    private static string[] UpstreamNames(NomadJobJson job)
        => [.. (job.Service?["Connect"]?["SidecarService"]?["Proxy"]?["Upstreams"]?.AsArray() ?? [])
            .Select(u => (string)u!["DestinationName"]!)];

    [Fact]
    public async Task Upstream_PerDependency_OnTheNextLoopbackPort()
    {
        var worker = await JobAsync(Fixtures.SimpleManifest(), "worker", Fixtures.DefaultConsulCfg(upstreamBase: 20000));

        var upstream = worker.Service!["Connect"]!["SidecarService"]!["Proxy"]!["Upstreams"]!.AsArray().Single()!;
        ((string?)upstream["DestinationName"]).ShouldBe("api");
        ((int?)upstream["LocalBindPort"]).ShouldBe(20000);
    }

    [Fact]
    public async Task Upstreams_ForEveryDependency()
    {
        var api = await JobAsync(Fixtures.ManifestWithDependencyChain(), "api", Fixtures.DefaultConsulCfg(upstreamBase: 20000));

        var upstreams = api.Service!["Connect"]!["SidecarService"]!["Proxy"]!["Upstreams"]!.AsArray();
        upstreams.Select(u => ((string)u!["DestinationName"]!, (int)u["LocalBindPort"]!))
            .ShouldBe([("redis", 20000), ("db", 20001)], ignoreOrder: true);
    }

    [Fact]
    public async Task NoUpstreams_WhenNoDependencies() => UpstreamNames(await SingleAsync("api")).ShouldBeEmpty();

    [Fact]
    public async Task Service_WithUpstreamsButNoPorts_StillGetsASidecar_WithoutAPort()
    {
        // worker has no bindings but depends on api.
        var worker = await JobAsync(Fixtures.SimpleManifest(), "worker");

        var service = worker.Service!;
        service["Connect"]!["SidecarService"].ShouldNotBeNull();
        service.ContainsKey("PortLabel").ShouldBeFalse();
        service.ContainsKey("Checks").ShouldBeFalse();
    }

    [Fact]
    public async Task NoService_WhenNoPortsAndNoUpstreams()
    {
        var manifest = new AspireManifest
        {
            Resources = new Dictionary<string, AspireResource>
            {
                ["cache"] = new() { Type = "container.v0", Image = "redis:7" }
            }
        };

        (await JobAsync(manifest)).Group.ContainsKey("Services").ShouldBeFalse();
    }

    // ── Env vars ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Env_HoldsTheServicesVariables()
        => ((string?)(await JobAsync(Fixtures.SimpleManifest(), "api")).Env()!["ASPNETCORE_ENVIRONMENT"]).ShouldBe("Production");

    [Fact]
    public async Task Env_HasTheUpstreamAddress()
        => ((string?)(await JobAsync(Fixtures.SimpleManifest(), "worker", Fixtures.DefaultConsulCfg(upstreamBase: 20000)))
            .Env()!["NOMAD_UPSTREAM_ADDR_API"]).ShouldBe("localhost:20000");

    // ── Resources ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resources_HaveTheDefaults()
    {
        var resources = (await SingleAsync("api")).Task()["Resources"]!;

        ((int?)resources["CPU"]).ShouldBe(256);
        ((int?)resources["MemoryMB"]).ShouldBe(256);
    }

    [Fact]
    public async Task Resources_ComeFromConfig()
    {
        var cfg = Fixtures.DefaultConsulCfg();
        cfg.DefaultCpu = 512;
        cfg.DefaultMemory = 1024;

        var resources = (await SingleAsync("api", cfg)).Task()["Resources"]!;

        ((int?)resources["CPU"]).ShouldBe(512);
        ((int?)resources["MemoryMB"]).ShouldBe(1024);
    }

    // ── Ingress Gateway ───────────────────────────────────────────────────────

    private async Task<NomadJobJson?> GatewayAsync(NomadConsulTargetConfig cfg)
    {
        using var dir = new Fixtures.TempDir();
        var jobs = await _sut.GenerateAsync(
            Fixtures.SingleService("api", "project.v0", 8080, "http", external: true), Fixtures.EnvConfig(), cfg, Ct);
        var gw = jobs.SingleOrDefault(j => j.IsIngressGateway);
        return gw is null ? null : await NomadJobJson.ReadAsync(await _sut.WriteJobAsync(gw, Fixtures.EnvConfig(), cfg, dir.Path, Ct));
    }

    [Fact]
    public async Task IngressGateway_HasAListenerPerExposedService_WhenEnabled()
    {
        var gw = (await GatewayAsync(Fixtures.DefaultConsulCfg(ingress: true))).ShouldNotBeNull();

        var listener = gw.Service!["Connect"]!["Gateway"]!["Ingress"]!["Listeners"]!.AsArray().Single()!;
        ((int?)listener["Port"]).ShouldBe(8080);
        ((string?)listener["Protocol"]).ShouldBe("tcp");
        ((string?)listener["Services"]![0]!["Name"]).ShouldBe("api");
        var port = gw.Network!["ReservedPorts"]!.AsArray().Single()!;
        ((string?)port["Label"]).ShouldBe("inbound_api");
        ((int?)port["Value"]).ShouldBe(8080);
        ((int?)port["To"]).ShouldBe(8080);
        gw.Group.ContainsKey("Tasks").ShouldBeFalse();
    }

    [Fact]
    public async Task IngressGateway_IsNotGenerated_WhenDisabled()
        => (await GatewayAsync(Fixtures.DefaultConsulCfg(ingress: false))).ShouldBeNull();

    [Fact]
    public async Task IngressGateway_TakesTheConfiguredName()
    {
        var cfg = Fixtures.DefaultConsulCfg(ingress: true);
        cfg.IngressGatewayName = "my-igw";

        ((string?)(await GatewayAsync(cfg))!.Job["ID"]).ShouldBe("my-igw");
    }

    // ── File output ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteJob_CreatesFile_WithJobName()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("myservice"), Fixtures.EnvConfig(), cfg, Ct);

        var path = await _sut.WriteJobAsync(jobs[0], Fixtures.EnvConfig(), cfg, dir.Path, Ct);

        File.Exists(path).ShouldBeTrue();
        path.ShouldEndWith("myservice.nomad.json");
        jobs[0].JobFilePath.ShouldBe(path);
    }

    [Fact]
    public async Task WriteJob_GeneratesAllServices()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var config = Fixtures.EnvConfig();
        foreach (var job in await _sut.GenerateAsync(Fixtures.SimpleManifest(), config, cfg, Ct))
        {
            await _sut.WriteJobAsync(job, config, cfg, dir.Path, Ct);
        }

        Directory.GetFiles(dir.Path, "*.nomad.json").Length.ShouldBe(3);
    }

    [Fact]
    public async Task WriteJob_IsIdempotent()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var config = Fixtures.EnvConfig();
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("api"), config, cfg, Ct);

        var path1 = await _sut.WriteJobAsync(jobs[0], config, cfg, dir.Path, Ct);
        var first = await File.ReadAllTextAsync(path1, Ct);
        var path2 = await _sut.WriteJobAsync(jobs[0], config, cfg, dir.Path, Ct);

        path1.ShouldBe(path2);
        (await File.ReadAllTextAsync(path2, Ct)).ShouldBe(first);
        Directory.GetFiles(dir.Path).Length.ShouldBe(1);
    }
}
