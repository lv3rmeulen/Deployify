using System;
using System.Linq;
using System.Text.Json;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Plugin.Contracts;
using Shouldly;
using Xunit;

namespace Deployify.Plugins.Tests;

public sealed class WireMappingTests
{
    [Fact]
    public void WireEnums_MatchTheCoreEnums()
    {
        // The mapping casts between them, so names and order must stay in step.
        Enum.GetNames<DiffKindInfo>().ShouldBe(Enum.GetNames<DiffKind>());
        Enum.GetNames<HealthStateInfo>().ShouldBe(Enum.GetNames<HealthState>());
    }

    [Fact]
    public void Environment_LeavesExplicitParameterValuesOut()
    {
        var config = new EnvironmentConfig
        {
            Target = DeploymentTargetType.Parse("example"),
            ImageTag = "v3",
            Parameters = { ["db-password"] = "hunter2" },
            TargetConfig = { ["example"] = JsonSerializer.SerializeToElement(new { app = "shop" }) }
        };

        var wire = WireMapping.ToWire(config);

        wire.GetRawText().ShouldNotContain("hunter2");
        wire.TryGetProperty("Parameters", out _).ShouldBeFalse();
        var back = WireMapping.EnvironmentFromWire(wire);
        back.Target.Value.ShouldBe("example");
        back.ImageTag.ShouldBe("v3");
        ((JsonElement)back.TargetConfig["example"]!).GetProperty("app").GetString().ShouldBe("shop");
    }

    [Fact]
    public void Manifest_RoundTripsIncludingUnknownFields()
    {
        var json = """
            {
              "resources": {
                "api": { "type": "project.v0", "path": "api.csproj", "env": { "A": "1" },
                         "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8080 } },
                         "deployment": { "custom": true } },
                "cache": { "type": "container.v0", "image": "redis:7" }
              },
              "$schema": "https://json.schemastore.org/aspire-8.0.json"
            }
            """;
        var manifest = JsonSerializer.Deserialize<AspireManifest>(json)!;

        var back = WireMapping.ManifestFromWire(WireMapping.ToWire(manifest));

        back.Resources.Keys.ShouldBe(["api", "cache"], ignoreOrder: true);
        back.Resources["api"].Env!["A"].ShouldBe("1");
        back.Resources["api"].Bindings!["http"].TargetPort.ShouldBe(8080);
        back.Resources["api"].ExtensionData!["deployment"].GetProperty("custom").GetBoolean().ShouldBeTrue();
        back.Resources["cache"].Image.ShouldBe("redis:7");
        back.ExtensionData!.ShouldContainKey("$schema");
    }

    [Fact]
    public void Messages_UseCamelCase()
    {
        var json = JsonSerializer.Serialize(
            new HealthObservationInfo(HealthStateInfo.RevertedByPlatform, "breaker", "r1"), PluginProtocol.JsonOptions);
        json.ShouldBe("""{"state":"revertedByPlatform","detail":"breaker","revision":"r1"}""");
    }

    [Fact]
    public void Results_RoundTrip()
    {
        var plan = new PlanResult { ToCreate = ["a"], ToUpdate = ["b"], ToDelete = ["c"], Unchanged = ["d"], Unknown = [new PlanUnknown("e", "why")] };
        var planBack = WireMapping.FromWire(WireMapping.ToWire(plan));
        (planBack.ToCreate.Single(), planBack.ToUpdate.Single(), planBack.ToDelete.Single(), planBack.Unchanged.Single())
            .ShouldBe(("a", "b", "c", "d"));
        planBack.Unknown.ShouldBe([new PlanUnknown("e", "why")]);

        var snapshot = new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = "7", Data = { ["count"] = "3" } };
        var snapBack = WireMapping.FromWire(WireMapping.ToWire(snapshot));
        (snapBack.ServiceName, snapBack.Existed, snapBack.Revision, snapBack.Data["count"]).ShouldBe(("api", true, "7", "3"));
    }
}
