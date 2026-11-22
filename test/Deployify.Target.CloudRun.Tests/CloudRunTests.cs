using System;
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
using Deployify.Core.SecretBackends;
using ServiceLogs = Deployify.Core.ServiceLogs;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.CloudRun.Tests;

internal static class Fixtures
{
    /// <summary>A public web front end calling an internal API, and a secret the API reads.</summary>
    public static AspireManifest Manifest() => JsonSerializer.Deserialize<AspireManifest>("""
        {
          "resources": {
            "api-key": {
              "type": "parameter.v0",
              "value": "{api-key.inputs.value}",
              "inputs": { "value": { "type": "string", "secret": true } }
            },
            "api": {
              "type": "project.v0",
              "path": "Api/Api.csproj",
              "env": { "HTTP_PORTS": "{api.bindings.http.targetPort}", "PORT": "1234", "API_KEY": "{api-key.value}" },
              "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8080 } }
            },
            "web": {
              "type": "project.v0",
              "path": "Web/Web.csproj",
              "env": { "services__api__http__0": "{api.bindings.http.url}" },
              "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8081, "external": true } },
              "references": [ { "name": "api" } ]
            }
          }
        }
        """)!;

    public static EnvironmentConfig Config(SecretBackendType backend = SecretBackendType.GcpSecretManager, string? projectNumber = "123456")
    {
        var config = new EnvironmentConfig
        {
            Name = "prod",
            Target = DeploymentTargetType.CloudRun,
            ContainerRegistry = "europe-docker.pkg.dev/shop/apps",
            ImageTag = "v2"
        };
        config.TargetConfig["cloudrun"] = JsonSerializer.SerializeToElement(new
        {
            projectId = "shop-prod", projectNumber, region = "europe-west1", maxInstances = 5
        });
        config.Secrets.Backend = backend;
        config.Parameters["api-key"] = "s3cret-key";
        return config;
    }

    public static CloudRunDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLogger<CloudRunDeploymentTarget>.Instance) { Runner = fake };

    public static ProcessResult Ok(string stdout = "") => new(0, stdout, "");

    public static string Service(
        long generation, long observed, string? created, string? ready, string readyStatus = "True",
        params (string? Revision, bool Latest, int Percent, string? Tag)[] traffic)
        => new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "api", ["generation"] = generation },
            ["status"] = new JsonObject
            {
                ["observedGeneration"] = observed,
                ["latestCreatedRevisionName"] = created,
                ["latestReadyRevisionName"] = ready,
                ["url"] = "https://api-123456.europe-west1.run.app",
                ["conditions"] = new JsonArray(new JsonObject { ["type"] = "Ready", ["status"] = readyStatus, ["message"] = "msg" }),
                ["traffic"] = new JsonArray([.. traffic.Select(t => (JsonNode)new JsonObject
                {
                    ["revisionName"] = t.Revision, ["latestRevision"] = t.Latest, ["percent"] = t.Percent, ["tag"] = t.Tag
                })])
            }
        }.ToJsonString();

    public static string Revision(string name, string status)
        => new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = name },
            ["status"] = new JsonObject { ["conditions"] = new JsonArray(new JsonObject { ["type"] = "Ready", ["status"] = status }) }
        }.ToJsonString();
}

