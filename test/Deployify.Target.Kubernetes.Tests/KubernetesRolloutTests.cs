using System.Linq;
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

namespace Deployify.Target.Kubernetes.Tests;

public sealed class KubernetesRolloutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KubernetesDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLogger<KubernetesDeploymentTarget>.Instance) { Processes = fake };

    private static ProcessResult Ok(string stdout = "") => new(0, stdout, "");

    private static string Deployment(long generation, long observed, long? revision, int spec, int replicas,
        int updated, int available, string? deadline = null)
    {
        var metadata = new JsonObject { ["name"] = "api", ["generation"] = generation };
        if (revision is not null)
        {
            metadata["annotations"] = new JsonObject { ["deployment.kubernetes.io/revision"] = revision.Value.ToString() };
        }

        var status = new JsonObject
        {
            ["observedGeneration"] = observed,
            ["replicas"] = replicas,
            ["updatedReplicas"] = updated,
            ["availableReplicas"] = available
        };
        if (deadline is not null)
        {
            status["conditions"] = new JsonArray(new JsonObject
            {
                ["type"] = "Progressing", ["status"] = "False",
                ["reason"] = "ProgressDeadlineExceeded", ["message"] = deadline
            });
        }

        return new JsonObject
        {
            ["metadata"] = metadata,
            ["spec"] = new JsonObject { ["replicas"] = spec },
            ["status"] = status
        }.ToJsonString();
    }

    private static HealthObservation Evaluate(string json, long generation)
        => KubernetesRolloutHealth.Evaluate(KubernetesDeploymentStatus.Parse(json), generation);

    // ── Health evaluation ────────────────────────────────────────────────────

    [Fact]
    public void AllPodsUpdatedAndAvailable_IsHealthy()
    {
        var obs = Evaluate(Deployment(4, 4, 3, spec: 2, replicas: 2, updated: 2, available: 2), 4);

        obs.State.ShouldBe(HealthState.Healthy);
        obs.Detail.ShouldBe("2/2 pod(s) available");
    }

    [Fact]
    public void ControllerNotCaughtUp_IsProgressing()
        => Evaluate(Deployment(4, 3, 2, 2, 2, 2, 2), 4).State.ShouldBe(HealthState.Progressing);

    [Fact]
    public void OldPodsStillRunning_IsProgressing()
    {
        var obs = Evaluate(Deployment(4, 4, 3, spec: 2, replicas: 3, updated: 2, available: 2), 4);

        obs.State.ShouldBe(HealthState.Progressing);
        obs.Detail!.ShouldContain("1 old pod(s) terminating");
    }

    [Fact]
    public void ProgressDeadlineExceeded_IsFailed()
    {
        var obs = Evaluate(Deployment(4, 4, 3, 2, 3, 1, 2, deadline: "ReplicaSet api-7f has timed out progressing."), 4);

        obs.State.ShouldBe(HealthState.Failed);
        obs.Detail!.ShouldContain("timed out progressing");
    }

    [Fact]
    public void ChangedAgainByOthers_IsFailed()
        => Evaluate(Deployment(5, 5, 4, 2, 2, 2, 2), 4).State.ShouldBe(HealthState.Failed);

    [Fact]
    public void MissingDeployment_IsFailed()
        => KubernetesRolloutHealth.Evaluate(null, 4).State.ShouldBe(HealthState.Failed);

    // ── Capture / apply / restore through kubectl ────────────────────────────

    [Fact]
    public async Task Capture_RecordsGenerationRevisionAndReplicas()
    {
        var fake = new FakeProcessRunner()
            .When(["get", "deployment", "api"], Ok(Deployment(7, 7, 3, 2, 2, 2, 2)));

        var snap = await Target(fake).CaptureAsync(Fixtures.SingleService("api"), Fixtures.KubernetesConfig(), "api", Ct);

        snap.Existed.ShouldBeTrue();
        snap.Revision.ShouldBe("7");
        snap.Data["rolloutRevision"].ShouldBe("3");
        snap.Data["replicas"].ShouldBe("2");
        snap.Data["namespace"].ShouldBe("test-api");
    }

    [Fact]
    public async Task Capture_MissingDeployment_IsNew()
    {
        var fake = new FakeProcessRunner().When(["get", "deployment", "api"], Ok(""));

        var snap = await Target(fake).CaptureAsync(Fixtures.SingleService("api"), Fixtures.KubernetesConfig(), "api", Ct);

        snap.Existed.ShouldBeFalse();
    }

    [Fact]
    public async Task Capture_UnreachableCluster_Throws()
    {
        var fake = new FakeProcessRunner()
            .When(["get", "deployment", "api"], new ProcessResult(1, "", "Unable to connect to the server"));

        await Should.ThrowAsync<System.InvalidOperationException>(
            () => Target(fake).CaptureAsync(Fixtures.SingleService("api"), Fixtures.KubernetesConfig(), "api", Ct));
    }

    [Fact]
    public async Task ApplyService_ReturnsGenerationReadBackAfterApply()
    {
        using var dir = new TempDir();
        var config = Fixtures.KubernetesConfig();
        var manifest = Fixtures.SingleService("api");
        var fake = new FakeProcessRunner()
            .When(["get", "deployment", "api"], Ok(Deployment(8, 7, 3, 2, 2, 2, 2)));
        var target = Target(fake);
        await target.GenerateAsync(manifest, config, dir.Path, Ct);

        var applied = await target.ApplyServiceAsync(manifest, config, dir.Path, "api", Ct);

        applied.Success.ShouldBeTrue();
        applied.Revision.ShouldBe("8");
        fake.Calls.ShouldContain(c => c.Args.Contains("apply"));
    }

    [Fact]
    public async Task Restore_UndoesToCapturedRevision_AndRestoresConfigFirst()
    {
        var before = Deployment(7, 7, 3, 2, 2, 2, 2);
        var fake = new FakeProcessRunner()
            .When(["get", "deployment", "api"], Ok(before))
            .When(["get", "configmap", "api-config"], Ok("""{"data":{"MODE":"old"}}"""));
        var target = Target(fake);
        var config = Fixtures.KubernetesConfig();
        var snap = await target.CaptureAsync(Fixtures.SingleService("api"), config, "api", Ct);

        // The rollout changed the config and the pod template.
        var deployed = Deployment(8, 8, 4, 2, 2, 2, 0);
        var undone = Deployment(9, 8, 5, 2, 2, 2, 0);
        var reads = 0;
        fake.When(spec => spec.Args.Contains("deployment") && spec.Args.Contains("get"),
                _ => Ok(++reads == 1 ? deployed : undone))
            .When(["get", "configmap", "api-config"], Ok("""{"data":{"MODE":"new"}}"""));

        var waitFor = await target.RestoreAsync(config, snap, Ct);

        waitFor.ShouldBe("9");
        var undo = fake.Single("rollout", "undo").Args;
        undo.ShouldContain("--to-revision=3");
        undo.ShouldContain("test-api");
        var applyConfig = fake.Calls.FindIndex(c => c.Args.Contains("--server-side"));
        applyConfig.ShouldBeGreaterThanOrEqualTo(0);
        fake.Calls[applyConfig].Stdin!.ShouldContain("\"MODE\":\"old\"");
        applyConfig.ShouldBeLessThan(fake.Calls.FindIndex(c => c.Args.Contains("undo")));
        fake.Calls.ShouldNotContain(c => c.Args.Contains("scale"));
    }

    [Fact]
    public async Task Restore_UnchangedDeployment_DoesNothing()
    {
        var fake = new FakeProcessRunner().When(["get", "deployment", "api"], Ok(Deployment(7, 7, 3, 2, 2, 2, 2)));
        var target = Target(fake);
        var config = Fixtures.KubernetesConfig();
        var snap = await target.CaptureAsync(Fixtures.SingleService("api"), config, "api", Ct);

        var waitFor = await target.RestoreAsync(config, snap, Ct);

        waitFor.ShouldBe("7");
        fake.Calls.ShouldNotContain(c => c.Args.Contains("undo") || c.Args.Contains("scale") || c.Args.Contains("--server-side"));
    }

    [Fact]
    public async Task Restore_NewDeployment_ScalesItToZero()
    {
        var fake = new FakeProcessRunner().When(["get", "deployment", "api"], Ok(Deployment(1, 1, 1, 1, 1, 1, 0)));
        var snapshot = new ServiceSnapshot
        {
            ServiceName = "api", Existed = false, Data = new() { ["namespace"] = "test-api" }
        };

        var waitFor = await Target(fake).RestoreAsync(Fixtures.KubernetesConfig(), snapshot, Ct);

        waitFor.ShouldBeNull();
        fake.Single("scale").Args.ShouldContain("--replicas=0");
    }

    [Fact]
    public async Task WithoutKubectl_RolloutsAreUnavailable_SoApplyUsesThePlainPath()
    {
        var fake = new FakeProcessRunner();
        fake.Missing.Add("kubectl");

        var reason = await ((IRolloutTarget)Target(fake)).GetUnavailableReasonAsync(Fixtures.KubernetesConfig(), Ct);

        reason.ShouldNotBeNull();
        reason.ShouldContain("kubectl");
    }
}
