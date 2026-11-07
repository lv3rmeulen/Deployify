using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Target.AwsEcs.Tests;

public sealed class EcsTaskDefinitionGeneratorTests
{
    private readonly EcsTaskDefinitionGenerator _sut = new();

    // ── Generate (model) ─────────────────────────────────────────────────────

    [Fact]
    public void Generate_SkipsParametersAndValues()
    {
        var services = _sut.Generate(EcsFixtures.ManifestWithIgnoredTypes(), EcsFixtures.EcsConfig(), new EcsTargetConfig());
        services.ShouldHaveSingleItem().Name.ShouldBe("api");
    }

    [Fact]
    public void Generate_SetsImage_FromRegistry_ForProjects()
    {
        var config = EcsFixtures.EcsConfig();
        config.ContainerRegistry = "123456789.dkr.ecr.us-east-1.amazonaws.com/myapp";
        var services = _sut.Generate(EcsFixtures.SingleService("api"), config, new EcsTargetConfig());
        services[0].Image.ShouldBe("123456789.dkr.ecr.us-east-1.amazonaws.com/myapp/api");
    }

    [Fact]
    public void Generate_SetsImage_Directly_ForContainers()
    {
        var services = _sut.Generate(EcsFixtures.SingleService("redis", "container.v0"), EcsFixtures.EcsConfig(), new EcsTargetConfig());
        services[0].Image.ShouldBe("nginx:latest");
    }

    [Fact]
    public void Generate_SetsImageTag_FromConfig()
    {
        var config = EcsFixtures.EcsConfig();
        config.ImageTag = "sha-abc123";
        var services = _sut.Generate(EcsFixtures.SingleService(), config, new EcsTargetConfig());
        services[0].ImageTag.ShouldBe("sha-abc123");
    }

    [Fact]
    public void Generate_RespectsServiceOverride_ImageTag()
    {
        var config = EcsFixtures.EcsConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride { ImageTag = "canary" };
        var services = _sut.Generate(EcsFixtures.SingleService("myapp"), config, new EcsTargetConfig());
        services[0].ImageTag.ShouldBe("canary");
    }

    [Fact]
    public void Generate_RespectsServiceOverride_Count()
    {
        var config = EcsFixtures.EcsConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride { Count = 5 };
        var services = _sut.Generate(EcsFixtures.SingleService("myapp"), config, new EcsTargetConfig());
        services[0].DesiredCount.ShouldBe(5);
    }

