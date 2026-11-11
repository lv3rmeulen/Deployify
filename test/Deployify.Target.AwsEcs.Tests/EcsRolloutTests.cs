using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.AwsEcs.Tests;

public sealed class EcsRolloutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Old = "arn:aws:ecs:us-east-1:1:task-definition/api:6";
    private const string New = "arn:aws:ecs:us-east-1:1:task-definition/api:7";

    private static EcsDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLogger<EcsDeploymentTarget>.Instance) { Runner = fake };

    private static ProcessResult Ok(string json) => new(0, json, "");

    private static string Describe(string serviceTaskDef, int desired, params string[] deployments)
        => $$"""{"services":[{"status":"ACTIVE","taskDefinition":"{{serviceTaskDef}}","desiredCount":{{desired}},"deployments":[{{string.Join(",", deployments)}}]}],"failures":[]}""";

    private static string Deployment(string status, string taskDef, string? rolloutState, int desired, int running,
        int failed = 0, string? reason = null)
        => $$"""{"status":"{{status}}","taskDefinition":"{{taskDef}}",{{(rolloutState is null ? "" : $"\"rolloutState\":\"{rolloutState}\",")}}{{(reason is null ? "" : $"\"rolloutStateReason\":\"{reason}\",")}}"desiredCount":{{desired}},"runningCount":{{running}},"failedTasks":{{failed}}}""";

    // ── Health evaluation ────────────────────────────────────────────────────

    [Fact]
    public void Completed_PrimaryDeployment_IsHealthy()
    {
        var status = EcsServiceStatus.Parse(Describe(New, 2, Deployment("PRIMARY", New, "COMPLETED", 2, 2)));

        var obs = EcsRolloutHealth.Evaluate(status, New, circuitBreaker: true);

        obs.State.ShouldBe(HealthState.Healthy);
        obs.Detail.ShouldBe("2/2 task(s) running");
    }

    [Fact]
    public void InProgress_IsProgressing_WithFailedTaskCount()
    {
        var status = EcsServiceStatus.Parse(Describe(New, 2,
            Deployment("PRIMARY", New, "IN_PROGRESS", 2, 1, failed: 3),
            Deployment("ACTIVE", Old, "COMPLETED", 2, 2)));

        var obs = EcsRolloutHealth.Evaluate(status, New, circuitBreaker: true);

        obs.State.ShouldBe(HealthState.Progressing);
        obs.Detail.ShouldBe("1/2 task(s) running, 3 failed to start");
    }

    [Fact]
    public void FailedRollout_WithoutCircuitBreaker_IsFailed()
    {
        var status = EcsServiceStatus.Parse(Describe(New, 1,
            Deployment("PRIMARY", New, "FAILED", 1, 0, reason: "tasks failed to start")));

        var obs = EcsRolloutHealth.Evaluate(status, New, circuitBreaker: false);

        obs.State.ShouldBe(HealthState.Failed);
        obs.Detail.ShouldBe("tasks failed to start");
    }

    [Fact]
    public void CircuitBreakerRollback_IsReportedAsPlatformRevert()
    {
        var status = EcsServiceStatus.Parse(Describe(Old, 1,
            Deployment("PRIMARY", Old, "IN_PROGRESS", 1, 0),
            Deployment("ACTIVE", New, "FAILED", 1, 0, reason: "circuit breaker triggered")));

        var obs = EcsRolloutHealth.Evaluate(status, New, circuitBreaker: true);

        obs.State.ShouldBe(HealthState.RevertedByPlatform);
        obs.Revision.ShouldBe(Old);
        obs.Detail!.ShouldContain("api:6");
        obs.Detail!.ShouldContain("circuit breaker triggered");
    }

    [Fact]
    public void MissingService_IsFailed()
        => EcsRolloutHealth.Evaluate(null, New, circuitBreaker: true).State.ShouldBe(HealthState.Failed);

    [Fact]
    public void WithoutRolloutState_SingleFullyRunningDeployment_IsHealthy()
    {
        var status = EcsServiceStatus.Parse(Describe(New, 1, Deployment("PRIMARY", New, null, 1, 1)));

        EcsRolloutHealth.Evaluate(status, New, circuitBreaker: true).State.ShouldBe(HealthState.Healthy);
    }

    // ── Capture ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Capture_RecordsRunningTaskDefinitionAndCount()
    {
        var fake = new FakeProcessRunner()
            .When(["describe-services"], Ok(Describe(Old, 3, Deployment("PRIMARY", Old, "COMPLETED", 3, 3))));

        var snap = await Target(fake).CaptureAsync(new(), EcsFixtures.EcsConfig(), "api", Ct);

        snap.Existed.ShouldBeTrue();
        snap.Revision.ShouldBe(Old);
        snap.Data["desiredCount"].ShouldBe("3");
    }

    [Fact]
    public async Task Capture_MissingService_IsNew()
    {
        var fake = new FakeProcessRunner()
            .When(["describe-services"], Ok("""{"services":[],"failures":[{"reason":"MISSING"}]}"""));

        var snap = await Target(fake).CaptureAsync(new(), EcsFixtures.EcsConfig(), "api", Ct);

        snap.Existed.ShouldBeFalse();
    }

    [Fact]
    public async Task Capture_WhenEcsCannotBeReached_Throws_InsteadOfAssumingANewService()
    {
        var fake = new FakeProcessRunner()
            .When(["describe-services"], new ProcessResult(255, "", "Unable to locate credentials"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => Target(fake).CaptureAsync(new(), EcsFixtures.EcsConfig(), "api", Ct));
    }

    // ── Apply / restore ──────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyService_ReturnsTaskDefinition_AndDoesNotWaitForStable()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = EcsFixtures.EcsConfig();
        var manifest = EcsFixtures.SingleService("myapp");
        var fake = new FakeProcessRunner()
            .When(["register-task-definition"], Ok("""{"taskDefinition":{"taskDefinitionArn":"arn:td/myapp:7"}}"""))
            .When(["list-services"], Ok("""{"serviceArns":["arn:aws:ecs:us-east-1:1:service/test-cluster/myapp"]}"""))
            .When(["update-service"], Ok("""{"service":{"serviceArn":"arn:svc/myapp"}}"""));
        var target = Target(fake);
        await target.GenerateAsync(manifest, config, dir.Path, Ct);

        var applied = await target.ApplyServiceAsync(manifest, config, dir.Path, "myapp", Ct);

        applied.Success.ShouldBeTrue();
        applied.Revision.ShouldBe("arn:td/myapp:7");
        fake.Calls.ShouldNotContain(c => c.Args.Contains("wait"));
    }

    [Fact]
    public async Task Restore_PointsServiceBackAtPreviousTaskDefinition()
    {
        var fake = new FakeProcessRunner()
            .When(["update-service"], Ok("""{"service":{"serviceArn":"arn:svc/api"}}"""));
        var snapshot = new ServiceSnapshot
        {
            ServiceName = "api", Existed = true, Revision = Old,
            Data = new() { ["desiredCount"] = "3" }
        };

        var waitFor = await Target(fake).RestoreAsync(EcsFixtures.EcsConfig(), snapshot, Ct);

        waitFor.ShouldBe(Old);
        var args = fake.Single("update-service").Args.ToList();
        args[args.IndexOf("--task-definition") + 1].ShouldBe(Old);
        args[args.IndexOf("--desired-count") + 1].ShouldBe("3");
    }

    [Fact]
    public async Task Restore_NewService_ScalesItToZero()
    {
        var fake = new FakeProcessRunner()
            .When(["describe-services"], Ok(Describe(New, 1, Deployment("PRIMARY", New, "FAILED", 1, 0))))
            .When(["update-service"], Ok("""{"service":{"serviceArn":"arn:svc/api"}}"""));

        var waitFor = await Target(fake).RestoreAsync(
            EcsFixtures.EcsConfig(), new ServiceSnapshot { ServiceName = "api", Existed = false }, Ct);

        waitFor.ShouldBeNull();
        var args = fake.Single("update-service").Args.ToList();
        args[args.IndexOf("--desired-count") + 1].ShouldBe("0");
        args.ShouldNotContain("--task-definition");
    }

    // ── SSM parameter versions ───────────────────────────────────────────────

    private const string DbParam = "/deployify/test-cluster/api/DB";
    private const string SsmVersions = "{\"" + DbParam + "\":3}";

    private static ProcessResult Parameter(string value, long version)
        => Ok("{\"Parameter\":{\"Name\":\"" + DbParam + "\",\"Value\":\"" + value + "\",\"Version\":" + version + "}}");

    [Fact]
    public async Task Capture_RecordsSsmVersions_NotValues()
    {
        var fake = new FakeProcessRunner()
            .When(["describe-services"], Ok(Describe(Old, 1, Deployment("PRIMARY", Old, "COMPLETED", 1, 1))))
            .When(["get-parameters-by-path"],
                Ok("{\"Parameters\":[{\"Name\":\"" + DbParam + "\",\"Value\":\"s3cret\",\"Version\":3}]}"));

        var snap = await Target(fake).CaptureAsync(new(), EcsFixtures.EcsConfig(), "api", Ct);

        snap.Data["ssmVersions"].ShouldBe(SsmVersions);
        snap.Data.Values.ShouldNotContain(v => v.Contains("s3cret"));
        fake.Single("get-parameters-by-path").Args.ShouldContain("/deployify/test-cluster/api");
    }

    [Fact]
    public async Task Restore_WritesTheCapturedVersionsValueBack_BeforeTheServiceRestarts()
    {
        string? written = null;
        var fake = new FakeProcessRunner()
            .When(["get-parameter", "--name", DbParam], Parameter("new", 4))
            .When(["get-parameter", "--name", DbParam + ":3"], Parameter("old", 3))
            .When(spec => spec.Args.Contains("put-parameter"), spec =>
            {
                var file = spec.Args[spec.Args.ToList().IndexOf("--cli-input-json") + 1]["file://".Length..];
                written = System.IO.File.ReadAllText(file);
                return Ok("""{"Version":5}""");
            })
            .When(["update-service"], Ok("""{"service":{"serviceArn":"arn:svc/api"}}"""));
        var snapshot = new ServiceSnapshot
        {
            ServiceName = "api", Existed = true, Revision = Old,
            Data = new() { ["desiredCount"] = "1", ["ssmVersions"] = SsmVersions }
        };

        await Target(fake).RestoreAsync(EcsFixtures.EcsConfig(), snapshot, Ct);

        written!.ShouldContain("\"Value\":\"old\"");
        fake.Calls.FindIndex(c => c.Args.Contains("put-parameter"))
            .ShouldBeLessThan(fake.Calls.FindIndex(c => c.Args.Contains("update-service")));
    }

    [Fact]
    public async Task Restore_UnchangedParameter_IsNotRewritten()
    {
        var fake = new FakeProcessRunner()
            .When(["get-parameter", "--name", DbParam], Parameter("old", 3))
            .When(["update-service"], Ok("""{"service":{"serviceArn":"arn:svc/api"}}"""));
        var snapshot = new ServiceSnapshot
        {
            ServiceName = "api", Existed = true, Revision = Old,
            Data = new() { ["desiredCount"] = "1", ["ssmVersions"] = SsmVersions }
        };

        await Target(fake).RestoreAsync(EcsFixtures.EcsConfig(), snapshot, Ct);

        fake.Calls.ShouldNotContain(c => c.Args.Contains("put-parameter"));
    }

    // ── Circuit breaker ──────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateService_EnablesCircuitBreakerWithRollback_ByDefault()
    {
        var fake = new FakeProcessRunner()
            .When(["list-services"], Ok("""{"serviceArns":["arn:x/api"]}"""))
            .When(["update-service"], Ok("""{"service":{"serviceArn":"arn:x"}}"""));

        await new EcsCliClient(new EcsTargetConfig(), NullLogger.Instance, fake).UpsertServiceAsync("api", New, 1, Ct);

        var args = fake.Single("update-service").Args.ToList();
        args[args.IndexOf("--deployment-configuration") + 1]
            .ShouldBe("deploymentCircuitBreaker={enable=true,rollback=true}");
    }

    [Fact]
    public async Task CircuitBreaker_CanBeTurnedOff()
    {
        var fake = new FakeProcessRunner()
            .When(["list-services"], Ok("""{"serviceArns":[]}"""))
            .When(["create-service"], Ok("""{"service":{"serviceArn":"arn:x"}}"""));

        await new EcsCliClient(new EcsTargetConfig { DeploymentCircuitBreaker = false }, NullLogger.Instance, fake)
            .UpsertServiceAsync("api", New, 1, Ct);

        fake.Single("create-service").Args.ShouldNotContain("--deployment-configuration");
    }

    [Fact]
    public async Task HasNativeRollback_FollowsCircuitBreakerSetting()
        => (await Target(new FakeProcessRunner()).HasNativeRollbackAsync(EcsFixtures.EcsConfig(), TestContext.Current.CancellationToken)).ShouldBeTrue();
}
