using System.Text.Json;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Target.AzureContainerApps.Tests;

public sealed class AcaManifestGeneratorTests
{
    private readonly AcaManifestGenerator _sut = new();

    // ── Generate (model) ─────────────────────────────────────────────────────

    [Fact]
    public void Generate_SkipsParametersAndValues()
    {
        var apps = _sut.Generate(AcaFixtures.ManifestWithIgnoredTypes(), AcaFixtures.AcaConfig(), new AcaTargetConfig());
        apps.ShouldHaveSingleItem().Name.ShouldBe("api");
    }

    [Fact]
    public void Generate_SetsImage_FromRegistry_ForProjects()
    {
        var config = AcaFixtures.AcaConfig();
        config.ContainerRegistry = "myregistry.azurecr.io/myapp";
        var apps = _sut.Generate(AcaFixtures.SingleService("api"), config, new AcaTargetConfig());
        apps[0].Image.ShouldBe("myregistry.azurecr.io/myapp/api");
    }

    [Fact]
    public void Generate_SetsImage_Directly_ForContainers()
    {
        var apps = _sut.Generate(AcaFixtures.SingleService("redis", "container.v0"), AcaFixtures.AcaConfig(), new AcaTargetConfig());
        apps[0].Image.ShouldBe("nginx:latest");
    }

    [Fact]
    public void Generate_SetsImageTag_FromConfig()
    {
        var config = AcaFixtures.AcaConfig();
        config.ImageTag = "sha-abc123";
        var apps = _sut.Generate(AcaFixtures.SingleService(), config, new AcaTargetConfig());
        apps[0].ImageTag.ShouldBe("sha-abc123");
    }

    [Fact]
    public void Generate_RespectsServiceOverride_ImageTag()
    {
        var config = AcaFixtures.AcaConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride { ImageTag = "canary" };
        var apps = _sut.Generate(AcaFixtures.SingleService("myapp"), config, new AcaTargetConfig());
        apps[0].ImageTag.ShouldBe("canary");
    }

    [Fact]
    public void Generate_RespectsServiceOverride_Count_AffectsMaxReplicas()
    {
        var config = AcaFixtures.AcaConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride { Count = 5 };
        var apps = _sut.Generate(AcaFixtures.SingleService("myapp"), config, new AcaTargetConfig());
        apps[0].MaxReplicas.ShouldBe(5);
    }

    [Fact]
    public void Generate_MergesEnvVars()
    {
        var apps = _sut.Generate(AcaFixtures.SimpleManifest(), AcaFixtures.AcaConfig(), new AcaTargetConfig());
        apps.Single(a => a.Name == "api").Env.ShouldContainKey("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public void Generate_PopulatesPorts()
    {
        var apps = _sut.Generate(AcaFixtures.SimpleManifest(), AcaFixtures.AcaConfig(), new AcaTargetConfig());
        apps.Single(a => a.Name == "api").Ports.ShouldContain(p => p.ContainerPort == 8080);
    }

    [Fact]
    public void Generate_PopulatesDependencies()
    {
        var apps = _sut.Generate(AcaFixtures.SimpleManifest(), AcaFixtures.AcaConfig(), new AcaTargetConfig());
        apps.Single(a => a.Name == "worker").Dependencies.ShouldContain("api");
        // single item implied;
    }

    [Fact]
    public void Generate_SetsMinAndMaxReplicas_FromConfig()
    {
        var acaCfg = new AcaTargetConfig { MinReplicas = 1, MaxReplicas = 10 };
        var apps = _sut.Generate(AcaFixtures.SingleService(), AcaFixtures.AcaConfig(), acaCfg);
        apps[0].MinReplicas.ShouldBe(1);
        apps[0].MaxReplicas.ShouldBe(10);
    }

    [Fact]
    public void Generate_SetsIsProjectService_ForProjects()
    {
        var apps = _sut.Generate(AcaFixtures.SingleService("api", "project.v0"), AcaFixtures.AcaConfig(), new AcaTargetConfig());
        apps[0].IsProjectService.ShouldBeTrue();
    }

    [Fact]
    public void Generate_SetsIsProjectService_False_ForContainers()
    {
        var apps = _sut.Generate(AcaFixtures.SingleService("redis", "container.v0"), AcaFixtures.AcaConfig(), new AcaTargetConfig());
        apps[0].IsProjectService.ShouldBeFalse();
    }

    [Fact]
    public void Generate_PropagatesIngressType()
    {
        var acaCfg = new AcaTargetConfig { IngressType = AcaIngressType.Internal };
        var apps = _sut.Generate(AcaFixtures.SingleService(), AcaFixtures.AcaConfig(), acaCfg);
        apps[0].IngressType.ShouldBe(AcaIngressType.Internal);
    }

    // ── WriteAppSpecAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAppSpec_CreatesFile_WithServiceName()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();
        var acaCfg = new AcaTargetConfig { Location = "eastus" };
        var apps = _sut.Generate(AcaFixtures.SingleService("myapi"), config, acaCfg);

        var path = await _sut.WriteAppSpecAsync(apps[0], config, acaCfg, dir.Path);

        File.Exists(path).ShouldBeTrue();
        path.ShouldEndWith("myapi.containerapp.json");
    }

    [Fact]
    public async Task WriteAppSpec_ProducesValidJson()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();
        var acaCfg = new AcaTargetConfig();
        var apps = _sut.Generate(AcaFixtures.SingleService("api"), config, acaCfg);

        var path = await _sut.WriteAppSpecAsync(apps[0], config, acaCfg, dir.Path);
        var act = () => JsonDocument.Parse(File.ReadAllText(path));

        Should.NotThrow(act);
    }

