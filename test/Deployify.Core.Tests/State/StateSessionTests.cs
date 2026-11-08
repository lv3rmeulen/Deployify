using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Rollouts;
using Deployify.Core.Services;
using Deployify.Core.State;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.State;

public sealed class InventoryStoreTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDir _dir = new();
    private readonly LocalStateBackend _backend;

    public InventoryStoreTests() => _backend = LocalStateBackend.ForStateDirectory(_dir.Path);

    private InventoryStore Store() => new(_backend, "deployify/prod/inventory.json", "prod");

    [Fact]
    public async Task ANewEnvironment_HasAnEmptyInventory_ThatTheFirstUpdateCreates()
    {
        (await Store().ReadAsync(Ct)).ETag.ShouldBeNull();

        var saved = await Store().UpdateAsync(inv => inv.Upsert("api", ["db"], DateTimeOffset.UtcNow), Ct);

        saved.Serial.ShouldBe(1);
        saved.Environment.ShouldBe("prod");
        var loaded = await Store().LoadAsync(Ct);
        loaded.Lineage.ShouldBe(saved.Lineage);
        loaded.Find("api")!.DependsOn.ShouldBe(["db"]);
    }

    [Fact]
    public async Task Upsert_KeepsWhenAServiceWasFirstDeployed()
    {
        var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await Store().UpdateAsync(inv => inv.Upsert("api", [], first), Ct);
        await Store().UpdateAsync(inv => inv.Upsert("api", [], first.AddDays(3)), Ct);

        var api = (await Store().LoadAsync(Ct)).Find("api")!;
        api.CreatedAt.ShouldBe(first);
        api.UpdatedAt.ShouldBe(first.AddDays(3));
    }

    [Fact]
    public async Task ConcurrentUpdates_AreMerged_NotLost()
    {
        await Store().UpdateAsync(inv => inv.Upsert("api", [], DateTimeOffset.UtcNow), Ct);

        var interfered = false;
        await Store().UpdateAsync(inv =>
        {
            if (!interfered)
            {
                interfered = true;
                // Another run writes between our read and our write.
                Store().UpdateAsync(other => other.Upsert("worker", [], DateTimeOffset.UtcNow), Ct).GetAwaiter().GetResult();
            }

            inv.Upsert("db", [], DateTimeOffset.UtcNow);
        }, Ct);

        var names = (await Store().LoadAsync(Ct)).Services.Select(s => s.Name).Order().ToList();
        names.ShouldBe(["api", "db", "worker"]);
        (await Store().LoadAsync(Ct)).Serial.ShouldBe(3);
    }

    public void Dispose() => _dir.Dispose();
}

