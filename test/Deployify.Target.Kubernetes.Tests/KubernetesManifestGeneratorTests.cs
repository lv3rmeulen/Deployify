using System.Linq;
using Shouldly;
using Xunit;

namespace Deployify.Target.Kubernetes.Tests;

public sealed class KubernetesManifestGeneratorTests
{
    private readonly KubernetesManifestGenerator _sut = new();

    private KubernetesTargetConfig DefaultKubernetesConfig(string prefix = "test") => new()
    {
        NamespacePrefix = prefix, DefaultReplicas = 1,
        CpuRequest = "100m", CpuLimit = "500m",
        MemoryRequest = "128Mi", MemoryLimit = "512Mi",
        GenerateIngress = false, IngressClassName = "nginx"
    };

    [Fact]
    public void Generate_SkipsParametersAndValues()
    {
        var services = _sut.Generate(Fixtures.ManifestWithIgnoredTypes(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig());
        services.ShouldHaveSingleItem().ServiceName.ShouldBe("api");
    }

    [Fact]
    public void Generate_AssignsNamespace_WithPrefix()
    {
        var services = _sut.Generate(Fixtures.SingleService("api"), Fixtures.KubernetesConfig("myapp"), DefaultKubernetesConfig("myapp"));
        services[0].Namespace.ShouldBe("myapp-api");
    }

    [Fact]
    public void Generate_SetsImage_FromRegistry_ForProjects()
    {
        var config = Fixtures.KubernetesConfig();
        config.ContainerRegistry = "registry.example.com/app";
        var services = _sut.Generate(Fixtures.SingleService("api"), config, DefaultKubernetesConfig());
        services[0].Image.ShouldBe("registry.example.com/app/api");
    }

    [Fact]
    public void Generate_SetsImageTag()
    {
        var config = Fixtures.KubernetesConfig();
        config.ImageTag = "v2.0.0";
        var services = _sut.Generate(Fixtures.SingleService(), config, DefaultKubernetesConfig());
        services[0].ImageTag.ShouldBe("v2.0.0");
    }

    [Fact]
    public void Generate_PopulatesPorts_FromBindings()
    {
        var services = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig());
        services.Single(s => s.ServiceName == "api").Ports.Count.ShouldBe(2);
    }

    [Fact]
    public void Generate_SetsHasService_WhenPortsPresent()
    {
        var services = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig());
        services.Single(s => s.ServiceName == "api").HasService.ShouldBeTrue();
    }

    [Fact]
    public void Generate_SetsHasConfigMap_WhenEnvVarsPresent()
    {
        var services = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig());
        services.Single(s => s.ServiceName == "api").HasConfigMap.ShouldBeTrue();
    }

    [Fact]
    public void Generate_NoConfigMap_WhenNoEnv()
    {
        var services = _sut.Generate(Fixtures.SingleService("worker", "project.v0"), Fixtures.KubernetesConfig(), DefaultKubernetesConfig());
        services[0].HasConfigMap.ShouldBeFalse();
    }

    [Fact]
    public void Generate_SetsIngress_WhenDomainConfigured()
    {
        var kubernetesCfg = DefaultKubernetesConfig();
        kubernetesCfg.GenerateIngress = true;
        kubernetesCfg.IngressBaseDomain = "example.com";
        var services = _sut.Generate(Fixtures.SingleService("api", "project.v0", 8080, external: true), Fixtures.KubernetesConfig(), kubernetesCfg);
        services[0].HasIngress.ShouldBeTrue();
        services[0].IngressHost.ShouldBe("api.example.com");
    }

    [Fact]
    public void Generate_NoIngress_ForInternalBinding()
    {
        var kubernetesCfg = DefaultKubernetesConfig();
        kubernetesCfg.GenerateIngress = true;
        kubernetesCfg.IngressBaseDomain = "example.com";
        var services = _sut.Generate(Fixtures.SingleService("api", "project.v0", 8080), Fixtures.KubernetesConfig(), kubernetesCfg);
        services[0].HasIngress.ShouldBeFalse();
    }

    [Fact]
    public void Generate_NoIngress_WhenNoDomain()
    {
        var services = _sut.Generate(Fixtures.SingleService(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig());
        services[0].HasIngress.ShouldBeFalse();
    }

    [Fact]
    public void Generate_SetsDependencies()
    {
        var services = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig());
        services.Single(s => s.ServiceName == "worker").Dependencies.ShouldContain("api");
        // single item implied;
    }

    [Fact]
    public void Generate_ProducesAllThreeServices()
    {
        var svcs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig());
        svcs.Count.ShouldBe(3);
    }

    [Fact]
    public void Generate_EmptyManifest_ReturnsEmpty()
    {
        var svcs = _sut.Generate(Fixtures.EmptyManifest(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig());
        svcs.ShouldBeEmpty();
    }

    [Fact]
    public void Generate_RespectsOverride_ImageTag()
    {
        var config = Fixtures.KubernetesConfig();
        config.ServiceOverrides["api"] = new() { ImageTag = "canary" };
        var svcs = _sut.Generate(Fixtures.SimpleManifest(), config, DefaultKubernetesConfig());
        svcs.Single(s => s.ServiceName == "api").ImageTag.ShouldBe("canary");
    }

    [Fact]
    public void Generate_RespectsOverride_Count()
    {
        var config = Fixtures.KubernetesConfig();
        config.ServiceOverrides["api"] = new() { Count = 3 };
        var svcs = _sut.Generate(Fixtures.SimpleManifest(), config, DefaultKubernetesConfig());
        svcs.Single(s => s.ServiceName == "api").Replicas.ShouldBe(3);
    }

    [Fact]
    public void Generate_ContainerResource_UsesDirectImage()
    {
        var svcs = _sut.Generate(Fixtures.SingleService("redis", "container.v0"), Fixtures.KubernetesConfig(), DefaultKubernetesConfig());
        svcs[0].Image.ShouldBe("nginx:latest");
    }
}
