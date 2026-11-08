using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;
using Deployify.Core.State;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>
/// destroy, state and the lock, end to end against real state files. Nomad is pointed at a
/// port nothing listens on, so every platform call fails fast; these tests check that the
/// commands never delete or record anything on a guess, and always let go of the lock.
/// </summary>
public sealed class StateCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static RootCommand Root() => CommandRunner.BuildFull();

    /// <summary>Nothing listens on port 1.</summary>
    private const string Unreachable = "http://127.0.0.1:1";

    private static async Task WriteStateAsync(string dir, Action<EnvironmentConfig>? configure = null)
    {
        var cfg = Harness.NomadConfig(address: Unreachable);
        configure?.Invoke(cfg);
        await Harness.WriteStateAsync(dir, new DeployifyStateFile { ActiveEnvironment = "prod", Environments = { ["prod"] = cfg } }, Ct);
        await Harness.WriteManifestAsync(dir, ct: Ct);
    }

    private static StateSession Session(string dir) => new(dir, "prod", null);

    private static string LockFile(string dir) => Path.Combine(dir, ".deployify", "state", "deployify", "prod", "lock.json");

    [Fact]
    public async Task Destroy_OfAProtectedEnvironment_IsRefused()
    {
        using var dir = new TempDir();
        await WriteStateAsync(dir.Path, c => c.Protected = true);

        (await Root().InvokeAsync(["destroy", "--project-path", dir.Path, "--auto-approve"], Ct)).ShouldBe(1);
        File.Exists(LockFile(dir.Path)).ShouldBeFalse();
    }

    [Fact]
    public async Task Destroy_WhenTheTargetCantBeAsked_FailsAndKeepsTheInventory()
    {
        using var dir = new TempDir();
        await WriteStateAsync(dir.Path);
        await using (var s = Session(dir.Path))
        {
            await s.Inventory.UpdateAsync(i => i.Upsert("api", [], DateTimeOffset.UtcNow), Ct);
        }

        (await Root().InvokeAsync(["destroy", "--project-path", dir.Path, "--auto-approve"], Ct)).ShouldBe(1);

        await using var after = Session(dir.Path);
        (await after.Inventory.LoadAsync(Ct)).Find("api").ShouldNotBeNull();
        File.Exists(LockFile(dir.Path)).ShouldBeFalse();
    }

    [Fact]
    public async Task Apply_RecordsTheManifestsServicesBeforeDeploying_AndReleasesTheLock()
    {
        using var dir = new TempDir();
        await WriteStateAsync(dir.Path);

        // Nomad is unreachable, so the deploy itself fails.
        (await Root().InvokeAsync(["apply", "--project-path", dir.Path, "--auto-approve", "--no-health-gate"], Ct)).ShouldBe(1);

        await using var s = Session(dir.Path);
        var inventory = await s.Inventory.LoadAsync(Ct);
        inventory.Services.Select(r => r.Name).Order().ShouldBe(["api", "redis", "worker"]);
        inventory.Find("worker")!.DependsOn.ShouldBe(["api"]);
        File.Exists(LockFile(dir.Path)).ShouldBeFalse();
    }

    [Fact]
    public async Task ALockedEnvironment_RefusesApplyDestroyAndRollback_UntilUnlocked()
    {
        using var dir = new TempDir();
        await WriteStateAsync(dir.Path);
        await using var s = Session(dir.Path);
        var held = await s.LockAsync("apply", Ct, autoRenew: false);

        (await Root().InvokeAsync(["apply", "--project-path", dir.Path, "--auto-approve", "--no-health-gate"], Ct)).ShouldBe(1);
        (await Root().InvokeAsync(["destroy", "--project-path", dir.Path, "--auto-approve"], Ct)).ShouldBe(1);
        (await Root().InvokeAsync(["rollback", "--project-path", dir.Path, "--auto-approve"], Ct)).ShouldBe(1);
        (await s.Inventory.ReadAsync(Ct)).ETag.ShouldBeNull(); // apply never got as far as recording anything

        (await Root().InvokeAsync(["state", "unlock", "not-the-id", "--project-path", dir.Path], Ct)).ShouldBe(1);
        (await Root().InvokeAsync(["state", "unlock", held.Info.Id, "--project-path", dir.Path], Ct)).ShouldBe(0);
        File.Exists(LockFile(dir.Path)).ShouldBeFalse();
    }

    [Fact]
    public async Task ADryRunApply_DoesNotNeedTheLock()
    {
        using var dir = new TempDir();
        await WriteStateAsync(dir.Path);
        await using var s = Session(dir.Path);
        await s.LockAsync("apply", Ct, autoRenew: false);

        (await Root().InvokeAsync(["apply", "--project-path", dir.Path, "--dry-run"], Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task StateShow_Works_WithAndWithoutAnInventory()
    {
        using var dir = new TempDir();
        await WriteStateAsync(dir.Path);

        (await Root().InvokeAsync(["state", "show", "--project-path", dir.Path], Ct)).ShouldBe(0);

        await using (var s = Session(dir.Path))
        {
            await s.Inventory.UpdateAsync(i => i.Upsert("api", ["db"], DateTimeOffset.UtcNow), Ct);
        }

        (await Root().InvokeAsync(["state", "show", "--project-path", dir.Path], Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task StatePush_NeedsARemoteBackend()
    {
        using var dir = new TempDir();
        await WriteStateAsync(dir.Path);

        (await Root().InvokeAsync(["state", "push", "--project-path", dir.Path], Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Rollback_IgnoresDestroyJournals()
    {
        using var dir = new TempDir();
        await WriteStateAsync(dir.Path);
        await using (var journal = await RolloutJournal.BeginAsync(dir.Path, "prod", "HashiCorp Nomad", [["api"]], ct: Ct,
                         operation: RolloutOperation.Destroy))
        {
            journal.Record.State = RolloutState.Succeeded;
            await journal.SaveAsync(Ct);
        }

        // The only journal is a destroy: there is no rollout to undo.
        (await Root().InvokeAsync(["rollback", "--project-path", dir.Path, "--auto-approve"], Ct)).ShouldBe(1);
        (await Root().InvokeAsync(["status", "--project-path", dir.Path], Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task ARemoteBackendWithoutItsSection_FailsWithAClearMessage()
    {
        using var dir = new TempDir();
        await WriteStateAsync(dir.Path, c => c.State = new StateBackendConfig { Backend = StateBackendType.S3 });

        (await Root().InvokeAsync(["apply", "--project-path", dir.Path, "--auto-approve"], Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task StateConfig_RoundTripsThroughTheStateFile()
    {
        using var dir = new TempDir();
        await WriteStateAsync(dir.Path, c =>
        {
            c.Protected = true;
            c.Prune = true;
            c.State = new StateBackendConfig
            {
                Backend = StateBackendType.AzureBlob,
                AzureBlob = new AzureBlobStateConfig { AccountName = "acct", SasToken = "env:SAS" }
            };
        });

        var (cfg, _) = await new Core.Services.StateManager().LoadActiveAsync(dir.Path, ct: Ct);

        cfg.Name.ShouldBe("prod");
        cfg.Protected.ShouldBeTrue();
        cfg.Prune.ShouldBeTrue();
        cfg.State!.Backend.ShouldBe(StateBackendType.AzureBlob);
        cfg.State.AzureBlob!.AccountName.ShouldBe("acct");
        StateFileSecrets.FindLiteral(await new Core.Services.StateManager().LoadFileAsync(dir.Path, Ct)).ShouldBeEmpty();
    }
}
