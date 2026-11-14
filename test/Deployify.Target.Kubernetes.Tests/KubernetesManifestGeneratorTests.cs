using System.Threading.Tasks;
using System.Linq;
using Deployify.Core.Models;
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
    public async Task Generate_SkipsParametersAndValues()
    {
        var services = await _sut.GenerateAsync(Fixtures.ManifestWithIgnoredTypes(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig(), TestContext.Current.CancellationToken);
        services.ShouldHaveSingleItem().ServiceName.ShouldBe("api");
    }

    [Fact]
    public async Task Generate_AssignsNamespace_WithPrefix()
    {
        var services = await _sut.GenerateAsync(Fixtures.SingleService("api"), Fixtures.KubernetesConfig("myapp"), DefaultKubernetesConfig("myapp"), TestContext.Current.CancellationToken);
        services[0].Namespace.ShouldBe("myapp-api");
    }

    [Fact]
    public async Task Generate_SetsImage_FromRegistry_ForProjects()
    {
        var config = Fixtures.KubernetesConfig();
        config.ContainerRegistry = "registry.example.com/app";
        var services = await _sut.GenerateAsync(Fixtures.SingleService("api"), config, DefaultKubernetesConfig(), TestContext.Current.CancellationToken);
        services[0].Image.ShouldBe("registry.example.com/app/api");
    }

    [Fact]
    public async Task Generate_SetsImageTag()
    {
        var config = Fixtures.KubernetesConfig();
        config.ImageTag = "v2.0.0";
        var services = await _sut.GenerateAsync(Fixtures.SingleService(), config, DefaultKubernetesConfig(), TestContext.Current.CancellationToken);
        services[0].ImageTag.ShouldBe("v2.0.0");
    }

    [Fact]
    public async Task Generate_PopulatesPorts_FromBindings()
    {
        var services = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig(), TestContext.Current.CancellationToken);
        services.Single(s => s.ServiceName == "api").Ports.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Generate_SetsHasService_WhenPortsPresent()
    {
        var services = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig(), TestContext.Current.CancellationToken);
        services.Single(s => s.ServiceName == "api").HasService.ShouldBeTrue();
    }

    [Fact]
    public async Task Generate_SetsHasConfigMap_WhenEnvVarsPresent()
    {
        var services = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig(), TestContext.Current.CancellationToken);
        services.Single(s => s.ServiceName == "api").HasConfigMap.ShouldBeTrue();
    }

    [Fact]
    public async Task Generate_NoConfigMap_WhenNoEnv()
    {
        var services = await _sut.GenerateAsync(Fixtures.SingleService("worker", "project.v0"), Fixtures.KubernetesConfig(), DefaultKubernetesConfig(), TestContext.Current.CancellationToken);
        services[0].HasConfigMap.ShouldBeFalse();
    }

    [Fact]
    public async Task Generate_SetsIngress_WhenDomainConfigured()
    {
        var kubernetesCfg = DefaultKubernetesConfig();
        kubernetesCfg.GenerateIngress = true;
        kubernetesCfg.IngressBaseDomain = "example.com";
        var services = await _sut.GenerateAsync(Fixtures.SingleService("api", "project.v0", 8080, external: true), Fixtures.KubernetesConfig(), kubernetesCfg, TestContext.Current.CancellationToken);
        services[0].HasIngress.ShouldBeTrue();
        services[0].IngressHost.ShouldBe("api.example.com");
    }

    [Fact]
    public async Task Generate_NoIngress_ForInternalBinding()
    {
        var kubernetesCfg = DefaultKubernetesConfig();
        kubernetesCfg.GenerateIngress = true;
        kubernetesCfg.IngressBaseDomain = "example.com";
        var services = await _sut.GenerateAsync(Fixtures.SingleService("api", "project.v0", 8080), Fixtures.KubernetesConfig(), kubernetesCfg, TestContext.Current.CancellationToken);
        services[0].HasIngress.ShouldBeFalse();
    }

    [Fact]
    public async Task Generate_NoIngress_WhenNoDomain()
    {
        var services = await _sut.GenerateAsync(Fixtures.SingleService(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig(), TestContext.Current.CancellationToken);
        services[0].HasIngress.ShouldBeFalse();
    }

    [Fact]
    public async Task Generate_SetsDependencies()
    {
        var services = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig(), TestContext.Current.CancellationToken);
        services.Single(s => s.ServiceName == "worker").Dependencies.ShouldContain("api");
        // single item implied;
    }

    [Fact]
    public async Task Generate_ProducesAllThreeServices()
    {
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig(), TestContext.Current.CancellationToken);
        svcs.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Generate_EmptyManifest_ReturnsEmpty()
    {
        var svcs = await _sut.GenerateAsync(Fixtures.EmptyManifest(), Fixtures.KubernetesConfig(), DefaultKubernetesConfig(), TestContext.Current.CancellationToken);
        svcs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Generate_RespectsOverride_ImageTag()
    {
        var config = Fixtures.KubernetesConfig();
        config.ServiceOverrides["api"] = new ServiceOverride { ImageTag = "canary" };
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), config, DefaultKubernetesConfig(), TestContext.Current.CancellationToken);
        svcs.Single(s => s.ServiceName == "api").ImageTag.ShouldBe("canary");
    }

    [Fact]
    public async Task Generate_RespectsOverride_Count()
    {
        var config = Fixtures.KubernetesConfig();
        config.ServiceOverrides["api"] = new ServiceOverride { Count = 3 };
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), config, DefaultKubernetesConfig(), TestContext.Current.CancellationToken);
        svcs.Single(s => s.ServiceName == "api").Replicas.ShouldBe(3);
    }

    [Fact]
    public async Task Generate_ContainerResource_UsesDirectImage()
    {
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService("redis", "container.v0"), Fixtures.KubernetesConfig(), DefaultKubernetesConfig(), TestContext.Current.CancellationToken);
        svcs[0].Image.ShouldBe("nginx:latest");
    }

    private const string Digest = "sha256:" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static AspireManifest DockerfileManifest(string name = "frontend") => new()
    {
        Resources = new System.Collections.Generic.Dictionary<string, AspireResource>
        {
            [name] = new()
            {
                Type = "dockerfile.v0", Path = "/src/frontend/Dockerfile", Context = "/src/frontend",
                Bindings = new System.Collections.Generic.Dictionary<string, AspireBinding> { ["http"] = new() { ContainerPort = 3000, Protocol = "http" } }
            }
        }
    };

    [Fact]
    public async Task Generate_PinsThePublishedDigest_ForTheServicesTag()
    {
        var config = Fixtures.KubernetesConfig();
        config.ContainerRegistry = "registry.example.com/app";
        config.ImageTag = "v2";
        config.PublishedImages.Record("registry.example.com/app/api", "v2", Digest, System.DateTimeOffset.UnixEpoch);

        var services = await _sut.GenerateAsync(Fixtures.SingleService("api"), config, DefaultKubernetesConfig(), TestContext.Current.CancellationToken);

        services[0].Image.ShouldBe($"registry.example.com/app/api@{Digest}");
        KubernetesManifestGenerator.Render(services[0]).ShouldContain(f => f.Content.Contains($"registry.example.com/app/api@{Digest}"));
    }

    [Fact]
    public async Task Generate_UsesTheTag_WhenTheDigestBelongsToAnotherTag()
    {
        var config = Fixtures.KubernetesConfig();
        config.ContainerRegistry = "registry.example.com/app";
        config.ImageTag = "v3";
        config.PublishedImages.Record("registry.example.com/app/api", "v2", Digest, System.DateTimeOffset.UnixEpoch);

        var services = await _sut.GenerateAsync(Fixtures.SingleService("api"), config, DefaultKubernetesConfig(), TestContext.Current.CancellationToken);

        services[0].Image.ShouldBe("registry.example.com/app/api");
        services[0].ImageTag.ShouldBe("v3");
    }

    [Fact]
    public async Task Generate_NamesDockerfileBuilds_LikeProjects()
    {
        var config = Fixtures.KubernetesConfig();
        config.ContainerRegistry = "registry.example.com/app";

        var services = await _sut.GenerateAsync(DockerfileManifest(), config, DefaultKubernetesConfig(), TestContext.Current.CancellationToken);

        services.ShouldHaveSingleItem().Image.ShouldBe("registry.example.com/app/frontend");
    }
}