public sealed class CloudRunGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IReadOnlyList<CloudRunService>> GenerateAsync(EnvironmentConfig config, string? projectNumber = "123456")
        => await new CloudRunManifestGenerator().GenerateAsync(Fixtures.Manifest(), config,
            CloudRunDeploymentTarget.GetConfig(config), projectNumber, Ct);

    [Fact]
    public async Task Services_ReachEachOtherAtTheirRunAppAddress()
    {
        var web = (await GenerateAsync(Fixtures.Config())).Single(s => s.Name == "web");

        web.Env["services__api__http__0"].ShouldBe("https://api-123456.europe-west1.run.app");
        web.External.ShouldBeTrue();
        web.MaxInstances.ShouldBe(5);
    }

    [Fact]
    public async Task Spec_IsAKnativeService_WithSecretReferences_AndAllTrafficOnTheLatestRevision()
    {
        var config = Fixtures.Config();
        var api = (await GenerateAsync(config)).Single(s => s.Name == "api");

        var spec = CloudRunManifestGenerator.BuildSpec(api, CloudRunDeploymentTarget.GetConfig(config), "deployify-prod");

        ((string)spec["apiVersion"]!).ShouldBe("serving.knative.dev/v1");
        ((string)spec["metadata"]!["annotations"]!["run.googleapis.com/ingress"]!).ShouldBe("internal");
        ((string)spec["metadata"]!["labels"]!["deployify-env"]!).ShouldBe("prod");
        spec["metadata"]!["labels"]!["deployify-spec"]!.GetValue<string>().Length.ShouldBe(16);
        var container = spec["spec"]!["template"]!["spec"]!["containers"]![0]!;
        ((string)container["image"]!).ShouldBe("europe-docker.pkg.dev/shop/apps/api:v2");
        ((int)container["ports"]![0]!["containerPort"]!).ShouldBe(8080);
        var env = container["env"]!.AsArray().ToDictionary(e => (string)e!["name"]!, e => e!);
        env.ShouldNotContainKey("PORT"); // Cloud Run sets it
        ((string)env["HTTP_PORTS"]["value"]!).ShouldBe("8080");
        var secret = env["API_KEY"]["valueFrom"]!["secretKeyRef"]!;
        ((string)secret["name"]!).ShouldBe("deployify-prod-api-API_KEY");
        ((string)secret["key"]!).ShouldBe("latest");
        spec.ToJsonString().ShouldNotContain("s3cret-key");
        var traffic = spec["spec"]!["traffic"]![0]!;
        ((bool)traffic["latestRevision"]!).ShouldBeTrue();
        ((int)traffic["percent"]!).ShouldBe(100);
    }

    [Fact]
    public void ForDeploy_PinsSecretVersions_AndKeepsTrafficOnTheStableRevision()
    {
        var spec = JsonNode.Parse("""
            {"spec":{"template":{"spec":{"containers":[{"env":[{"name":"API_KEY","valueFrom":{"secretKeyRef":{"name":"s1","key":"latest"}}}]}]}},
             "traffic":[{"latestRevision":true,"percent":100}]}}
            """)!.AsObject();

        var deploy = CloudRunManifestGenerator.ForDeploy(spec, new Dictionary<string, string> { ["s1"] = "7" }, "api-00003-abc");

        ((string)deploy["spec"]!["template"]!["spec"]!["containers"]![0]!["env"]![0]!["valueFrom"]!["secretKeyRef"]!["key"]!).ShouldBe("7");
        var traffic = deploy["spec"]!["traffic"]!.AsArray();
        ((string)traffic[0]!["revisionName"]!).ShouldBe("api-00003-abc");
        ((int)traffic[0]!["percent"]!).ShouldBe(100);
        ((string)traffic[1]!["tag"]!).ShouldBe("candidate");
        ((int)traffic[1]!["percent"]!).ShouldBe(0);
        // The plan's file is untouched.
        ((string)spec["spec"]!["template"]!["spec"]!["containers"]![0]!["env"]![0]!["valueFrom"]!["secretKeyRef"]!["key"]!).ShouldBe("latest");
    }

    [Fact]
    public async Task NonHttpResources_AreRefused_UnlessIgnored()
    {
        var manifest = Fixtures.Manifest();
        manifest.Resources["redis"] = new AspireResource
        {
            Type = "container.v0", Image = "redis:7",
            Bindings = new Dictionary<string, AspireBinding> { ["tcp"] = new() { ContainerPort = 6379, Protocol = "tcp" } }
        };
        var config = Fixtures.Config();
        var gen = new CloudRunManifestGenerator();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            gen.GenerateAsync(manifest, config, CloudRunDeploymentTarget.GetConfig(config), "123456", Ct));
        ex.Message.ShouldContain("redis");

        config.IgnoreResources.Add("redis");
        (await gen.GenerateAsync(manifest, config, CloudRunDeploymentTarget.GetConfig(config), "123456", Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task UnknownProjectNumber_IsAnErrorOnlyWhenAnAddressIsNeeded()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => GenerateAsync(Fixtures.Config(projectNumber: null), projectNumber: null));
        ex.Message.ShouldContain("projectNumber");
    }

    [Fact]
    public async Task SecretsWithoutSecretManager_AreRefused()
    {
        var config = Fixtures.Config(SecretBackendType.Inline);
        var api = (await GenerateAsync(config)).Single(s => s.Name == "api");
        var dir = Directory.CreateTempSubdirectory("deployify-cloudrun-");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => new CloudRunManifestGenerator().WriteSpecAsync(
            api, config, CloudRunDeploymentTarget.GetConfig(config), "p", dir.FullName, Ct));
        ex.Message.ShouldContain("GcpSecretManager");
    }

    [Theory]
    [InlineData("Prod_EU.1", "prod_eu-1")]
    [InlineData("staging", "staging")]
    public void LabelValues_AreLowercaseAndSafe(string value, string label)
        => CloudRunManifestGenerator.LabelValue(value).ShouldBe(label);
}

