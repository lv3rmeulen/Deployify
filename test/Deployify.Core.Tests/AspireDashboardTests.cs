using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

public sealed class AspireDashboardTests
{
    // ── WithDashboardIfEnabled — disabled ──────────────────────────────────────

    [Fact]
    public void WithDashboardIfEnabled_ReturnsOriginal_WhenDisabled()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = false };

        var result = manifest.WithDashboardIfEnabled(config);

        result.ShouldBeSameAs(manifest);
    }

    [Fact]
    public void WithDashboardIfEnabled_DoesNotInjectDashboard_WhenDisabled()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = false };

        var result = manifest.WithDashboardIfEnabled(config);

        result.Resources.ContainsKey(AspireDashboard.ResourceName).ShouldBeFalse();
    }

    // ── WithDashboardIfEnabled — enabled ───────────────────────────────────────

    [Fact]
    public void WithDashboardIfEnabled_InjectsDashboardResource_WhenEnabled()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = true };

        var result = manifest.WithDashboardIfEnabled(config);

        result.Resources.ContainsKey(AspireDashboard.ResourceName).ShouldBeTrue();
    }

    [Fact]
    public void WithDashboardIfEnabled_OriginalServicesStillPresent_WhenEnabled()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = true };

        var result = manifest.WithDashboardIfEnabled(config);

        result.Resources.ContainsKey("api").ShouldBeTrue();
        result.Resources.ContainsKey("redis").ShouldBeTrue();
    }

    [Fact]
    public void WithDashboardIfEnabled_TotalResourceCount_IsOriginalPlusOne()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = true };

        var result = manifest.WithDashboardIfEnabled(config);

        result.Resources.Count.ShouldBe(manifest.Resources.Count + 1);
    }

    [Fact]
    public void WithDashboardIfEnabled_DashboardResource_IsContainerType()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = true };

        var result = manifest.WithDashboardIfEnabled(config);

        result.Resources[AspireDashboard.ResourceName].Type.ShouldBe(AspireDashboard.ResourceType);
    }

    [Fact]
    public void WithDashboardIfEnabled_DashboardResource_HasCorrectImage()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = true, Dashboard = new DashboardConfig { ImageTag = "9.0" } };

        var result = manifest.WithDashboardIfEnabled(config);

        var db = result.Resources[AspireDashboard.ResourceName];
        db.Image.ShouldBe($"{AspireDashboard.Image}:9.0");
    }

    [Fact]
    public void WithDashboardIfEnabled_DashboardResource_DefaultImageTag_IsLatest()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = true };

        var result = manifest.WithDashboardIfEnabled(config);

        var db = result.Resources[AspireDashboard.ResourceName];
        db.Image.ShouldBe($"{AspireDashboard.Image}:latest");
    }

    [Fact]
    public void WithDashboardIfEnabled_DashboardResource_HasHttpBinding()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = true };

        var result = manifest.WithDashboardIfEnabled(config);

        var db = result.Resources[AspireDashboard.ResourceName];
        db.Bindings.ShouldNotBeNull();
        db.Bindings!.ContainsKey("http").ShouldBeTrue();
    }

    [Fact]
    public void WithDashboardIfEnabled_DashboardResource_HasOtlpBinding()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = true };

        var result = manifest.WithDashboardIfEnabled(config);

        var db = result.Resources[AspireDashboard.ResourceName];
        db.Bindings!.ContainsKey("otlp").ShouldBeTrue();
    }

    [Fact]
    public void WithDashboardIfEnabled_DashboardResource_HttpBinding_UsesConfiguredPort()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = true, Dashboard = new DashboardConfig { Port = 19000 } };

        var result = manifest.WithDashboardIfEnabled(config);

        var db = result.Resources[AspireDashboard.ResourceName];
        db.Bindings!["http"].ContainerPort.ShouldBe(19000);
    }

    [Fact]
    public void WithDashboardIfEnabled_DashboardResource_OtlpBinding_UsesConfiguredPort()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = true, Dashboard = new DashboardConfig { OtlpPort = 19001 } };

        var result = manifest.WithDashboardIfEnabled(config);

        var db = result.Resources[AspireDashboard.ResourceName];
        db.Bindings!["otlp"].ContainerPort.ShouldBe(19001);
    }

    // ── Env vars ───────────────────────────────────────────────────────────────

    [Fact]
    public void WithDashboardIfEnabled_DashboardResource_HasOtlpEndpointEnvVar()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = true, Dashboard = new DashboardConfig { OtlpPort = 18889 } };

        var result = manifest.WithDashboardIfEnabled(config);

        var db = result.Resources[AspireDashboard.ResourceName];
        db.Env!["DASHBOARD__OTLP__ENDPOINT_URL"].ShouldBe("http://0.0.0.0:18889");
    }

    [Fact]
    public void WithDashboardIfEnabled_DashboardResource_HasFrontendEndpointEnvVar()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = true, Dashboard = new DashboardConfig { Port = 18888 } };

        var result = manifest.WithDashboardIfEnabled(config);

        var db = result.Resources[AspireDashboard.ResourceName];
        db.Env!["DASHBOARD__FRONTEND__ENDPOINT_URL"].ShouldBe("http://0.0.0.0:18888");
    }

    [Fact]
    public void WithDashboardIfEnabled_NoBrowserToken_EnvVars_DoNotContainAuthMode()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = true };

        var result = manifest.WithDashboardIfEnabled(config);

        var db = result.Resources[AspireDashboard.ResourceName];
        db.Env!.ContainsKey("DASHBOARD__FRONTEND__AUTHMODE").ShouldBeFalse();
        db.Env!.ContainsKey("DASHBOARD__FRONTEND__BROWSERTOKEN").ShouldBeFalse();
    }

    [Fact]
    public void WithDashboardIfEnabled_WithBrowserToken_InjectsAuthEnvVars()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig
        {
            DeployDashboard = true,
            Dashboard = new DashboardConfig { BrowserToken = "secret-token" }
        };

        var result = manifest.WithDashboardIfEnabled(config);

        var db = result.Resources[AspireDashboard.ResourceName];
        db.Env!["DASHBOARD__FRONTEND__AUTHMODE"].ShouldBe("BrowserToken");
        db.Env!["DASHBOARD__FRONTEND__BROWSERTOKEN"].ShouldBe("secret-token");
    }

    // ── Idempotency ────────────────────────────────────────────────────────────

    [Fact]
    public void WithDashboardIfEnabled_IsIdempotent_DoesNotDuplicateDashboard()
    {
        var manifest = SimpleManifest();
        var config = new EnvironmentConfig { DeployDashboard = true };

        var once = manifest.WithDashboardIfEnabled(config);
        var twice = once.WithDashboardIfEnabled(config);

        twice.Resources.Count(r => r.Key == AspireDashboard.ResourceName).ShouldBe(1);
    }

    // ── DashboardConfig defaults ───────────────────────────────────────────────

    [Fact]
    public void DashboardConfig_DefaultPort_Is18888()
    {
        new DashboardConfig().Port.ShouldBe(18888);
    }

    [Fact]
    public void DashboardConfig_DefaultOtlpPort_Is18889()
    {
        new DashboardConfig().OtlpPort.ShouldBe(18889);
    }

    [Fact]
    public void DashboardConfig_DefaultImageTag_IsLatest()
    {
        new DashboardConfig().ImageTag.ShouldBe("latest");
    }

    [Fact]
    public void DashboardConfig_DefaultBrowserToken_IsNull()
    {
        new DashboardConfig().BrowserToken.ShouldBeNull();
    }

    [Fact]
    public void EnvironmentConfig_DeployDashboard_DefaultIsFalse()
    {
        new EnvironmentConfig().DeployDashboard.ShouldBeFalse();
    }

    [Fact]
    public void DashboardConfig_RoundTrips_ThroughJson()
    {
        var state = new DeployifyStateFile
        {
            Environments =
            {
                ["local"] = new EnvironmentConfig
                {
                    DeployDashboard = true,
                    Dashboard = new DashboardConfig
                    {
                        ImageTag = "9.1",
                        Port = 19000,
                        OtlpPort = 19001,
                        BrowserToken = "tok"
                    }
                }
            }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var back = System.Text.Json.JsonSerializer.Deserialize<DeployifyStateFile>(json)!;
        var cfg = back.Environments["local"];
        cfg.DeployDashboard.ShouldBeTrue();
        cfg.Dashboard.ImageTag.ShouldBe("9.1");
        cfg.Dashboard.Port.ShouldBe(19000);
        cfg.Dashboard.OtlpPort.ShouldBe(19001);
        cfg.Dashboard.BrowserToken.ShouldBe("tok");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static AspireManifest SimpleManifest() => new()
    {
	    Resources = new Dictionary<string, AspireResource>
	    {
		    ["api"] = new() { Type = "project.v0", Path = "Api/Api.csproj" },
		    ["redis"] = new() { Type = "container.v0", Image = "redis:7" }
	    }
    };
}