public sealed class StateSessionTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDir _local = new();
    private readonly TempDir _remoteDir = new();
    private readonly LocalStateBackend _remote;

    public StateSessionTests() => _remote = new LocalStateBackend(_remoteDir.Path);

    /// <summary>A session that treats another directory as its remote backend.</summary>
    private StateSession Remote(string env = "prod")
        => new(_local.Path, env, new StateBackendConfig { Backend = StateBackendType.Consul }, _remote);

    private async Task PutRemoteAsync(string key, object value)
        => await _remote.WriteAsync(key, JsonSerializer.SerializeToUtf8Bytes(value), (await _remote.ReadAsync(key, Ct))?.ETag, Ct);

    [Fact]
    public async Task LocalState_KeepsEverythingUnderDotDeployify()
    {
        await using var session = new StateSession(_local.Path, "prod", null);
        await session.Inventory.UpdateAsync(inv => inv.Upsert("api", [], DateTimeOffset.UtcNow), Ct);
        await session.LockAsync("apply", Ct, autoRenew: false);

        session.IsRemote.ShouldBeFalse();
        session.JournalSink.ShouldBeNull();
        File.Exists(Path.Combine(_local.Path, ".deployify", "state", "deployify", "prod", "inventory.json")).ShouldBeTrue();
        File.Exists(Path.Combine(_local.Path, ".deployify", "state", "deployify", "prod", "lock.json")).ShouldBeTrue();

        await session.DisposeAsync();
        File.Exists(Path.Combine(_local.Path, ".deployify", "state", "deployify", "prod", "lock.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task Pull_ReplacesThisEnvironmentsParameters_AndLeavesOthersAlone()
    {
        ParameterStore.WriteSection(_local.Path, "prod", new Dictionary<string, string> { ["pg.value"] = "stale" });
        ParameterStore.WriteSection(_local.Path, "dev", new Dictionary<string, string> { ["pg.value"] = "dev-pw" });
        await PutRemoteAsync("deployify/prod/parameters.json", new Dictionary<string, string> { ["pg.value"] = "shared" });

        await using var session = Remote();
        await session.PullAsync(Ct);

        ParameterStore.ReadSection(_local.Path, "prod").ShouldBe(new Dictionary<string, string> { ["pg.value"] = "shared" });
        ParameterStore.ReadSection(_local.Path, "dev").ShouldBe(new Dictionary<string, string> { ["pg.value"] = "dev-pw" });
    }

    [Fact]
    public async Task Pull_WithNothingRemoteYet_KeepsTheLocalParameters_AndPushCopiesThemUp()
    {
        ParameterStore.WriteSection(_local.Path, "prod", new Dictionary<string, string> { ["pg.value"] = "local-pw" });

        await using var session = Remote();
        await session.PullAsync(Ct);
        await session.PushParametersAsync(Ct);

        var remote = JsonSerializer.Deserialize<Dictionary<string, string>>((await _remote.ReadAsync("deployify/prod/parameters.json", Ct))!.Data);
        remote.ShouldBe(new Dictionary<string, string> { ["pg.value"] = "local-pw" });
    }

    [Fact]
    public async Task Push_WithoutChanges_WritesNothing()
    {
        await PutRemoteAsync("deployify/prod/parameters.json", new Dictionary<string, string> { ["pg.value"] = "shared" });
        await using var session = Remote();
        await session.PullAsync(Ct);
        var before = (await _remote.ReadAsync("deployify/prod/parameters.json", Ct))!.ETag;

        await session.PushParametersAsync(Ct);

        (await _remote.ReadAsync("deployify/prod/parameters.json", Ct))!.ETag.ShouldBe(before);
    }

    [Fact]
    public async Task Push_MergesValuesGeneratedThisRun_IntoTheRemoteOnes()
    {
        await PutRemoteAsync("deployify/prod/parameters.json", new Dictionary<string, string> { ["pg.value"] = "shared" });
        await using var session = Remote();
        await session.PullAsync(Ct);

        // This run generates a new value; meanwhile another key appeared remotely.
        var local = ParameterStore.ReadSection(_local.Path, "prod");
        local["redis.value"] = "new-pw";
        ParameterStore.WriteSection(_local.Path, "prod", local);
        await PutRemoteAsync("deployify/prod/parameters.json", new Dictionary<string, string> { ["pg.value"] = "shared", ["kafka.value"] = "k" });

        await session.PushParametersAsync(Ct);

        var remote = JsonSerializer.Deserialize<Dictionary<string, string>>((await _remote.ReadAsync("deployify/prod/parameters.json", Ct))!.Data)!;
        remote.Keys.Order().ShouldBe(["kafka.value", "pg.value", "redis.value"]);
        remote["redis.value"].ShouldBe("new-pw");
    }

    [Fact]
    public async Task Journals_AreCopiedUp_OnEverySave_AndDownWhenMissingLocally()
    {
        await using (var session = Remote())
        {
            await using var journal = await RolloutJournal.BeginAsync(_local.Path, "prod", "Fake", [["api"]], ct: Ct,
                onSaved: session.JournalSink);
            journal.Record.State = RolloutState.Succeeded;
            await journal.SaveAsync(Ct);
            journal.SyncError.ShouldBeNull();
        }

        var keys = await _remote.ListAsync("deployify/prod/rollouts/", Ct);
        keys.Count.ShouldBe(1);

        // A fresh machine: no local journals.
        Directory.Delete(Path.Combine(_local.Path, RolloutJournal.DirectoryName), recursive: true);
        await using var other = Remote();
        await other.PullAsync(Ct);

        var listed = await RolloutJournal.ListAsync(_local.Path, "prod", Ct);
        listed.Count.ShouldBe(1);
        listed[0].Record.State.ShouldBe(RolloutState.Succeeded);
    }

    [Fact]
    public async Task AFailingRemoteCopy_IsReported_ButTheLocalJournalIsWritten()
    {
        await using var journal = await RolloutJournal.BeginAsync(_local.Path, "prod", "Fake", [["api"]], ct: Ct,
            onSaved: (_, _) => throw new StateBackendException("bucket unreachable"));

        journal.SyncError.ShouldBe("bucket unreachable");
        File.Exists(journal.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task EnvironmentsAndPrefixes_DoNotShareKeys()
    {
        await using var a = new StateSession(_local.Path, "prod", new StateBackendConfig { Prefix = "shop" });
        await using var b = new StateSession(_local.Path, "staging", new StateBackendConfig { Prefix = "shop" });

        a.Inventory.Key.ShouldBe("shop/prod/inventory.json");
        b.LockKey.ShouldBe("shop/staging/lock.json");
        a.RolloutsPrefix.ShouldBe("shop/prod/rollouts/");
    }

    public void Dispose()
    {
        _local.Dispose();
        _remoteDir.Dispose();
    }
}