public sealed class CloudRunHealthTests
{
    [Fact]
    public void Generation_RolledOutAndServing_IsHealthy()
    {
        var status = CloudRunServiceStatus.Parse(Fixtures.Service(4, 4, "api-4", "api-4", "True", (null, true, 100, null)));

        CloudRunHealth.Evaluate(status, 4).State.ShouldBe(HealthState.Healthy);
    }

    [Fact]
    public void Generation_NotObservedYet_IsProgressing()
        => CloudRunHealth.Evaluate(CloudRunServiceStatus.Parse(Fixtures.Service(4, 3, "api-3", "api-3")), 4).State
            .ShouldBe(HealthState.Progressing);

    [Fact]
    public void Generation_ReadyFalse_IsFailed()
        => CloudRunHealth.Evaluate(CloudRunServiceStatus.Parse(Fixtures.Service(4, 4, "api-4", "api-3", "False")), 4).State
            .ShouldBe(HealthState.Failed);

    [Fact]
    public void Generation_ChangedAgain_IsFailed()
        => CloudRunHealth.Evaluate(CloudRunServiceStatus.Parse(Fixtures.Service(5, 5, "api-5", "api-5")), 4).State
            .ShouldBe(HealthState.Failed);

    [Fact]
    public void Revision_NeedsAllTheTraffic()
    {
        var revision = CloudRunRevisionStatus.Parse(Fixtures.Revision("api-3", "True"));

        CloudRunHealth.EvaluateRevision(CloudRunServiceStatus.Parse(Fixtures.Service(5, 5, "api-4", "api-4", "True",
            ("api-3", false, 100, null))), revision, "api-3").State.ShouldBe(HealthState.Healthy);
        CloudRunHealth.EvaluateRevision(CloudRunServiceStatus.Parse(Fixtures.Service(5, 5, "api-4", "api-4", "True",
            ("api-3", false, 90, null), ("api-4", false, 10, null))), revision, "api-3").State.ShouldBe(HealthState.Progressing);
    }

    [Fact]
    public void Candidate_IsJudgedByItsRevision_WithoutTraffic()
    {
        var status = CloudRunServiceStatus.Parse(Fixtures.Service(6, 6, "api-4", "api-3", "True", ("api-3", false, 100, null)));

        CloudRunHealth.EvaluateCandidate(status, CloudRunRevisionStatus.Parse(Fixtures.Revision("api-4", "True")), 6, "api-3")
            .State.ShouldBe(HealthState.Healthy);
        CloudRunHealth.EvaluateCandidate(status, CloudRunRevisionStatus.Parse(Fixtures.Revision("api-4", "False")), 6, "api-3")
            .State.ShouldBe(HealthState.Failed);
        CloudRunHealth.EvaluateCandidate(status, null, 7, "api-3").State.ShouldBe(HealthState.Progressing);
    }

