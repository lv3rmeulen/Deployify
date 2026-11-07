using System.Collections.Generic;
using System.Linq;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

public sealed class DaprTests
{
    // ── WithDaprIfEnabled — disabled ──────────────────────────────────────────

    [Fact]
    public void WithDaprIfEnabled_PreservesAllResources_WhenDisabledAndNoDaprResources()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDapr = false };
        var result = manifest.WithDaprIfEnabled(config);
        result.Resources.Keys.ShouldBe(manifest.Resources.Keys, ignoreOrder: true);
    }

    [Fact]
    public void WithDaprIfEnabled_FoldsDaprV0Resources_EvenWhenDisabled()
    {
        var manifest = new AspireManifest
        {
            Resources = new Dictionary<string, AspireResource>
            {
                ["api"] = new() { Type = "project.v0", Path = "Api/Api.csproj" },
                ["api-dapr"] = new()
                {
                    Type = "dapr.v0",
                    Dapr = new AspireDaprConfig
                    {
                        Application = "api", AppId = "api", AppPort = 8080,
                        DaprHttpPort = 3500, DaprGrpcPort = 50001
                    }
                }
            }
        };
        var config = new EnvironmentConfig { DeployDapr = false };
        var result = manifest.WithDaprIfEnabled(config);
        result.Resources.ContainsKey("api-dapr").ShouldBeFalse();
        result.Resources["api"].HasDaprSidecar.ShouldBeTrue();
    }

    [Fact]
    public void WithDaprIfEnabled_RemovesDaprComponentResources()
    {
        var manifest = new AspireManifest
        {
            Resources = new Dictionary<string, AspireResource>
            {
                ["api"] = new() { Type = "project.v0", Path = "Api/Api.csproj" },
                ["statestore"] = new()
                {
                    Type = "dapr.component.v0",
                    DaprComponent = new AspireDaprComponentConfig { Type = "state" }
                }
            }
        };
        var config = new EnvironmentConfig { DeployDapr = false };
        var result = manifest.WithDaprIfEnabled(config);
        result.Resources.ContainsKey("statestore").ShouldBeFalse();
        result.Resources.Count.ShouldBe(1);
    }

    [Fact]
    public void WithDaprIfEnabled_DoesNotInjectAnnotations_WhenDisabled()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDapr = false };
        var result = manifest.WithDaprIfEnabled(config);
        foreach (var r in result.Resources.Values)
        {
            r.HasDaprSidecar.ShouldBeFalse();
        }
    }

    // ── WithDaprIfEnabled — enabled ───────────────────────────────────────────

    [Fact]
    public void WithDaprIfEnabled_InjectsAnnotations_WhenEnabled()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDapr = true };
        var result = manifest.WithDaprIfEnabled(config);
        foreach (var r in result.Resources.Values)
        {
            r.HasDaprSidecar.ShouldBeTrue();
        }
    }

    [Fact]
    public void WithDaprIfEnabled_ResourceCount_IsUnchanged()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDapr = true };
        var result = manifest.WithDaprIfEnabled(config);
        result.Resources.Count.ShouldBe(manifest.Resources.Count);
    }

    [Fact]
    public void WithDaprIfEnabled_SetsAppId_FromResourceName()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDapr = true };
        var result = manifest.WithDaprIfEnabled(config);
        result.Resources["api"].DaprAppId.ShouldBe("api");
        result.Resources["worker"].DaprAppId.ShouldBe("worker");
    }

    [Fact]
    public void WithDaprIfEnabled_SetsHttpPort_FromDaprConfig()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig
        {
            DeployDapr = true,
            Dapr = new DaprConfig { DaprHttpPort = 4000 }
        };
        var result = manifest.WithDaprIfEnabled(config);
        result.Resources["api"].Annotations![DaprConstants.HttpPortAnnotation].ShouldBe("4000");
    }

    [Fact]
    public void WithDaprIfEnabled_IncrementsHttpPort_PerService()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig
        {
            DeployDapr = true,
            Dapr = new DaprConfig { DaprHttpPort = 3500 }
        };
        var result = manifest.WithDaprIfEnabled(config);
        var ports = result.Resources.Values
            .Select(r => int.Parse(r.Annotations![DaprConstants.HttpPortAnnotation]))
            .ToList();
        ports.Distinct().Count().ShouldBe(ports.Count); // all unique
    }

    [Fact]
    public void WithDaprIfEnabled_SetsAppPort_FromHttpBinding()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDapr = true };
        var result = manifest.WithDaprIfEnabled(config);
        // api has port 8080
        result.Resources["api"].DaprAppPort.ShouldBe(8080);
    }

    [Fact]
    public void WithDaprIfEnabled_NoAppPort_WhenNoHttpBinding()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDapr = true };
        var result = manifest.WithDaprIfEnabled(config);
        // worker has no bindings
        result.Resources["worker"].DaprAppPort.ShouldBeNull();
    }

    [Fact]
    public void WithDaprIfEnabled_SkipsParameters()
    {
        var manifest = new AspireManifest
        {
            Resources = new Dictionary<string, AspireResource>
            {
                ["api"] = new() { Type = "project.v0", Path = "Api/Api.csproj" },
                ["cfg"] = new() { Type = "parameter.v0" },
                ["sec"] = new() { Type = "value.v0" }
            }
        };
        var config = new EnvironmentConfig { DeployDapr = true };
        var result = manifest.WithDaprIfEnabled(config);
        result.Resources["api"].HasDaprSidecar.ShouldBeTrue();
        result.Resources["cfg"].HasDaprSidecar.ShouldBeFalse();
        result.Resources["sec"].HasDaprSidecar.ShouldBeFalse();
    }

    [Fact]
    public void WithDaprIfEnabled_DoesNotOverride_ExistingAnnotations()
    {
        var manifest = new AspireManifest
        {
            Resources = new Dictionary<string, AspireResource>
            {
                ["api"] = new()
                {
                    Type = "project.v0",
                    Path = "Api/Api.csproj",
                    Annotations = new Dictionary<string, string>
                    {
                        [DaprConstants.EnabledAnnotation] = "true",
                        [DaprConstants.AppIdAnnotation] = "my-custom-id"
                    }
                }
            }
        };
        var config = new EnvironmentConfig { DeployDapr = true };
        var result = manifest.WithDaprIfEnabled(config);
        result.Resources["api"].DaprAppId.ShouldBe("my-custom-id");
    }

    [Fact]
    public void WithDaprIfEnabled_IsIdempotent()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDapr = true };
        var once = manifest.WithDaprIfEnabled(config);
        var twice = once.WithDaprIfEnabled(config);
        // All resources already annotated, nothing changes
        twice.Resources.Count.ShouldBe(once.Resources.Count);
    }

    [Fact]
    public void WithDaprIfEnabled_FoldsDaprHttpPort_FromDaprV0Resource()
    {
        var manifest = new AspireManifest
        {
            Resources = new Dictionary<string, AspireResource>
            {
                ["weatherapi"] = new() { Type = "project.v0", Path = "src/Weather.Api/Weather.Api.csproj" },
                ["weatherapi-dapr"] = new()
                {
                    Type = "dapr.v0",
                    Dapr = new AspireDaprConfig
                    {
                        Application = "weatherapi", AppId = "weatherapi",
                        AppPort = 5160, DaprHttpPort = 50216, DaprGrpcPort = 50215
                    }
                }
            }
        };
        var config = new EnvironmentConfig { DeployDapr = false };
        var result = manifest.WithDaprIfEnabled(config);

        var api = result.Resources["weatherapi"];
        api.HasDaprSidecar.ShouldBeTrue();
        api.DaprAppId.ShouldBe("weatherapi");
        api.DaprAppPort.ShouldBe(5160);
        api.Annotations![DaprConstants.HttpPortAnnotation].ShouldBe("50216");
        api.Annotations![DaprConstants.GrpcPortAnnotation].ShouldBe("50215");
    }

    [Fact]
    public void HasDaprSidecar_ReturnsFalse_WhenNoAnnotations()
    {
        new AspireResource { Type = "project.v0" }.HasDaprSidecar.ShouldBeFalse();
    }

    [Fact]
    public void HasDaprSidecar_ReturnsFalse_WhenEnabledIsFalse()
    {
        var r = new AspireResource
        {
            Type = "project.v0",
            Annotations = new Dictionary<string, string> { [DaprConstants.EnabledAnnotation] = "false" }
        };
        r.HasDaprSidecar.ShouldBeFalse();
    }

    [Fact]
    public void HasDaprSidecar_ReturnsTrue_WhenEnabledIsTrue()
    {
        var r = new AspireResource
        {
            Type = "project.v0",
            Annotations = new Dictionary<string, string> { [DaprConstants.EnabledAnnotation] = "true" }
        };
        r.HasDaprSidecar.ShouldBeTrue();
    }

    [Fact]
    public void DaprAppPort_ReturnsNull_WhenNotAnnotated()
    {
        var r = new AspireResource
        {
            Type = "project.v0",
            Annotations = new Dictionary<string, string> { [DaprConstants.EnabledAnnotation] = "true" }
        };
        r.DaprAppPort.ShouldBeNull();
    }

    [Fact]
    public void DaprAppPort_ReturnsValue_WhenAnnotated()
    {
        var r = new AspireResource
        {
            Type = "project.v0",
            Annotations = new Dictionary<string, string>
            {
                [DaprConstants.EnabledAnnotation] = "true",
                [DaprConstants.AppPortAnnotation] = "8080"
            }
        };
        r.DaprAppPort.ShouldBe(8080);
    }

    // ── DaprConfig defaults ───────────────────────────────────────────────────

    [Fact]
    public void DaprConfig_DefaultDaprVersion_IsLatest()
        => new DaprConfig().DaprVersion.ShouldBe("latest");

    [Fact]
    public void DaprConfig_DefaultHttpPort_Is3500()
        => new DaprConfig().DaprHttpPort.ShouldBe(3500);

    [Fact]
    public void DaprConfig_DefaultGrpcPort_Is50001()
        => new DaprConfig().DaprGrpcPort.ShouldBe(50001);

    [Fact]
    public void DaprConfig_DefaultLogLevel_IsInfo()
        => new DaprConfig().LogLevel.ShouldBe("info");

    [Fact]
    public void EnvironmentConfig_DeployDapr_DefaultIsFalse()
        => new EnvironmentConfig().DeployDapr.ShouldBeFalse();

    [Fact]
    public void DaprConfig_RoundTrips_ThroughJson()
    {
        var state = new DeployifyStateFile
        {
            Environments =
            {
                ["local"] = new EnvironmentConfig
                {
                    DeployDapr = true,
                    Dapr = new DaprConfig
                    {
                        DaprVersion = "1.13",
                        DaprHttpPort = 3501,
                        DaprGrpcPort = 50002,
                        ComponentsPath = "/components",
                        LogLevel = "debug"
                    }
                }
            }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var back = System.Text.Json.JsonSerializer.Deserialize<DeployifyStateFile>(json)!;
        var cfg = back.Environments["local"];
        cfg.DeployDapr.ShouldBeTrue();
        cfg.Dapr.DaprVersion.ShouldBe("1.13");
        cfg.Dapr.DaprHttpPort.ShouldBe(3501);
        cfg.Dapr.DaprGrpcPort.ShouldBe(50002);
        cfg.Dapr.ComponentsPath.ShouldBe("/components");
        cfg.Dapr.LogLevel.ShouldBe("debug");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AspireManifest SimpleManifest() => new()
    {
        Resources = new Dictionary<string, AspireResource>
        {
            ["api"] = new()
            {
                Type = "project.v0",
                Path = "Api/Api.csproj",
                Bindings = new() { ["http"] = new() { Scheme = "http", ContainerPort = 8080 } }
            },
            ["worker"] = new() { Type = "project.v0", Path = "Worker/Worker.csproj" },
            ["redis"] = new() { Type = "container.v0", Image = "redis:7" }
        }
    };
}
