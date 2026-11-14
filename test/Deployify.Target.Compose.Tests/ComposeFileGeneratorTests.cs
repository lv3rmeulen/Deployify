using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Target.Compose.Tests;

public sealed class ComposeFileGeneratorTests
{
    private readonly ComposeFileGenerator _sut = new();

    private static ComposeTargetConfig Cfg(
        string project = "testproject", bool profiles = false,
        bool envFile = false, bool merged = true) => new()
    {
        ProjectName = project, RestartPolicy = "unless-stopped",
        GenerateProfiles = profiles, GenerateEnvFile = envFile,
        GenerateMergedFile = merged, GenerateHealthChecks = true
    };

    // Generate (model) ---------------------------------------------------

    [Fact]
    public async Task Generate_SkipsParametersAndValues()
    {
        var svcs = await _sut.GenerateAsync(Fixtures.ManifestWithIgnoredTypes(), Fixtures.ComposeConfig(), Cfg(), TestContext.Current.CancellationToken);
        svcs.ShouldHaveSingleItem().Name.ShouldBe("api");
    }

    [Fact]
    public async Task Generate_SetsImage_FromRegistry_ForProjects()
    {
        var config = Fixtures.ComposeConfig();
        config.ContainerRegistry = "registry.example.com/app";
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService("api"), config, Cfg(), TestContext.Current.CancellationToken);
        svcs[0].Image.ShouldBe("registry.example.com/app/api");
    }

    [Fact]
    public async Task Generate_SetsImage_Directly_ForContainers()
    {
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService("redis", "container.v0"), Fixtures.ComposeConfig(), Cfg(), TestContext.Current.CancellationToken);
        svcs[0].Image.ShouldBe("nginx:latest");
    }

    [Fact]
    public async Task Generate_SetsImageTag_FromConfig()
    {
        var config = Fixtures.ComposeConfig();
        config.ImageTag = "sha-abc";
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService(), config, Cfg(), TestContext.Current.CancellationToken);
        svcs[0].ImageTag.ShouldBe("sha-abc");
    }

    [Fact]
    public async Task Generate_RespectsServiceOverride_ImageTag()
    {
        var config = Fixtures.ComposeConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride { ImageTag = "canary" };
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService("myapp"), config, Cfg(), TestContext.Current.CancellationToken);
        svcs[0].ImageTag.ShouldBe("canary");
    }

    [Fact]
    public async Task Generate_RespectsServiceOverride_Count()
    {
        var config = Fixtures.ComposeConfig();
        config.ServiceOverrides["myapp"] = new ServiceOverride { Count = 3 };
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService("myapp"), config, Cfg(), TestContext.Current.CancellationToken);
        svcs[0].Replicas.ShouldBe(3);
    }

    [Fact]
    public async Task Generate_MergesEnvVars()
    {
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.ComposeConfig(), Cfg(), TestContext.Current.CancellationToken);
        svcs.Single(s => s.Name == "api").Env.ShouldContainKey("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public async Task Generate_PopulatesDependsOn_FromReferences()
    {
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.ComposeConfig(), Cfg(), TestContext.Current.CancellationToken);
        svcs.Single(s => s.Name == "worker").DependsOn.ShouldContain("api");
        // single item implied;
    }

    [Fact]
    public async Task Generate_PopulatesPorts()
    {
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.ComposeConfig(), Cfg(), TestContext.Current.CancellationToken);
        svcs.Single(s => s.Name == "api").Ports.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Generate_AllocatesUniqueHostPorts()
    {
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.ComposeConfig(), Cfg(), TestContext.Current.CancellationToken);
        var all = svcs.SelectMany(s => s.Ports).Select(p => p.HostPort).ToList();
        all.ShouldBeUnique();
    }

    [Fact]
    public async Task Generate_SetsProfiles_WhenEnabled()
    {
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.ComposeConfig(), Cfg(profiles: true), TestContext.Current.CancellationToken);
        svcs.Single(s => s.Name == "api").Profiles.ShouldContain("prod");
        svcs.Single(s => s.Name == "redis").Profiles.ShouldContain("dev");
    }

    [Fact]
    public async Task Generate_NoProfiles_WhenDisabled()
    {
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.ComposeConfig(), Cfg(profiles: false), TestContext.Current.CancellationToken);
        svcs.All(s => s.Profiles.Count == 0).ShouldBeTrue();
    }

    [Fact]
    public async Task Generate_SetsRestartPolicy()
    {
        var cfg = Cfg();
        cfg.RestartPolicy = "on-failure";
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService(), Fixtures.ComposeConfig(), cfg, TestContext.Current.CancellationToken);
        svcs[0].RestartPolicy.ShouldBe("on-failure");
    }

    [Fact]
    public async Task Generate_MarksProjectServices()
    {
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.ComposeConfig(), Cfg(), TestContext.Current.CancellationToken);
        svcs.Single(s => s.Name == "api").IsProjectService.ShouldBeTrue();
        svcs.Single(s => s.Name == "redis").IsProjectService.ShouldBeFalse();
    }

    [Fact]
    public async Task Generate_EmptyManifest_ReturnsNoServices()
    {
        var svcs = await _sut.GenerateAsync(Fixtures.EmptyManifest(), Fixtures.ComposeConfig(), Cfg(), TestContext.Current.CancellationToken);
        svcs.ShouldBeEmpty();
    }

    // WriteServiceFileAsync ----------------------------------------------

    [Fact]
    public async Task WriteServiceFile_CreatesComposeYml()
    {
        using var dir = new TempDir();
        var cfg = Cfg();
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService("api"), Fixtures.ComposeConfig(), cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteServiceFileAsync(svcs[0], dir.Path, cfg, TestContext.Current.CancellationToken);
        File.Exists(path).ShouldBeTrue();
        path.ShouldEndWith("compose.yml");
    }

    [Fact]
    public async Task WriteServiceFile_ContainsServiceName()
    {
        using var dir = new TempDir();
        var cfg = Cfg();
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService("myservice"), Fixtures.ComposeConfig(), cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteServiceFileAsync(svcs[0], dir.Path, cfg, TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldContain("myservice:");
    }

    [Fact]
    public async Task WriteServiceFile_ContainsImage()
    {
        using var dir = new TempDir();
        var cfg = Cfg();
        var config = Fixtures.ComposeConfig();
        config.ContainerRegistry = "reg.example.com/app";
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService("api"), config, cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteServiceFileAsync(svcs[0], dir.Path, cfg, TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldContain("reg.example.com/app/api");
    }

    [Fact]
    public async Task WriteServiceFile_ContainsPorts()
    {
        using var dir = new TempDir();
        var cfg = Cfg();
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService("api", "project.v0", 8080), Fixtures.ComposeConfig(), cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteServiceFileAsync(svcs[0], dir.Path, cfg, TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldContain("8080");
    }

    [Fact]
    public async Task WriteServiceFile_NoHealthCheck_ByDefault()
    {
        // Aspire images ship without curl/wget, so a generated healthcheck would mark
        // every service unhealthy. Healthchecks are opt-in.
        using var dir = new TempDir();
        var cfg = Cfg();
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService("api", "project.v0", 8080), Fixtures.ComposeConfig(), cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteServiceFileAsync(svcs[0], dir.Path, cfg, TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldNotContain("healthcheck:");
    }

    [Fact]
    public async Task WriteServiceFile_ContainsDependsOn_WhenDepsExist()
    {
        using var dir = new TempDir();
        var cfg = Cfg();
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.ComposeConfig(), cfg, TestContext.Current.CancellationToken);
        var worker = svcs.Single(s => s.Name == "worker");
        var path = await _sut.WriteServiceFileAsync(worker, dir.Path, cfg, TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldContain("depends_on:");
    }

    [Fact]
    public async Task WriteServiceFile_WritesEnvFile_WhenEnabled()
    {
        using var dir = new TempDir();
        var cfg = Cfg(envFile: true);
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.ComposeConfig(envFile: true), cfg, TestContext.Current.CancellationToken);
        var api = svcs.Single(s => s.Name == "api");
        await _sut.WriteServiceFileAsync(api, dir.Path, cfg, TestContext.Current.CancellationToken);
        File.Exists(Path.Combine(dir.Path, "api", ".env")).ShouldBeTrue();
    }

    [Fact]
    public async Task WriteServiceFile_NoEnvFile_WhenDisabled()
    {
        using var dir = new TempDir();
        var cfg = Cfg(envFile: false);
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.ComposeConfig(envFile: false), cfg, TestContext.Current.CancellationToken);
        var api = svcs.Single(s => s.Name == "api");
        await _sut.WriteServiceFileAsync(api, dir.Path, cfg, TestContext.Current.CancellationToken);
        File.Exists(Path.Combine(dir.Path, "api", ".env")).ShouldBeFalse();
    }

    [Fact]
    public async Task WriteServiceFile_CreatesSubdirectory()
    {
        using var dir = new TempDir();
        var cfg = Cfg();
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService("myapp"), Fixtures.ComposeConfig(), cfg, TestContext.Current.CancellationToken);
        await _sut.WriteServiceFileAsync(svcs[0], dir.Path, cfg, TestContext.Current.CancellationToken);
        Directory.Exists(Path.Combine(dir.Path, "myapp")).ShouldBeTrue();
    }

    // WriteMergedFileAsync -----------------------------------------------

    [Fact]
    public async Task WriteMergedFile_CreatesComposeYmlAtRoot()
    {
        using var dir = new TempDir();
        var cfg = Cfg();
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.ComposeConfig(), cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteMergedFileAsync(svcs, dir.Path, cfg, TestContext.Current.CancellationToken);
        path.ShouldBe(Path.Combine(dir.Path, "compose.yml"));
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task WriteMergedFile_ContainsAllServiceNames()
    {
        using var dir = new TempDir();
        var cfg = Cfg();
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.ComposeConfig(), cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteMergedFileAsync(svcs, dir.Path, cfg, TestContext.Current.CancellationToken);
        var yaml = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        yaml.ShouldContain("api:");
        yaml.ShouldContain("worker:");
        yaml.ShouldContain("redis:");
    }

    [Fact]
    public async Task WriteMergedFile_ContainsProjectNetworkName()
    {
        using var dir = new TempDir();
        var cfg = Cfg("mynet");
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.ComposeConfig("mynet"), cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteMergedFileAsync(svcs, dir.Path, cfg, TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldContain("mynet:");
    }

    [Fact]
    public async Task WriteAllServiceFiles_CreatesSubdirPerService()
    {
        using var dir = new TempDir();
        var cfg = Cfg();
        var svcs = await _sut.GenerateAsync(Fixtures.SimpleManifest(), Fixtures.ComposeConfig(), cfg, TestContext.Current.CancellationToken);
        foreach (var svc in svcs)
        {
            await _sut.WriteServiceFileAsync(svc, dir.Path, cfg, TestContext.Current.CancellationToken);
        }

        Directory.GetDirectories(dir.Path).Length.ShouldBe(3);
    }

    [Fact]
    public async Task WriteServiceFile_ContainsRestartPolicy()
    {
        using var dir = new TempDir();
        var cfg = Cfg();
        cfg.RestartPolicy = "always";
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService("api"), Fixtures.ComposeConfig(), cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteServiceFileAsync(svcs[0], dir.Path, cfg, TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldContain("restart: always");
    }

    [Fact]
    public async Task WriteServiceFile_ContainsNetwork()
    {
        using var dir = new TempDir();
        var cfg = Cfg("myproj");
        var svcs = await _sut.GenerateAsync(Fixtures.SingleService("api"), Fixtures.ComposeConfig("myproj"), cfg, TestContext.Current.CancellationToken);
        var path = await _sut.WriteServiceFileAsync(svcs[0], dir.Path, cfg, TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldContain("myproj");
    }

    [Fact]
    public async Task Generate_PutsDockerfileBuilds_InTheProdProfileOnly()
    {
        var manifest = new AspireManifest
        {
            Resources = new System.Collections.Generic.Dictionary<string, AspireResource>
            {
                ["frontend"] = new() { Type = "dockerfile.v0", Path = "/src/frontend/Dockerfile", Context = "/src/frontend" }
            }
        };

        var svcs = await _sut.GenerateAsync(manifest, Fixtures.ComposeConfig(), Cfg(profiles: true), TestContext.Current.CancellationToken);

        svcs.ShouldHaveSingleItem().Profiles.ShouldBe(["prod"]);
    }
}