    [Fact]
    public void Serving_IsTheRevisionWithAllTheTraffic()
    {
        CloudRunServiceStatus.Parse(Fixtures.Service(1, 1, "api-2", "api-2", "True", ("api-2", true, 100, null))).Serving.ShouldBe("api-2");
        CloudRunServiceStatus.Parse(Fixtures.Service(1, 1, "api-2", "api-2", "True", ("api-1", false, 50, null), ("api-2", false, 50, null)))
            .Serving.ShouldBeNull();
    }
}

public sealed class CloudRunTargetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> PlanAsync(CloudRunDeploymentTarget target, EnvironmentConfig config)
    {
        var dir = Directory.CreateTempSubdirectory("deployify-cloudrun-").FullName;
        await target.GenerateAsync(Fixtures.Manifest(), config, dir, Ct);
        return dir;
    }

    [Fact]
    public async Task ApplyService_ReplacesAsync_WithPinnedSecrets_AndReturnsTheGeneration()
    {
        string? deployed = null;
        var fake = new FakeProcessRunner()
            .When(["secrets", "versions", "access"], Fixtures.Ok("old-value"))
            .When(["secrets", "describe"], Fixtures.Ok("projects/1/secrets/x"))
            .When(["secrets", "versions", "add"], Fixtures.Ok("projects/1/secrets/deployify-prod-api-API_KEY/versions/3"))
            .When(s => s.Args.Contains("replace"), s =>
            {
                deployed = File.ReadAllText(s.Args[s.Args.ToList().IndexOf("replace") + 1]);
                return Fixtures.Ok(Fixtures.Service(9, 8, "api-8", "api-8"));
            });
        var target = Fixtures.Target(fake);
        var config = Fixtures.Config();
        var dir = await PlanAsync(target, config);

        var applied = await target.ApplyServiceAsync(Fixtures.Manifest(), config, dir, "api", Ct);

        applied.Success.ShouldBeTrue(applied.Error);
        applied.Revision.ShouldBe("9");
        fake.Calls.Single(c => c.Args.Contains("replace")).Args.ShouldContain("--async");
        deployed!.ShouldContain("\"key\":\"3\"");
        fake.Calls.ShouldContain(c => c.Args.Contains("add-iam-policy-binding") && c.Args.Contains("allUsers"));
        config.Secrets.References["api/API_KEY"].Version.ShouldBe("3");
        fake.Calls.Single(c => c.Args.Contains("add") && c.Args.Contains("versions")).Stdin.ShouldBe("s3cret-key");
    }

    [Fact]
    public async Task UnchangedSecret_ReusesItsVersion()
    {
        var fake = new FakeProcessRunner()
            .When(["secrets", "versions", "access"], Fixtures.Ok("s3cret-key"))
            .When(["secrets", "versions", "describe"], Fixtures.Ok("projects/1/secrets/x/versions/2"))
            .When(s => s.Args.Contains("replace"), _ => Fixtures.Ok(Fixtures.Service(9, 8, "api-8", "api-8")));
        var target = Fixtures.Target(fake);
        var config = Fixtures.Config();

        await target.ApplyServiceAsync(Fixtures.Manifest(), config, await PlanAsync(target, config), "api", Ct);

        fake.Calls.ShouldNotContain(c => c.Args.Contains("add") && c.Args.Contains("versions"));
        config.Secrets.References["api/API_KEY"].Version.ShouldBe("2");
    }

    [Fact]
    public async Task Candidate_StartsWithoutTraffic_ThenShiftsByExactPercentages()
    {
        string? deployed = null;
        var fake = new FakeProcessRunner()
            .When(["secrets", "versions", "access"], Fixtures.Ok("s3cret-key"))
            .When(["secrets", "versions", "describe"], Fixtures.Ok("projects/1/secrets/x/versions/2"))
            .When(s => s.Args.Contains("replace"), s =>
            {
                deployed = File.ReadAllText(s.Args[s.Args.ToList().IndexOf("replace") + 1]);
                return Fixtures.Ok(Fixtures.Service(6, 5, "api-3", "api-3"));
            })
            .When(["services", "describe"], Fixtures.Ok(Fixtures.Service(6, 6, "api-4", "api-3", "True", ("api-3", false, 100, null))));
        var target = Fixtures.Target(fake);
        var config = Fixtures.Config();
        var strategy = RolloutStrategies.For(new EnvironmentConfig { Rollout = new RolloutConfig { Strategy = RolloutStrategyKind.Canary } }, "api");

        var started = await target.StartCandidateAsync(Fixtures.Manifest(), config, await PlanAsync(target, config), "api", strategy,
            new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = "api-3" }, Ct);
        var share = await target.ShiftTrafficAsync(config, "api", started.Revision!, 10, Ct);

        started.Success.ShouldBeTrue(started.Error);
        started.PreviewUrl.ShouldBe("https://candidate---api-123456.europe-west1.run.app");
        deployed!.ShouldContain("\"revisionName\":\"api-3\",\"percent\":100");
        deployed!.ShouldContain("\"tag\":\"candidate\"");
        share.ShouldBe(10);
        fake.Calls.ShouldContain(c => c.Args.Contains("update-traffic") && c.Args.Contains("api-3=90,api-4=10"));
    }

    [Fact]
    public async Task Restore_SendsAllTrafficBack_AndDropsTheCandidateTag()
    {
        var fake = new FakeProcessRunner()
            .When(["services", "describe"], Fixtures.Ok(Fixtures.Service(6, 6, "api-4", "api-4", "True",
                ("api-3", false, 90, null), ("api-4", false, 10, "candidate"))));

        var restored = await Fixtures.Target(fake).RestoreAsync(Fixtures.Config(),
            new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = "api-3" }, Ct);

        restored.ShouldBe("api-3");
        fake.Calls.ShouldContain(c => c.Args.Contains("--to-revisions") && c.Args.Contains("api-3=100"));
        fake.Calls.ShouldContain(c => c.Args.Contains("--remove-tags") && c.Args.Contains("candidate"));
    }

    [Fact]
    public async Task Restore_OfANewService_DeletesIt()
    {
        var fake = new FakeProcessRunner().When(["services", "describe"], Fixtures.Ok(Fixtures.Service(1, 1, "api-1", "api-1")));

        (await Fixtures.Target(fake).RestoreAsync(Fixtures.Config(), new ServiceSnapshot { ServiceName = "api", Existed = false }, Ct))
            .ShouldBeNull();

        fake.Calls.ShouldContain(c => c.Args.Contains("delete") && c.Args.Contains("api"));
    }

    [Fact]
    public async Task Diff_ComparesTheSpecHash()
    {
        var config = Fixtures.Config();
        var api = (await new CloudRunManifestGenerator().GenerateAsync(Fixtures.Manifest(), config,
            CloudRunDeploymentTarget.GetConfig(config), "123456", Ct)).Single(s => s.Name == "api");
        var hash = (string)CloudRunManifestGenerator.BuildSpec(api, CloudRunDeploymentTarget.GetConfig(config), "deployify-prod")["metadata"]!["labels"]!["deployify-spec"]!;
        var live = $$"""
            [ { "metadata": { "name": "api", "labels": { "deployify-spec": "{{hash}}", "deployify-env": "prod" } } },
              { "metadata": { "name": "old", "labels": { "deployify-env": "prod" } } },
              { "metadata": { "name": "theirs", "labels": { "deployify-env": "staging" } } } ]
            """;
        var fake = new FakeProcessRunner().When(["services", "list"], Fixtures.Ok(live));

        var diff = await Fixtures.Target(fake).DiffAsync(Fixtures.Manifest(), config, "out", Ct);

        diff.Single(d => d.ServiceName == "api").Kind.ShouldBe(DiffKind.Unchanged);
        diff.Single(d => d.ServiceName == "web").Kind.ShouldBe(DiffKind.Added);
        diff.Single(d => d.ServiceName == "old").Kind.ShouldBe(DiffKind.Removed);
        diff.ShouldNotContain(d => d.ServiceName == "theirs");
    }

    [Fact]
    public void TagUrl_PutsTheTagInFrontOfTheHost()
        => CloudRunDeploymentTarget.TagUrl("https://api-123456.europe-west1.run.app")
            .ShouldBe("https://candidate---api-123456.europe-west1.run.app");

    [Fact]
    public async Task GeneratedValues_LiveInOneSecret()
    {
        var fake = new FakeProcessRunner()
            .When(["secrets", "versions", "access"], new ProcessResult(1, "", "NOT_FOUND: Secret not found"))
            .When(["secrets", "describe"], new ProcessResult(1, "", "NOT_FOUND"))
            .When(["secrets", "versions", "add"], Fixtures.Ok("projects/1/secrets/deployify-prod-generated/versions/1"));
        var store = Fixtures.Target(fake).CreateSecretStore(Fixtures.Config(), NullLogger.Instance);

        (await store.LoadAsync(Ct)).ShouldBeEmpty();
        await store.SaveAsync(new Dictionary<string, SensitiveValue> { ["db-password.value"] = new("pw") }, ["db-password.value"], Ct);

        fake.Calls.ShouldContain(c => c.Args.Contains("create") && c.Args.Contains("deployify-prod-generated"));
        fake.Calls.Single(c => c.Args.Contains("add")).Stdin.ShouldBe("""{"db-password.value":"pw"}""");
    }
}

