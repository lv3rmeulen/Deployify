using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.NomadConsul.Tests;

public sealed class ConsulJobGeneratorModelTests
{
    private readonly ConsulJobGenerator _sut = new(NullLogger<ConsulJobGenerator>.Instance);

    // ── Filtering ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generate_SkipsParameterAndValueTypes()
    {
        var jobs = await _sut.GenerateAsync(
            Fixtures.ManifestWithIgnoredTypes(),
            Fixtures.EnvConfig(),
            Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);

        jobs.ShouldHaveSingleItem().Name.ShouldBe("api");
    }

    // ── Image resolution ──────────────────────────────────────────────────────

    [Fact]
    public async Task Generate_SetsImage_FromRegistry_ForProjects()
    {
        var config = Fixtures.EnvConfig(registry: "reg.io/myapp");
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("api"), config, Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs[0].Image.ShouldBe("reg.io/myapp/api");
    }

    [Fact]
    public async Task Generate_SetsImage_Directly_ForContainers()
    {
        var jobs = await _sut.GenerateAsync(
            Fixtures.SingleService("redis", "container.v0"),
            Fixtures.EnvConfig(),
            Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs[0].Image.ShouldBe("nginx:latest");
    }

    [Fact]
    public async Task Generate_UsesServiceName_WhenNoRegistry()
    {
        var config = Fixtures.EnvConfig(registry: "");
        config.ContainerRegistry = null;
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("api"), config, Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs[0].Image.ShouldBe("api");
    }

    // ── ImageTag ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generate_SetsImageTag_FromConfig()
    {
        var config = Fixtures.EnvConfig(tag: "sha-abc123");
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService(), config, Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs[0].ImageTag.ShouldBe("sha-abc123");
    }

    [Fact]
    public async Task Generate_RespectsServiceOverride_ImageTag()
    {
        var config = Fixtures.EnvConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride { ImageTag = "canary" };
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("myapp"), config, Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs[0].ImageTag.ShouldBe("canary");
    }

    // ── Count ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generate_DefaultCount_IsOne()
    {
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService(), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs[0].Count.ShouldBe(1);
    }

    [Fact]
    public async Task Generate_RespectsServiceOverride_Count()
    {
        var config = Fixtures.EnvConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride { Count = 4 };
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("myapp"), config, Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs[0].Count.ShouldBe(4);
    }

    // ── Env vars ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generate_MergesEnvVars_FromManifest()
    {
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs.Single(j => j.Name == "api").Env.ShouldContainKey("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public async Task Generate_MergesEnvVars_FromServiceOverride()
    {
        var config = Fixtures.EnvConfig();
        config.ServiceOverrides["api"] = new ServiceOverride { Env = { ["MY_VAR"] = "hello" } };
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), config, Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs.Single(j => j.Name == "api").Env.ShouldContainKey("MY_VAR");
    }

    // ── Ports ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generate_PopulatsPorts_FromBindings()
    {
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs.Single(j => j.Name == "api").Ports.ShouldContain(p => p.ContainerPort == 8080);
    }

    [Fact]
    public async Task Generate_SetsPortProtocol()
    {
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs.Single(j => j.Name == "api").Ports[0].Protocol.ShouldBe("http");
    }

    // ── Dependencies & upstreams ───────────────────────────────────────────────

    [Fact]
    public async Task Generate_PopulatesDependencies_FromReferences()
    {
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs.Single(j => j.Name == "worker").Dependencies.ShouldContain("api");
        // single item implied;
    }

    [Fact]
    public async Task Generate_ResolvesUpstreams_ForEachDependency()
    {
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        var worker = jobs.Single(j => j.Name == "worker");
        worker.Upstreams.ShouldContain(u => u.DestinationName == "api");
    }

    [Fact]
    public async Task Generate_AssignsUpstreamBasePort_ToFirstDependency()
    {
        var cfg = Fixtures.DefaultConsulCfg(upstreamBase: 20000);
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var worker = jobs.Single(j => j.Name == "worker");
        worker.Upstreams[0].LocalBindPort.ShouldBe(20000);
    }

    [Fact]
    public async Task Generate_AssignsIncrementingPorts_ForMultipleDependencies()
    {
        var cfg = Fixtures.DefaultConsulCfg(upstreamBase: 20000);
        var jobs = await _sut.GenerateAsync(Fixtures.ManifestWithDependencyChain(), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var api = jobs.Single(j => j.Name == "api");
        var ports = api.Upstreams.Select(u => u.LocalBindPort).ToList();
        ports.ShouldBe([20000, 20001], ignoreOrder: true);
        ports.ShouldBeUnique();
    }

    [Fact]
    public async Task Generate_InjectsUpstreamEnvVar_ForEachDependency()
    {
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        var worker = jobs.Single(j => j.Name == "worker");
        worker.Env.ShouldContainKey("NOMAD_UPSTREAM_ADDR_API");
        worker.Env["NOMAD_UPSTREAM_ADDR_API"].ShouldStartWith("localhost:");
    }

    [Fact]
    public async Task Generate_UpstreamEnvVar_ContainsCorrectPort()
    {
        var cfg = Fixtures.DefaultConsulCfg(upstreamBase: 21000);
        var jobs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var worker = jobs.Single(j => j.Name == "worker");
        worker.Env["NOMAD_UPSTREAM_ADDR_API"].ShouldBe("localhost:21000");
    }

    [Fact]
    public async Task Generate_UpstreamEnvVar_NormalizesHyphens()
    {
        var manifest = new AspireManifest
        {
            Resources = new Dictionary<string, AspireResource>
            {
                ["my-service"] = new()
                {
                    Type = "project.v0",
                    Bindings = new Dictionary<string, AspireBinding> { ["http"] = new() { ContainerPort = 8080, Protocol = "http" } }
                },
                ["caller"] = new()
                {
                    Type = "project.v0",
                    References = [new AspireReference { Name = "my-service" }]
                }
            }
        };
        var jobs = await _sut.GenerateAsync(manifest, Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        var caller = jobs.Single(j => j.Name == "caller");
        caller.Env.ShouldContainKey("NOMAD_UPSTREAM_ADDR_MY_SERVICE");
    }

    [Fact]
    public async Task Generate_NoUpstreams_WhenNoDependencies()
    {
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("api"), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs[0].Upstreams.ShouldBeEmpty();
    }

    // ── IsProjectService ──────────────────────────────────────────────────────

    [Fact]
    public async Task Generate_SetsIsProjectService_True_ForProjects()
    {
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("api", "project.v0"), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs[0].IsProjectService.ShouldBeTrue();
    }

    [Fact]
    public async Task Generate_SetsIsProjectService_False_ForContainers()
    {
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("redis", "container.v0"), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg(), TestContext.Current.CancellationToken);
        jobs[0].IsProjectService.ShouldBeFalse();
    }

    // ── Ingress Gateway ───────────────────────────────────────────────────────

    [Fact]
    public async Task Generate_ExposeViaIngressGateway_False_WhenDisabled()
    {
        var cfg = Fixtures.DefaultConsulCfg(ingress: false);
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("api", "project.v0"), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        jobs[0].ExposeViaIngressGateway.ShouldBeFalse();
    }

    [Fact]
    public async Task Generate_ExposeViaIngressGateway_True_WhenEnabled_AndHasExternalHttpBinding()
    {
        var cfg = Fixtures.DefaultConsulCfg(ingress: true);
        var jobs = await _sut.GenerateAsync(
            Fixtures.SingleService("api", "project.v0", 8080, "http", external: true),
            Fixtures.EnvConfig(),
            cfg, TestContext.Current.CancellationToken);
        jobs[0].ExposeViaIngressGateway.ShouldBeTrue();
    }

    [Fact]
    public async Task Generate_ExposeViaIngressGateway_False_ForInternalHttpBinding()
    {
        var cfg = Fixtures.DefaultConsulCfg(ingress: true);
        var jobs = await _sut.GenerateAsync(Fixtures.SingleService("api", "project.v0", 8080, "http"), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        jobs.ShouldHaveSingleItem().ExposeViaIngressGateway.ShouldBeFalse();
    }

    [Fact]
    public async Task Generate_AddsSingleIngressGatewayJob_NamedFromConfig()
    {
        var cfg = Fixtures.DefaultConsulCfg(ingress: true);
        cfg.IngressGatewayName = "my-igw";
        var jobs = await _sut.GenerateAsync(
            Fixtures.SingleService("api", "project.v0", 8080, "http", external: true), Fixtures.EnvConfig(), cfg, TestContext.Current.CancellationToken);
        var gw = jobs.Single(j => j.IsIngressGateway);
        gw.Name.ShouldBe("my-igw");
        gw.IngressListeners.ShouldHaveSingleItem().ServiceName.ShouldBe("api");
    }

    [Fact]
    public async Task Generate_ExposeViaIngressGateway_False_ForContainerWithTcp_EvenWhenEnabled()
    {
        var cfg = Fixtures.DefaultConsulCfg(ingress: true);
        var jobs = await _sut.GenerateAsync(
            Fixtures.SingleService("redis", "container.v0", 6379, "tcp"),
            Fixtures.EnvConfig(),
            cfg, TestContext.Current.CancellationToken);
        jobs[0].ExposeViaIngressGateway.ShouldBeFalse();
    }
}