    [Fact]
    public async Task WriteAppSpec_ContainsAppName()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();
        var acaCfg = new AcaTargetConfig();
        var apps = _sut.Generate(AcaFixtures.SingleService("myapi"), config, acaCfg);

        var path = await _sut.WriteAppSpecAsync(apps[0], config, acaCfg, dir.Path);
        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        doc.RootElement.GetProperty("name").GetString().ShouldBe("myapi");
    }

    [Fact]
    public async Task WriteAppSpec_ContainsLocation()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig(location: "westeurope");
        var acaCfg = new AcaTargetConfig { Location = "westeurope" };
        var apps = _sut.Generate(AcaFixtures.SingleService("api"), config, acaCfg);

        var path = await _sut.WriteAppSpecAsync(apps[0], config, acaCfg, dir.Path);
        var json = await File.ReadAllTextAsync(path);

        json.ShouldContain("westeurope");
    }

    [Fact]
    public async Task WriteAppSpec_ContainsImageWithTag()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();
        config.ContainerRegistry = "reg.io/myapp";
        config.ImageTag = "v3";
        var acaCfg = new AcaTargetConfig();
        var apps = _sut.Generate(AcaFixtures.SingleService("api"), config, acaCfg);

        var path = await _sut.WriteAppSpecAsync(apps[0], config, acaCfg, dir.Path);
        var json = await File.ReadAllTextAsync(path);

        json.ShouldContain("reg.io/myapp/api:v3");
    }

    [Fact]
    public async Task WriteAppSpec_ContainsIngress_WhenHttpBindingPresent()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();
        var acaCfg = new AcaTargetConfig { IngressType = AcaIngressType.External };
        var apps = _sut.Generate(AcaFixtures.SingleService("api", "project.v0", 8080), config, acaCfg);

        var path = await _sut.WriteAppSpecAsync(apps[0], config, acaCfg, dir.Path);
        var json = await File.ReadAllTextAsync(path);

        json.ShouldContain("ingress");
        json.ShouldContain("8080");
    }

    [Fact]
    public async Task WriteAppSpec_IngressExternal_True_WhenExternalIngress()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();
        var acaCfg = new AcaTargetConfig { IngressType = AcaIngressType.External };
        var apps = _sut.Generate(AcaFixtures.SingleService("api", "project.v0", 8080), config, acaCfg);

        var path = await _sut.WriteAppSpecAsync(apps[0], config, acaCfg, dir.Path);
        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        var ingress = doc.RootElement
            .GetProperty("properties")
            .GetProperty("configuration")
            .GetProperty("ingress");
        ingress.GetProperty("external").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task WriteAppSpec_ContainsScaleMinMax()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();
        var acaCfg = new AcaTargetConfig { MinReplicas = 1, MaxReplicas = 5 };
        var apps = _sut.Generate(AcaFixtures.SingleService("api"), config, acaCfg);

        var path = await _sut.WriteAppSpecAsync(apps[0], config, acaCfg, dir.Path);
        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        var scale = doc.RootElement
            .GetProperty("properties")
            .GetProperty("template")
            .GetProperty("scale");
        scale.GetProperty("minReplicas").GetInt32().ShouldBe(1);
        scale.GetProperty("maxReplicas").GetInt32().ShouldBe(5);
    }

    [Fact]
    public async Task WriteAppSpec_ContainsAcrRegistry_WhenConfigured()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfigWithAcr("myregistry.azurecr.io");
        var acaCfg = new AcaTargetConfig { AcrLoginServer = "myregistry.azurecr.io" };
        var apps = _sut.Generate(AcaFixtures.SingleService("api"), config, acaCfg);

        var path = await _sut.WriteAppSpecAsync(apps[0], config, acaCfg, dir.Path);
        var json = await File.ReadAllTextAsync(path);

        json.ShouldContain("myregistry.azurecr.io");
    }

    [Fact]
    public async Task WriteAppSpec_ContainsEnvVars_WhenPresent()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();
        var acaCfg = new AcaTargetConfig();
        var apps = _sut.Generate(AcaFixtures.SimpleManifest(), config, acaCfg);
        var api = apps.Single(a => a.Name == "api");

        var path = await _sut.WriteAppSpecAsync(api, config, acaCfg, dir.Path);
        var json = await File.ReadAllTextAsync(path);

        json.ShouldContain("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public async Task WriteAppSpec_GeneratesAllServices()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();
        var acaCfg = new AcaTargetConfig();
        foreach (var app in _sut.Generate(AcaFixtures.SimpleManifest(), config, acaCfg))
        {
	        await _sut.WriteAppSpecAsync(app, config, acaCfg, dir.Path);
        }

        Directory.GetFiles(dir.Path, "*.containerapp.json").Length.ShouldBe(3);
    }

    [Fact]
    public async Task WriteAppSpec_IsIdempotent()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();
        var acaCfg = new AcaTargetConfig();
        var apps = _sut.Generate(AcaFixtures.SingleService("api"), config, acaCfg);

        var path1 = await _sut.WriteAppSpecAsync(apps[0], config, acaCfg, dir.Path);
        var path2 = await _sut.WriteAppSpecAsync(apps[0], config, acaCfg, dir.Path);

        path1.ShouldBe(path2);
        Directory.GetFiles(dir.Path, "*.containerapp.json").Length.ShouldBe(1);
    }
}
