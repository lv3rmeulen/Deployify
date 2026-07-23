using Deployify.Core.Models;
using Shouldly;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Deployify.Target.NomadConsul.Tests;

public sealed class ConsulJobGeneratorModelTests
{
    private readonly ConsulJobGenerator _sut = new(NullLogger<ConsulJobGenerator>.Instance);

    // ── Filtering ─────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_SkipsParameterAndValueTypes()
    {
        var jobs = _sut.Generate(
            Fixtures.ManifestWithIgnoredTypes(),
            Fixtures.EnvConfig(),
            Fixtures.DefaultConsulCfg());

        jobs.ShouldHaveSingleItem().Name.ShouldBe("api");
    }

    // ── Image resolution ──────────────────────────────────────────────────────

    [Fact]
    public void Generate_SetsImage_FromRegistry_ForProjects()
    {
        var config = Fixtures.EnvConfig(registry: "reg.io/myapp");
        var jobs = _sut.Generate(Fixtures.SingleService("api"), config, Fixtures.DefaultConsulCfg());
        jobs[0].Image.ShouldBe("reg.io/myapp/api");
    }

    [Fact]
    public void Generate_SetsImage_Directly_ForContainers()
    {
        var jobs = _sut.Generate(
            Fixtures.SingleService("redis", "container.v0"),
            Fixtures.EnvConfig(),
            Fixtures.DefaultConsulCfg());
        jobs[0].Image.ShouldBe("nginx:latest");
    }

    [Fact]
    public void Generate_UsesServiceName_WhenNoRegistry()
    {
        var config = Fixtures.EnvConfig(registry: "");
        config.ContainerRegistry = null;
        var jobs = _sut.Generate(Fixtures.SingleService("api"), config, Fixtures.DefaultConsulCfg());
        jobs[0].Image.ShouldBe("api");
    }

    // ── ImageTag ──────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_SetsImageTag_FromConfig()
    {
        var config = Fixtures.EnvConfig(tag: "sha-abc123");
        var jobs = _sut.Generate(Fixtures.SingleService(), config, Fixtures.DefaultConsulCfg());
        jobs[0].ImageTag.ShouldBe("sha-abc123");
    }

    [Fact]
    public void Generate_RespectsServiceOverride_ImageTag()
    {
        var config = Fixtures.EnvConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride { ImageTag = "canary" };
        var jobs = _sut.Generate(Fixtures.SingleService("myapp"), config, Fixtures.DefaultConsulCfg());
        jobs[0].ImageTag.ShouldBe("canary");
    }

    // ── Count ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_DefaultCount_IsOne()
    {
        var jobs = _sut.Generate(Fixtures.SingleService(), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg());
        jobs[0].Count.ShouldBe(1);
    }

    [Fact]
    public void Generate_RespectsServiceOverride_Count()
    {
        var config = Fixtures.EnvConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride { Count = 4 };
        var jobs = _sut.Generate(Fixtures.SingleService("myapp"), config, Fixtures.DefaultConsulCfg());
        jobs[0].Count.ShouldBe(4);
    }