    [Fact]
    public void Generate_MergesEnvVars()
    {
        var services = _sut.Generate(EcsFixtures.SimpleManifest(), EcsFixtures.EcsConfig(), new EcsTargetConfig());
        services.Single(s => s.Name == "api").Env.ShouldContainKey("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public void Generate_PopulatesPortMappings()
    {
        var services = _sut.Generate(EcsFixtures.SimpleManifest(), EcsFixtures.EcsConfig(), new EcsTargetConfig());
        services.Single(s => s.Name == "api").PortMappings.ShouldHaveSingleItem()
            .ContainerPort.ShouldBe(8080);
    }

    [Fact]
    public void Generate_PopulatesDependencies()
    {
        var services = _sut.Generate(EcsFixtures.SimpleManifest(), EcsFixtures.EcsConfig(), new EcsTargetConfig());
        services.Single(s => s.Name == "worker").Dependencies.ShouldContain("api");
        // single item implied;
    }

    [Fact]
    public void Generate_SetsDefaultCpuAndMemory()
    {
        var cfg = new EcsTargetConfig { DefaultCpu = 512, DefaultMemory = 1024 };
        var services = _sut.Generate(EcsFixtures.SingleService(), EcsFixtures.EcsConfig(), cfg);
        services[0].Cpu.ShouldBe(512);
        services[0].Memory.ShouldBe(1024);
    }

    [Fact]
    public void Generate_SetsIsProjectService_ForProjects()
    {
        var services = _sut.Generate(EcsFixtures.SingleService("api", "project.v0"), EcsFixtures.EcsConfig(), new EcsTargetConfig());
        services[0].IsProjectService.ShouldBeTrue();
    }

    [Fact]
    public void Generate_SetsIsProjectService_False_ForContainers()
    {
        var services = _sut.Generate(EcsFixtures.SingleService("redis", "container.v0"), EcsFixtures.EcsConfig(), new EcsTargetConfig());
        services[0].IsProjectService.ShouldBeFalse();
    }

    // ── WriteTaskDefinitionAsync ──────────────────────────────────────────────

    [Fact]
    public async Task WriteTaskDefinition_CreatesFile_WithServiceName()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();
        var ecsCfg = new EcsTargetConfig { Region = "us-east-1", ClusterName = "test" };
        var services = _sut.Generate(EcsFixtures.SingleService("myapi"), config, ecsCfg);

        var path = await _sut.WriteTaskDefinitionAsync(services[0], config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);

        File.Exists(path).ShouldBeTrue();
        path.ShouldEndWith("myapi.taskdef.json");
    }

    [Fact]
    public async Task WriteTaskDefinition_ProducesValidJson()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();
        var ecsCfg = new EcsTargetConfig { Region = "eu-west-1" };
        var services = _sut.Generate(EcsFixtures.SingleService("api"), config, ecsCfg);

        var path = await _sut.WriteTaskDefinitionAsync(services[0], config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        var act = () => JsonDocument.Parse(json);
        Should.NotThrow(act);
    }

    [Fact]
    public async Task WriteTaskDefinition_ContainsFamily()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();
        var ecsCfg = new EcsTargetConfig();
        var services = _sut.Generate(EcsFixtures.SingleService("myapi"), config, ecsCfg);

        var path = await _sut.WriteTaskDefinitionAsync(services[0], config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        doc.RootElement.GetProperty("family").GetString().ShouldBe("myapi");
    }

    [Fact]
    public async Task WriteTaskDefinition_ContainsContainerDefinition()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();
        var ecsCfg = new EcsTargetConfig();
        var services = _sut.Generate(EcsFixtures.SingleService("api"), config, ecsCfg);

        var path = await _sut.WriteTaskDefinitionAsync(services[0], config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        doc.RootElement.GetProperty("containerDefinitions").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task WriteTaskDefinition_ContainsImageWithTag()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();
        config.ContainerRegistry = "reg.io/myapp";
        config.ImageTag = "v2";
        var ecsCfg = new EcsTargetConfig();
        var services = _sut.Generate(EcsFixtures.SingleService("api"), config, ecsCfg);

        var path = await _sut.WriteTaskDefinitionAsync(services[0], config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        json.ShouldContain("reg.io/myapp/api:v2");
    }

    [Fact]
    public async Task WriteTaskDefinition_ContainsPortMappings_WhenBindingsPresent()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();
        var ecsCfg = new EcsTargetConfig();
        var services = _sut.Generate(EcsFixtures.SingleService("api", "project.v0", 8080), config, ecsCfg);

        var path = await _sut.WriteTaskDefinitionAsync(services[0], config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        var container = doc.RootElement.GetProperty("containerDefinitions")[0];
        container.GetProperty("portMappings").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task WriteTaskDefinition_ContainsEnvVars_WhenPresent()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();
        var ecsCfg = new EcsTargetConfig();
        var services = _sut.Generate(EcsFixtures.SimpleManifest(), config, ecsCfg);
        var api = services.Single(s => s.Name == "api");

        var path = await _sut.WriteTaskDefinitionAsync(api, config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        json.ShouldContain("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public async Task WriteTaskDefinition_ContainsLogConfiguration()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();
        var ecsCfg = new EcsTargetConfig { Region = "ap-southeast-1" };
        var services = _sut.Generate(EcsFixtures.SingleService("api"), config, ecsCfg);

        var path = await _sut.WriteTaskDefinitionAsync(services[0], config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        json.ShouldContain("awslogs");
        json.ShouldContain("ap-southeast-1");
    }

    [Fact]
    public async Task WriteTaskDefinition_ContainsFargate_RequiresCompatibility()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();
        var ecsCfg = new EcsTargetConfig { LaunchType = EcsLaunchType.Fargate };
        var services = _sut.Generate(EcsFixtures.SingleService("api"), config, ecsCfg);

        var path = await _sut.WriteTaskDefinitionAsync(services[0], config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        json.ShouldContain("FARGATE");
    }

    [Fact]
    public async Task WriteTaskDefinition_ContainsNetworkMode_Awsvpc()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();
        var ecsCfg = new EcsTargetConfig();
        var services = _sut.Generate(EcsFixtures.SingleService("api"), config, ecsCfg);

        var path = await _sut.WriteTaskDefinitionAsync(services[0], config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        json.ShouldContain("awsvpc");
    }

    [Fact]
    public async Task WriteTaskDefinition_IncludesExecutionRole_WhenConfigured()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();
        var ecsCfg = new EcsTargetConfig { ExecutionRoleArn = "arn:aws:iam::123:role/ecsExec" };
        var services = _sut.Generate(EcsFixtures.SingleService("api"), config, ecsCfg);

        var path = await _sut.WriteTaskDefinitionAsync(services[0], config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        json.ShouldContain("arn:aws:iam::123:role/ecsExec");
    }

    [Fact]
    public async Task WriteTaskDefinition_GeneratesAllServices()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();
        var ecsCfg = new EcsTargetConfig();
        foreach (var svc in _sut.Generate(EcsFixtures.SimpleManifest(), config, ecsCfg))
        {
            await _sut.WriteTaskDefinitionAsync(svc, config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);
        }

        Directory.GetFiles(dir.Path, "*.taskdef.json").Length.ShouldBe(3);
    }

    [Fact]
    public async Task WriteTaskDefinition_IsIdempotent()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();
        var ecsCfg = new EcsTargetConfig();
        var services = _sut.Generate(EcsFixtures.SingleService("api"), config, ecsCfg);

        var path1 = await _sut.WriteTaskDefinitionAsync(services[0], config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);
        var path2 = await _sut.WriteTaskDefinitionAsync(services[0], config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);

        path1.ShouldBe(path2);
        Directory.GetFiles(dir.Path, "*.taskdef.json").Length.ShouldBe(1);
    }
}
