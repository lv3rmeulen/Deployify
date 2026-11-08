using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Rollouts;

public sealed class RolloutEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RolloutOptions Fast = new()
    {
        HealthTimeout = System.TimeSpan.FromSeconds(5),
        PollInterval = System.TimeSpan.FromMilliseconds(1),
        ProbeTimeout = System.TimeSpan.FromSeconds(2),
        RequiredSuccesses = 2,
        RollbackTimeout = System.TimeSpan.FromSeconds(10)
    };

    // api uses db, so db goes first.
    private static readonly Dictionary<string, string[]> ApiUsesDb = new() { ["api"] = ["db"] };

    private static Task<RolloutResult> RunAsync(
        TempDir dir, FakeRolloutTarget fake, RolloutOptions? options = null, string imageTag = "v1",
        CancellationToken? ct = null, List<RolloutProgress>? progress = null)
        => new RolloutEngine().RunAsync(new RolloutRequest
        {
            Target = FakeRolloutTarget.Target(["api", "db"], ApiUsesDb, imageTag),
            Rollout = fake,
            Manifest = new AspireManifest(),
            Config = new EnvironmentConfig(),
            EnvironmentName = "prod",
            OutputDirectory = dir.Path,
            StateDirectory = dir.Path,
            Options = options ?? Fast,
            OnProgress = progress is null ? null : new System.Action<RolloutProgress>(progress.Add)
        }, ct ?? Ct);

    [Fact]
    public async Task AllHealthy_Succeeds_DeployingDependenciesFirst()
    {
        using var dir = new TempDir();
        var fake = new FakeRolloutTarget();

        var result = await RunAsync(dir, fake);

        result.Outcome.ShouldBe(RolloutOutcome.Succeeded);
        result.ExitCode.ShouldBe(0);
        fake.Calls.IndexOf("apply db").ShouldBeLessThan(fake.Calls.IndexOf("apply api"));
        fake.Calls.IndexOf("observe db:2").ShouldBeLessThan(fake.Calls.IndexOf("capture api"));
        fake.Calls.ShouldNotContain(c => c.StartsWith("restore"));
        result.Services.ShouldAllBe(s => s.State == ServiceRolloutState.Healthy);
    }

    [Fact]
    public async Task AllHealthy_WritesJournal_AndReleasesLock()
    {
        using var dir = new TempDir();

        var result = await RunAsync(dir, new FakeRolloutTarget());

        File.Exists(result.JournalPath).ShouldBeTrue();
        var record = await RolloutJournal.ReadAsync(result.JournalPath, Ct);
        record!.State.ShouldBe(RolloutState.Succeeded);
        record.Services.Single(s => s.Name == "api").Snapshot!.Revision.ShouldBe("api:1");
        record.Waves.Count.ShouldBe(2);
        Directory.GetFiles(Path.Combine(dir.Path, RolloutJournal.DirectoryName), "*.lock").ShouldBeEmpty();
    }

    [Fact]
    public async Task UnhealthyService_RestoresEveryTouchedService_InReverseOrder()
    {
        using var dir = new TempDir();
        var fake = new FakeRolloutTarget().Script("api:2", HealthObservation.Failed("crash loop"));

        var result = await RunAsync(dir, fake);

        result.Outcome.ShouldBe(RolloutOutcome.RolledBack);
        result.ExitCode.ShouldBe(2);
        result.Reason!.ShouldContain("crash loop");
        fake.Calls.IndexOf("restore api").ShouldBeLessThan(fake.Calls.IndexOf("restore db"));
        // Each restored service is gated on its previous revision.
        fake.Calls.ShouldContain("observe api:1");
        fake.Calls.ShouldContain("observe db:1");
        result.Services.ShouldAllBe(s => s.State == ServiceRolloutState.RolledBack);
    }

    [Fact]
    public async Task HealthTimeout_FailsTheRollout_AndRollsBack()
    {
        using var dir = new TempDir();
        var fake = new FakeRolloutTarget().Script("api:2", HealthObservation.Progressing("0/1 task(s) running"));
        var options = new RolloutOptions
        {
            HealthTimeout = System.TimeSpan.FromMilliseconds(200),
            PollInterval = System.TimeSpan.FromMilliseconds(10),
            RequiredSuccesses = 1,
            RollbackTimeout = System.TimeSpan.FromSeconds(10)
        };

        var result = await RunAsync(dir, fake, options);

        result.Outcome.ShouldBe(RolloutOutcome.RolledBack);
        result.Reason!.ShouldContain("not healthy after");
        result.Reason!.ShouldContain("0/1 task(s) running");
    }

    [Fact]
    public async Task FlappingService_NeedsConsecutiveHealthyObservations()
    {
        using var dir = new TempDir();
        var fake = new FakeRolloutTarget().Script("db:2",
            HealthObservation.Healthy(), HealthObservation.Progressing(), HealthObservation.Healthy(), HealthObservation.Healthy());

        var result = await RunAsync(dir, fake);

        result.Outcome.ShouldBe(RolloutOutcome.Succeeded);
        fake.Calls.Count(c => c == "observe db:2").ShouldBe(4);
    }

    [Fact]
    public async Task DeployFailure_RollsBackTheFailedServiceToo()
    {
        using var dir = new TempDir();
        var fake = new FakeRolloutTarget();
        fake.ApplyResults["api"] = new AppliedRevision { ServiceName = "api", Success = false, Error = "register failed" };

        var result = await RunAsync(dir, fake);

        result.Outcome.ShouldBe(RolloutOutcome.RolledBack);
        result.Reason!.ShouldContain("register failed");
        fake.Calls.ShouldContain("restore api");
        fake.Calls.ShouldContain("restore db");
    }

    [Fact]
    public async Task NoRollback_LeavesFailureInPlace()
    {
        using var dir = new TempDir();
        var fake = new FakeRolloutTarget().Script("api:2", HealthObservation.Failed("boom"));

        var result = await RunAsync(dir, fake, new RolloutOptions
        {
            PollInterval = System.TimeSpan.FromMilliseconds(1),
            AutoRollback = false
        });

        result.Outcome.ShouldBe(RolloutOutcome.Failed);
        result.ExitCode.ShouldBe(1);
        fake.Calls.ShouldNotContain(c => c.StartsWith("restore"));
    }

    [Fact]
    public async Task PlatformRevert_IsNotRestoredAgain_ButOthersAre()
    {
        using var dir = new TempDir();
        var fake = new FakeRolloutTarget { NativeRollback = true }
            .Script("api:2", HealthObservation.Reverted("circuit breaker"));

        var result = await RunAsync(dir, fake);

        result.Outcome.ShouldBe(RolloutOutcome.RolledBack);
        fake.Calls.ShouldNotContain("restore api");
        fake.Calls.ShouldContain("observe api:1");
        fake.Calls.ShouldContain("restore db");
        result.Services.Single(s => s.Name == "api").State.ShouldBe(ServiceRolloutState.RevertedByPlatform);
    }

    [Fact]
    public async Task RestoreFailure_EndsAsRollbackFailed_AndStillRestoresTheRest()
    {
        using var dir = new TempDir();
        var fake = new FakeRolloutTarget().Script("api:2", HealthObservation.Failed("boom"));
        fake.FailRestore.Add("api");

        var result = await RunAsync(dir, fake);

        result.Outcome.ShouldBe(RolloutOutcome.RollbackFailed);
        result.ExitCode.ShouldBe(3);
        fake.Calls.ShouldContain("restore db");
        result.Services.Single(s => s.Name == "api").State.ShouldBe(ServiceRolloutState.RollbackFailed);
        result.Services.Single(s => s.Name == "db").State.ShouldBe(ServiceRolloutState.RolledBack);
    }

    [Fact]
    public async Task Cancellation_StillRollsBack()
    {
        using var dir = new TempDir();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var fake = new FakeRolloutTarget().Script("api:2", HealthObservation.Progressing());
        fake.OnObserve = (_, revision) =>
        {
            if (revision == "api:2")
            {
                cts.Cancel();
            }
        };

        var result = await RunAsync(dir, fake, ct: cts.Token);

        result.Outcome.ShouldBe(RolloutOutcome.RolledBack);
        result.Reason.ShouldBe("cancelled");
        fake.Calls.ShouldContain("restore api");
        fake.Calls.ShouldContain("restore db");
    }

    [Fact]
    public async Task NewService_IsRestoredWithoutWaitingForAPreviousRevision()
    {
        using var dir = new TempDir();
        var fake = new FakeRolloutTarget().Script("api:2", HealthObservation.Failed("boom"));
        fake.Snapshots["api"] = new ServiceSnapshot { ServiceName = "api", Existed = false };

        var result = await RunAsync(dir, fake);

        result.Outcome.ShouldBe(RolloutOutcome.RolledBack);
        fake.Calls.ShouldContain("restore api");
        fake.Calls.ShouldNotContain(c => c.StartsWith("observe api:1"));
    }

    [Fact]
    public async Task FailureBeforeAnythingChanged_IsPlainFailure()
    {
        using var dir = new TempDir();
        var fake = new FakeRolloutTarget();
        var throwing = new ThrowingCapture(fake);

        var result = await new RolloutEngine().RunAsync(new RolloutRequest
        {
            Target = FakeRolloutTarget.Target(["api", "db"], ApiUsesDb),
            Rollout = throwing,
            Manifest = new AspireManifest(),
            Config = new EnvironmentConfig(),
            EnvironmentName = "prod",
            OutputDirectory = dir.Path,
            StateDirectory = dir.Path,
            Options = Fast
        }, Ct);

        result.Outcome.ShouldBe(RolloutOutcome.Failed);
        result.Reason!.ShouldContain("aws not found");
        fake.Calls.ShouldNotContain(c => c.StartsWith("apply") || c.StartsWith("restore"));
    }

    [Fact]
    public async Task MutableImageTag_IsReportedAsWarning()
    {
        using var dir = new TempDir();
        var progress = new List<RolloutProgress>();

        var result = await RunAsync(dir, new FakeRolloutTarget(), imageTag: "latest", progress: progress);

        result.Outcome.ShouldBe(RolloutOutcome.Succeeded);
        result.Warnings.Single().ShouldContain("mutable image tag");
        progress.ShouldContain(p => p.Kind == RolloutProgressKind.Warning);
    }

    [Fact]
    public async Task SecondRolloutToSameEnvironment_IsRefusedWhileFirstHoldsLock()
    {
        using var dir = new TempDir();
        await using var first = await RolloutJournal.BeginAsync(dir.Path, "prod", "Fake", [["api"]], ct: Ct);

        await Should.ThrowAsync<System.InvalidOperationException>(
            () => RolloutJournal.BeginAsync(dir.Path, "prod", "Fake", [["api"]], ct: Ct));
    }

    /// <summary>Wraps the fake so capture fails, as when the platform CLI is missing.</summary>
    private sealed class ThrowingCapture(FakeRolloutTarget inner) : IRolloutTarget
    {
        public Task<ServiceSnapshot> CaptureAsync(
            AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
            => throw new System.InvalidOperationException("aws not found");

        public Task<AppliedRevision> ApplyServiceAsync(
            AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service,
            CancellationToken ct = default)
            => inner.ApplyServiceAsync(manifest, config, outputDirectory, service, ct);

        public Task<HealthObservation> ObserveAsync(
            EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
            => inner.ObserveAsync(config, service, revision, ct);

        public Task RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
            => inner.RestoreAsync(config, snapshot, ct);

        public bool HasNativeRollback(EnvironmentConfig config) => false;
    }
}
