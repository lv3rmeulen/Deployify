using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.AzureContainerApps.Tests;

public sealed class AcaRolloutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AcaDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLogger<AcaDeploymentTarget>.Instance) { Runner = fake };

    private static ProcessResult Ok(string stdout) => new(0, stdout, "");

    private static readonly ProcessResult NotFound =
        new(3, "", "(ResourceNotFound) The Resource 'Microsoft.App/containerApps/api' was not found.");

    private static string App(string latest, string? ready)
        => new JsonObject
        {
            ["name"] = "api",
            ["properties"] = new JsonObject { ["latestRevisionName"] = latest, ["latestReadyRevisionName"] = ready }
        }.ToJsonString();

    private static AcaRevisionStatus Revision(string provisioning, string running, string health = "Healthy",
        bool active = true, string? error = null)
        => new(active, provisioning, running, health, 1, error);

    // ── Health evaluation ────────────────────────────────────────────────────

    [Fact]
    public void ProvisionedRunningAndReady_IsHealthy()
    {
        var obs = AcaRolloutHealth.Evaluate(new AcaAppStatus("api--v2", "api--v2"), Revision("Provisioned", "Running"), "api--v2");

        obs.State.ShouldBe(HealthState.Healthy);
        obs.Detail.ShouldBe("Running, health Healthy, 1 replica(s)");
    }

    [Fact]
    public void ScaledToZero_CountsAsRunning()
        => AcaRolloutHealth.Evaluate(new AcaAppStatus("api--v2", "api--v2"),
                Revision("Provisioned", "ScaledToZero", "None"), "api--v2")
            .State.ShouldBe(HealthState.Healthy);

    [Fact]
    public void NotYetTheLatestReadyRevision_IsProgressing()
        => AcaRolloutHealth.Evaluate(new AcaAppStatus("api--v2", "api--v1"), Revision("Provisioned", "Running"), "api--v2")
            .State.ShouldBe(HealthState.Progressing);

    [Fact]
    public void StillProvisioning_IsProgressing()
        => AcaRolloutHealth.Evaluate(new AcaAppStatus("api--v2", "api--v1"), Revision("Provisioning", "Activating"), "api--v2")
            .State.ShouldBe(HealthState.Progressing);

    [Fact]
    public void FailedRevision_IsFailed_WithAzuresReason()
    {
        var obs = AcaRolloutHealth.Evaluate(new AcaAppStatus("api--v2", "api--v1"),
            Revision("Failed", "Failed", error: "Container crashed: exit code 1"), "api--v2");

        obs.State.ShouldBe(HealthState.Failed);
        obs.Detail.ShouldBe("Container crashed: exit code 1");
    }

    [Fact]
    public void ReplacedByANewerRevision_IsFailed()
        => AcaRolloutHealth.Evaluate(new AcaAppStatus("api--v3", "api--v3"), Revision("Provisioned", "Running"), "api--v2")
            .State.ShouldBe(HealthState.Failed);

    [Fact]
    public void RevisionJsonIsParsed()
    {
        var json = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["active"] = true, ["provisioningState"] = "Provisioned", ["runningState"] = "Running",
                ["healthState"] = "Healthy", ["replicas"] = 2
            }
        }.ToJsonString();

        AcaRevisionStatus.Parse(json).ShouldBe(new AcaRevisionStatus(true, "Provisioned", "Running", "Healthy", 2, null));
    }

    // ── Capture / restore through the az CLI ─────────────────────────────────

    [Fact]
    public async Task Capture_RecordsTheRevisionServingTraffic()
    {
        var fake = new FakeProcessRunner().When(["containerapp", "show"], Ok(App("api--v2", "api--v1")));

        var snap = await Target(fake).CaptureAsync(AcaFixtures.SingleService("api"), AcaFixtures.AcaConfig(), "api", Ct);

        snap.Existed.ShouldBeTrue();
        snap.Revision.ShouldBe("api--v1");
    }

    [Fact]
    public async Task Capture_MissingApp_IsNew()
    {
        var fake = new FakeProcessRunner().When(["containerapp", "show"], NotFound);

        var snap = await Target(fake).CaptureAsync(AcaFixtures.SingleService("api"), AcaFixtures.AcaConfig(), "api", Ct);

        snap.Existed.ShouldBeFalse();
    }

    [Fact]
    public async Task Capture_WhenAzureCannotBeReached_Throws()
    {
        var fake = new FakeProcessRunner()
            .When(["containerapp", "show"], new ProcessResult(1, "", "Please run 'az login' to setup account."));

        await Should.ThrowAsync<System.InvalidOperationException>(
            () => Target(fake).CaptureAsync(AcaFixtures.SingleService("api"), AcaFixtures.AcaConfig(), "api", Ct));
    }

    [Fact]
    public async Task Restore_CopiesThePreviousRevision_AndWaitsForTheCopy()
    {
        var shows = 0;
        var fake = new FakeProcessRunner()
            .When(spec => spec.Args.Contains("containerapp") && spec.Args.Contains("show") && !spec.Args.Contains("revision"),
                _ => Ok(++shows == 1 ? App("api--v2", "api--v1") : App("api--v3", "api--v1")));
        var snapshot = new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = "api--v1" };

        var waitFor = await Target(fake).RestoreAsync(AcaFixtures.AcaConfig(), snapshot, Ct);

        waitFor.ShouldBe("api--v3");
        var copy = fake.Single("revision", "copy").Args.ToList();
        copy[copy.IndexOf("--from-revision") + 1].ShouldBe("api--v1");
    }

    // ── Key Vault secret versions ────────────────────────────────────────────

    private const string ManifestJson = """
        {
          "resources": {
            "pw": { "type": "parameter.v0", "value": "{pw.inputs.value}", "inputs": { "value": { "type": "string", "secret": true } } },
            "api": {
              "type": "container.v0",
              "image": "api:1",
              "env": { "DB_PASSWORD": "{pw.value}" },
              "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8080 } }
            }
          }
        }
        """;

    private static Deployify.Core.Models.EnvironmentConfig KeyVaultConfig()
    {
        var config = AcaFixtures.AcaConfig();
        config.Parameters["pw"] = "s3cret";
        config.Secrets.Backend = Deployify.Core.SecretBackends.SecretBackendType.KeyVault;
        config.Secrets.KeyVault.VaultName = "shop-kv";
        return config;
    }

    [Fact]
    public async Task Capture_RecordsKeyVaultVersions_NotValues()
    {
        var vault = new FakeKeyVault();
        var fake = new FakeProcessRunner().When(["containerapp", "show"], Ok(App("api--v2", "api--v2")));
        var target = new AcaDeploymentTarget(Substitute.For<IPublisher>(), NullLogger<AcaDeploymentTarget>.Instance)
        {
            Runner = fake, KeyVaultClientFactory = _ => vault
        };
        var manifest = System.Text.Json.JsonSerializer.Deserialize<Deployify.Core.Models.AspireManifest>(ManifestJson)!;
        var config = KeyVaultConfig();
        var cfg = ((System.Text.Json.JsonElement)config.TargetConfig["aca"]!)
            .Deserialize<AcaTargetConfig>(Deployify.Core.Models.TargetConfigJsonOptions.Default)!;
        var app = new AcaManifestGenerator().Generate(manifest, config, cfg).Single(a => a.Name == "api");
        var secretName = AcaKeyVault.AppSecretName(
            config.Secrets.KeyVault, cfg, "api", AcaManifestGenerator.SecretNames(app)[app.SecretEnv.Keys.Single()]);

        // Another secret first, so the app's one is at version v2.
        await vault.SetSecretAsync("shop-kv", "unrelated", "x", new System.Collections.Generic.Dictionary<string, string>(), Ct);
        await vault.SetSecretAsync("shop-kv", secretName, "s3cret", new System.Collections.Generic.Dictionary<string, string>(), Ct);

        var snap = await target.CaptureAsync(manifest, config, "api", Ct);

        var versions = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(
            snap.Data["keyVaultVersions"])!;
        versions.ShouldContainKeyAndValue(secretName, "v2");
        versions.Count.ShouldBe(1);
        snap.Data["keyVaultVersions"].ShouldNotContain("s3cret");
    }

    [Fact]
    public async Task Restore_WritesTheCapturedSecretVersionBack_BeforeCopyingTheRevision()
    {
        var vault = new FakeKeyVault();
        var tags = new System.Collections.Generic.Dictionary<string, string>();
        var v1 = await vault.SetSecretAsync("shop-kv", "deployify-test-env-api-db", "old", tags, Ct);
        await vault.SetSecretAsync("shop-kv", "deployify-test-env-api-db", "new", tags, Ct);
        var shows = 0;
        var fake = new FakeProcessRunner()
            .When(spec => spec.Args.Contains("containerapp") && spec.Args.Contains("show") && !spec.Args.Contains("revision"),
                _ => Ok(++shows == 1 ? App("api--v2", "api--v1") : App("api--v3", "api--v1")));
        var target = new AcaDeploymentTarget(Substitute.For<IPublisher>(), NullLogger<AcaDeploymentTarget>.Instance)
        {
            Runner = fake, KeyVaultClientFactory = _ => vault
        };
        var snapshot = new ServiceSnapshot
        {
            ServiceName = "api", Existed = true, Revision = "api--v1",
            Data = new() { ["keyVaultVersions"] = "{\"deployify-test-env-api-db\":\"" + v1 + "\"}" }
        };

        await target.RestoreAsync(KeyVaultConfig(), snapshot, Ct);

        vault.Secrets["deployify-test-env-api-db"].Value.ShouldBe("old");
        vault.Secrets["deployify-test-env-api-db"].Tags["deployify-app"].ShouldBe("api");
        fake.Calls.ShouldContain(c => c.Args.Contains("copy"));
    }

    [Fact]
    public async Task Restore_UnchangedApp_DoesNotCopy()
    {
        var fake = new FakeProcessRunner().When(["containerapp", "show"], Ok(App("api--v1", "api--v1")));
        var snapshot = new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = "api--v1" };

        var waitFor = await Target(fake).RestoreAsync(AcaFixtures.AcaConfig(), snapshot, Ct);

        waitFor.ShouldBe("api--v1");
        fake.Calls.ShouldNotContain(c => c.Args.Contains("copy"));
    }

    [Fact]
    public async Task Restore_NewApp_DeletesIt()
    {
        var fake = new FakeProcessRunner().When(["containerapp", "show"], Ok(App("api--v1", null)));

        var waitFor = await Target(fake).RestoreAsync(
            AcaFixtures.AcaConfig(), new ServiceSnapshot { ServiceName = "api", Existed = false }, Ct);

        waitFor.ShouldBeNull();
        fake.Single("containerapp", "delete").Args.ShouldContain("--yes");
    }
}