    // ── Env vars ──────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_MergesEnvVars_FromManifest()
    {
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg());
        jobs.Single(j => j.Name == "api").Env.ShouldContainKey("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public void Generate_MergesEnvVars_FromServiceOverride()
    {
        var config = Fixtures.EnvConfig();
        config.ServiceOverrides["api"] = new ServiceOverride { Env = { ["MY_VAR"] = "hello" } };
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), config, Fixtures.DefaultConsulCfg());
        jobs.Single(j => j.Name == "api").Env.ShouldContainKey("MY_VAR");
    }

    // ── Ports ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_PopulatsPorts_FromBindings()
    {
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg());
        jobs.Single(j => j.Name == "api").Ports.ShouldContain(p => p.ContainerPort == 8080);
    }

    [Fact]
    public void Generate_SetsPortProtocol()
    {
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg());
        jobs.Single(j => j.Name == "api").Ports[0].Protocol.ShouldBe("http");
    }

    // ── Dependencies & upstreams ───────────────────────────────────────────────

    [Fact]
    public void Generate_PopulatesDependencies_FromReferences()
    {
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg());
        jobs.Single(j => j.Name == "worker").Dependencies.ShouldContain("api");
        // single item implied;
    }

    [Fact]
    public void Generate_ResolvesUpstreams_ForEachDependency()
    {
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg());
        var worker = jobs.Single(j => j.Name == "worker");
        worker.Upstreams.ShouldContain(u => u.DestinationName == "api");
    }

    [Fact]
    public void Generate_AssignsUpstreamBasePort_ToFirstDependency()
    {
        var cfg = Fixtures.DefaultConsulCfg(upstreamBase: 20000);
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), cfg);
        var worker = jobs.Single(j => j.Name == "worker");
        worker.Upstreams[0].LocalBindPort.ShouldBe(20000);
    }

    [Fact]
    public void Generate_AssignsIncrementingPorts_ForMultipleDependencies()
    {
        var cfg = Fixtures.DefaultConsulCfg(upstreamBase: 20000);
        var jobs = _sut.Generate(Fixtures.ManifestWithDependencyChain(), Fixtures.EnvConfig(), cfg);
        var api = jobs.Single(j => j.Name == "api");
        var ports = api.Upstreams.Select(u => u.LocalBindPort).ToList();
        ports.ShouldBe([20000, 20001], ignoreOrder: true);
        ports.ShouldBeUnique();
    }

    [Fact]
    public void Generate_InjectsUpstreamEnvVar_ForEachDependency()
    {
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg());
        var worker = jobs.Single(j => j.Name == "worker");
        worker.Env.ShouldContainKey("NOMAD_UPSTREAM_ADDR_API");
        worker.Env["NOMAD_UPSTREAM_ADDR_API"].ShouldStartWith("localhost:");
    }

    [Fact]
    public void Generate_UpstreamEnvVar_ContainsCorrectPort()
    {
        var cfg = Fixtures.DefaultConsulCfg(upstreamBase: 21000);
        var jobs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.EnvConfig(), cfg);
        var worker = jobs.Single(j => j.Name == "worker");
        worker.Env["NOMAD_UPSTREAM_ADDR_API"].ShouldBe("localhost:21000");
    }

    [Fact]
    public void Generate_UpstreamEnvVar_NormalisesHyphens()
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
        var jobs = _sut.Generate(manifest, Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg());
        var caller = jobs.Single(j => j.Name == "caller");
        caller.Env.ShouldContainKey("NOMAD_UPSTREAM_ADDR_MY_SERVICE");
    }

    [Fact]
    public void Generate_NoUpstreams_WhenNoDependencies()
    {
        var jobs = _sut.Generate(Fixtures.SingleService("api"), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg());
        jobs[0].Upstreams.ShouldBeEmpty();
    }

    // ── IsProjectService ──────────────────────────────────────────────────────

    [Fact]
    public void Generate_SetsIsProjectService_True_ForProjects()
    {
        var jobs = _sut.Generate(Fixtures.SingleService("api", "project.v0"), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg());
        jobs[0].IsProjectService.ShouldBeTrue();
    }

    [Fact]
    public void Generate_SetsIsProjectService_False_ForContainers()
    {
        var jobs = _sut.Generate(Fixtures.SingleService("redis", "container.v0"), Fixtures.EnvConfig(), Fixtures.DefaultConsulCfg());
        jobs[0].IsProjectService.ShouldBeFalse();
    }

    // ── Ingress Gateway ───────────────────────────────────────────────────────

    [Fact]
    public void Generate_ExposeViaIngressGateway_False_WhenDisabled()
    {
        var cfg = Fixtures.DefaultConsulCfg(ingress: false);
        var jobs = _sut.Generate(Fixtures.SingleService("api", "project.v0"), Fixtures.EnvConfig(), cfg);
        jobs[0].ExposeViaIngressGateway.ShouldBeFalse();
    }

    [Fact]
    public void Generate_ExposeViaIngressGateway_True_WhenEnabled_AndHasHttpBinding()
    {
        var cfg = Fixtures.DefaultConsulCfg(ingress: true);
        var jobs = _sut.Generate(
            Fixtures.SingleService("api", "project.v0", 8080, "http"),
            Fixtures.EnvConfig(),
            cfg);
        jobs[0].ExposeViaIngressGateway.ShouldBeTrue();
    }

    [Fact]
    public void Generate_ExposeViaIngressGateway_False_ForContainerWithTcp_EvenWhenEnabled()
    {
        var cfg = Fixtures.DefaultConsulCfg(ingress: true);
        var jobs = _sut.Generate(
            Fixtures.SingleService("redis", "container.v0", 6379, "tcp"),
            Fixtures.EnvConfig(),
            cfg);
        jobs[0].ExposeViaIngressGateway.ShouldBeFalse();
    }
}