public sealed class CloudRunLogsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Entries = """
        [
          { "textPayload": "second\n", "timestamp": "2026-11-22T10:00:02Z", "labels": { "instanceId": "0069c7a9881234567890abcd" },
            "logName": "projects/p/logs/run.googleapis.com%2Fstderr" },
          { "jsonPayload": { "message": "first" }, "timestamp": "2026-11-22T10:00:01Z",
            "logName": "projects/p/logs/run.googleapis.com%2Fstdout" }
        ]
        """;

    [Fact]
    public async Task Tail_ReadsStdoutAndStderr_OldestFirst()
    {
        var fake = new FakeProcessRunner()
            .When(["--version"], Fixtures.Ok("Google Cloud SDK 550.0.0"))
            .When(["services", "describe"], Fixtures.Ok(Fixtures.Service(1, 1, "api-1", "api-1")))
            .When(["logging", "read"], Fixtures.Ok(Entries));

        var lines = new List<ServiceLogs.ServiceLogLine>();
        await foreach (var line in Fixtures.Target(fake).ReadLogsAsync(Fixtures.Config(), new ServiceLogs.ServiceLogQuery { Service = "api", Tail = 50 }, Ct))
        {
            lines.Add(line);
        }

        lines.Select(l => l.Text).ShouldBe(["first", "second"]);
        lines[1].IsError.ShouldBeTrue();
        lines[1].Instance.ShouldBe("34567890abcd");
        var read = fake.Calls.Single(c => c.Args.Contains("logging")).Args.ToList();
        read[read.IndexOf("--limit") + 1].ShouldBe("50");
        read[2].ShouldContain("resource.labels.service_name=\"api\"");
        read[2].ShouldContain("log_id(\"run.googleapis.com/stdout\")");
    }

    [Fact]
    public async Task AServiceThatIsntDeployed_IsAnError()
    {
        var fake = new FakeProcessRunner()
            .When(["--version"], Fixtures.Ok("Google Cloud SDK 550.0.0"))
            .When(["services", "describe"], new ProcessResult(1, "", "ERROR: Service [api] could not be found."));

        await Should.ThrowAsync<ServiceLogs.ServiceLogsException>(async () =>
        {
            await foreach (var _ in Fixtures.Target(fake).ReadLogsAsync(Fixtures.Config(), new ServiceLogs.ServiceLogQuery { Service = "api" }, Ct))
            {
            }
        });
    }

    [Fact]
    public void Since_IsATimestampFilter()
        => CloudRunLogs.Filter("api", "europe-west1", new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero))
            .ShouldEndWith("timestamp>=\"2026-11-22T10:00:00.0000000Z\"");
}
