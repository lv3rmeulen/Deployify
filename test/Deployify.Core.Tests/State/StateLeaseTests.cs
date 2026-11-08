using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.State;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.State;

public sealed class StateLeaseTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Key = "deployify/prod/lock.json";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(3);

    private readonly TempDir _dir = new();
    private readonly LocalStateBackend _backend;
    private readonly ManualTime _time = ManualTime.At2026();

    public StateLeaseTests() => _backend = LocalStateBackend.ForStateDirectory(_dir.Path);

    private Task<StateLease> AcquireAsync(string operation = "apply", TimeSpan? wait = null, Action<string>? warn = null)
        => StateLease.AcquireAsync(_backend, Key, "prod", operation, Ttl, wait ?? TimeSpan.Zero,
            autoRenew: false, time: _time, onWarning: warn, ct: Ct);

    [Fact]
    public async Task TheLock_RecordsWhoHoldsIt_AndUntilWhen()
    {
        await using var lease = await AcquireAsync();

        var holder = await StateLease.PeekAsync(_backend, Key, _time, Ct);
        holder.ShouldNotBeNull();
        holder.Id.ShouldBe(lease.Info.Id);
        holder.Operation.ShouldBe("apply");
        holder.Who.ShouldBe($"{Environment.UserName}@{Environment.MachineName}");
        holder.ExpiresAt.ShouldBe(_time.Now + Ttl);
    }

    [Fact]
    public async Task AnExpiredLock_IsTakenOver_AndReportedAsThePreviousHolder()
    {
        var crashed = await AcquireAsync("apply");
        _time.Now += Ttl + TimeSpan.FromSeconds(1);

        await using var next = await AcquireAsync("destroy");

        next.Previous.ShouldNotBeNull();
        next.Previous.Id.ShouldBe(crashed.Info.Id);
        (await StateLease.PeekAsync(_backend, Key, _time, Ct))!.Id.ShouldBe(next.Info.Id);
    }

    [Fact]
    public async Task ALiveLock_IsNotTakenOver_EvenAMomentBeforeItExpires()
    {
        await using var holder = await AcquireAsync();
        _time.Now += Ttl - TimeSpan.FromSeconds(1);

        await Should.ThrowAsync<StateLockedException>(() => AcquireAsync());
    }

    [Fact]
    public async Task Renew_PushesTheExpiryOut()
    {
        await using var lease = await AcquireAsync();
        _time.Now += TimeSpan.FromMinutes(2);

        (await lease.RenewAsync(Ct)).ShouldBeTrue();

        (await StateLease.PeekAsync(_backend, Key, _time, Ct))!.ExpiresAt.ShouldBe(_time.Now + Ttl);
    }

    [Fact]
    public async Task AHolderWhoseLockWasTakenOver_LosesIt_AndDoesNotDeleteTheNewOne()
    {
        string? warning = null;
        var slow = await AcquireAsync(warn: w => warning = w);
        _time.Now += Ttl + TimeSpan.FromSeconds(1);
        await using var next = await AcquireAsync("destroy");

        (await slow.RenewAsync(Ct)).ShouldBeFalse();
        slow.Lost.ShouldBeTrue();
        warning.ShouldNotBeNull().ShouldContain("taken over");

        await slow.DisposeAsync();
        (await StateLease.PeekAsync(_backend, Key, _time, Ct))!.Id.ShouldBe(next.Info.Id);
    }

    [Fact]
    public async Task ForceUnlock_NeedsTheHoldersId()
    {
        var lease = await AcquireAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => StateLease.ForceUnlockAsync(_backend, Key, "not-it", Ct));
        (await StateLease.ForceUnlockAsync(_backend, Key, lease.Info.Id, Ct)).ShouldBeTrue();
        (await StateLease.ForceUnlockAsync(_backend, Key, lease.Info.Id, Ct)).ShouldBeFalse();

        await using var again = await AcquireAsync();
        await lease.DisposeAsync(); // its lock is gone; releasing must not remove the new one
        (await StateLease.PeekAsync(_backend, Key, _time, Ct))!.Id.ShouldBe(again.Info.Id);
    }

    [Fact]
    public async Task AnUnreadableLock_CountsAsFree()
    {
        await _backend.WriteAsync(Key, Encoding.UTF8.GetBytes("not json"), null, Ct);

        await using var lease = await AcquireAsync();

        lease.Previous.ShouldBeNull();
    }

    [Fact]
    public async Task Waiting_GetsTheLock_OnceTheHolderReleasesIt()
    {
        var holder = await StateLease.AcquireAsync(_backend, Key, "prod", "apply", Ttl, TimeSpan.Zero, autoRenew: false, ct: Ct);

        var waiting = StateLease.AcquireAsync(_backend, Key, "prod", "destroy", Ttl, TimeSpan.FromSeconds(30), autoRenew: false, ct: Ct);
        await Task.Delay(200, Ct);
        waiting.IsCompleted.ShouldBeFalse();
        await holder.DisposeAsync();

        await using var got = await waiting.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        got.Info.Operation.ShouldBe("destroy");
    }

    [Fact]
    public async Task AutoRenew_KeepsALongRunningLockAlive()
    {
        await using var lease = await StateLease.AcquireAsync(_backend, Key, "prod", "apply",
            TimeSpan.FromSeconds(3), TimeSpan.Zero, autoRenew: true, ct: Ct);
        var first = lease.Info.ExpiresAt;

        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);

        (await StateLease.PeekAsync(_backend, Key, ct: Ct))!.ExpiresAt.ShouldBeGreaterThan(first);
    }

    [Fact]
    public void TheRunUrl_ComesFromTheCiEnvironment()
    {
        var saved = Environment.GetEnvironmentVariable("DEPLOYIFY_RUN_URL");
        try
        {
            Environment.SetEnvironmentVariable("DEPLOYIFY_RUN_URL", "https://ci.example/run/7");
            LockInfo.DetectRunUrl().ShouldBe("https://ci.example/run/7");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEPLOYIFY_RUN_URL", saved);
        }
    }

    public void Dispose() => _dir.Dispose();
}
