using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.AzureContainerApps.Tests;

public sealed class AcaStrategyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AcaDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLogger<AcaDeploymentTarget>.Instance) { Runner = fake };

    private static ProcessResult Ok(string stdout = "") => new(0, stdout, "");

    private static string App(string latest, string mode, params (string? Revision, bool Latest, int Weight)[] traffic)
        => new JsonObject
        {
            ["name"] = "api",
            ["properties"] = new JsonObject
            {
                ["latestRevisionName"] = latest,
                ["latestReadyRevisionName"] = latest,
                ["configuration"] = new JsonObject
                {
                    ["activeRevisionsMode"] = mode,
                    ["ingress"] = new JsonObject
                    {
                        ["fqdn"] = "api.happy-sea-1.westeurope.azurecontainerapps.io",
                        ["traffic"] = new JsonArray([.. traffic.Select(t => (JsonNode)new JsonObject
                        {
                            ["revisionName"] = t.Revision, ["latestRevision"] = t.Latest, ["weight"] = t.Weight
                        })])
                    }
                }
            }
        }.ToJsonString();

    private static string RevisionJson(bool active = true)
        => new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["active"] = active, ["provisioningState"] = "Provisioned", ["runningState"] = "Running",
                ["healthState"] = "Healthy", ["replicas"] = 1
            }
        }.ToJsonString();

    private static EnvironmentConfig Config(RolloutStrategyKind kind)
    {
        var config = AcaFixtures.AcaConfig();
        config.Rollout = new RolloutConfig { Strategy = kind };
        return config;
    }

    [Fact]
    public void Parse_ReadsModeFqdnAndTraffic()
    {
        var app = AcaAppStatus.Parse(App("api--v2", "Multiple", ("api--v1", false, 90), ("api--v2", false, 10)));

        app.MultipleRevisions.ShouldBeTrue();
        app.WeightOf("api--v1").ShouldBe(90);
        app.WeightOf("api--v2").ShouldBe(10);
        app.Fqdn.ShouldStartWith("api.");
    }

    [Fact]
    public void LatestRevisionEntry_CountsForTheLatestRevision()
        => AcaAppStatus.Parse(App("api--v2", "Multiple", (null, true, 100))).WeightOf("api--v2").ShouldBe(100);

    [Fact]
    public void MultipleMode_HealthyOnlyWithAllTheTraffic()
    {
        var revision = new AcaRevisionStatus(true, "Provisioned", "Running", "Healthy", 1, null);

        AcaRolloutHealth.Evaluate(AcaAppStatus.Parse(App("api--v2", "Multiple", ("api--v1", false, 100))), revision, "api--v1")
            .State.ShouldBe(HealthState.Healthy);
        AcaRolloutHealth.Evaluate(AcaAppStatus.Parse(App("api--v2", "Multiple", ("api--v1", false, 50), ("api--v2", false, 50))), revision, "api--v1")
            .State.ShouldBe(HealthState.Progressing);
    }

    [Fact]
    public void Candidate_IsHealthyWithoutTraffic()
    {
        var app = AcaAppStatus.Parse(App("api--v2", "Multiple", ("api--v1", false, 100)));

        AcaRolloutHealth.EvaluateCandidate(app, new AcaRevisionStatus(true, "Provisioned", "Running", "Healthy", 1, null), "api--v2")
            .State.ShouldBe(HealthState.Healthy);
        AcaRolloutHealth.EvaluateCandidate(app, new AcaRevisionStatus(true, "Provisioned", "Running", "Unhealthy", 1, "probe failed"), "api--v2")
            .State.ShouldBe(HealthState.Failed);
    }

    [Fact]
    public void LabelUrl_IsTheTripleDashHost()
    {
        AcaDeploymentTarget.LabelUrl("api.happy-sea-1.westeurope.azurecontainerapps.io", "api", "candidate")
            .ShouldBe("https://api---candidate.happy-sea-1.westeurope.azurecontainerapps.io");
        AcaDeploymentTarget.LabelUrl(null, "api", "candidate").ShouldBeNull();
    }

    [Fact]
    public async Task Generator_ProgressiveApp_RunsMultipleRevisions_WithAllTrafficOnTheLatest()
    {
        var dir = Directory.CreateTempSubdirectory("deployify-aca-");
        var config = Config(RolloutStrategyKind.Canary);
        var gen = new AcaManifestGenerator();
        var app = (await gen.GenerateAsync(AcaFixtures.SingleService(external: true), config, new AcaTargetConfig(), Ct)).Single();

        var path = await gen.WriteAppSpecAsync(app, config, new AcaTargetConfig(), dir.FullName, Ct);

        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!["properties"]!["configuration"]!;
        ((string)configuration["activeRevisionsMode"]!).ShouldBe("Multiple");
        var traffic = configuration["ingress"]!["traffic"]!.AsArray().Single()!;
        ((bool)traffic["latestRevision"]!).ShouldBeTrue();
        ((int)traffic["weight"]!).ShouldBe(100);
    }

    [Fact]
    public async Task Generator_RollingApp_SaysSingleRevision()
    {
        var dir = Directory.CreateTempSubdirectory("deployify-aca-");
        var config = AcaFixtures.AcaConfig();
        var gen = new AcaManifestGenerator();
        var app = (await gen.GenerateAsync(AcaFixtures.SingleService(external: true), config, new AcaTargetConfig(), Ct)).Single();

        var path = await gen.WriteAppSpecAsync(app, config, new AcaTargetConfig(), dir.FullName, Ct);

        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!["properties"]!["configuration"]!;
        ((string)configuration["activeRevisionsMode"]!).ShouldBe("Single");
        configuration["ingress"]!.AsObject().ContainsKey("traffic").ShouldBeFalse();
    }

    [Fact]
    public async Task CandidateSpec_PinsAllTrafficToTheRunningRevision()
    {
        var dir = Directory.CreateTempSubdirectory("deployify-aca-");
        var spec = Path.Combine(dir.FullName, "api.containerapp.json");
        await File.WriteAllTextAsync(spec, """{"properties":{"configuration":{"ingress":{"traffic":[{"latestRevision":true,"weight":100}]}}}}""", Ct);

        var path = await AcaDeploymentTarget.WriteCandidateSpecAsync(spec, "api", "api--v1", Ct);
        try
        {
            var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!["properties"]!["configuration"]!;
            ((string)configuration["activeRevisionsMode"]!).ShouldBe("Multiple");
            var traffic = configuration["ingress"]!["traffic"]!.AsArray().Single()!;
            ((string)traffic["revisionName"]!).ShouldBe("api--v1");
            ((int)traffic["weight"]!).ShouldBe(100);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NoIngress_RefusesStrategies()
    {
        var config = Config(RolloutStrategyKind.Canary);
        var manifest = new AspireManifest
        {
            Resources = new Dictionary<string, AspireResource> { ["worker"] = new() { Type = "project.v0", Path = "W/W.csproj" } }
        };

        var reason = await Target(new FakeProcessRunner()).GetStrategyUnsupportedReasonAsync(
            manifest, config, "worker", RolloutStrategies.For(config, "worker"), Ct);

        reason!.ShouldContain("HTTP ingress");
    }

    [Fact]
    public async Task Restore_DuringCanary_MovesTrafficBack_InsteadOfCopying()
    {
        var fake = new FakeProcessRunner()
            .When(["containerapp", "show"], Ok(App("api--v2", "Multiple", ("api--v1", false, 90), ("api--v2", false, 10))))
            .When(["revision", "show"], Ok(RevisionJson()));

        var restored = await Target(fake).RestoreAsync(AcaFixtures.AcaConfig(),
            new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = "api--v1" }, Ct);

        restored.ShouldBe("api--v1");
        fake.Calls.ShouldContain(c => c.Args.Contains("traffic") && c.Args.Contains("api--v1=100"));
        fake.Calls.ShouldContain(c => c.Args.Contains("deactivate") && c.Args.Contains("api--v2"));
        fake.Calls.ShouldNotContain(c => c.Args.Contains("copy"));
    }
}
