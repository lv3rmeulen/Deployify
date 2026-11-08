using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Core.State;

/// <summary>Who holds an environment's lock, as stored in the lock key.</summary>
public sealed class LockInfo
{
    public string Id { get; set; } = string.Empty;

    /// <summary>What the holder is doing: apply, destroy, rollback, …</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary><c>user@host</c>.</summary>
    public string Who { get; set; } = string.Empty;

    /// <summary>The CI run that holds it, when known.</summary>
    public string? RunUrl { get; set; }

    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>A lock for this process, with the CI run URL taken from the environment when there is one.</summary>
    public static LockInfo ForThisProcess(string operation, DateTimeOffset now, TimeSpan ttl) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        Operation = operation,
        Who = $"{Environment.UserName}@{Environment.MachineName}",
        RunUrl = DetectRunUrl(),
        AcquiredAt = now,
        ExpiresAt = now + ttl
    };

    /// <summary>One line for messages: <c>ci@runner-7, apply, since 14:02 (run: …)</c>.</summary>
    public string Describe(DateTimeOffset now)
    {
        var age = now - AcquiredAt;
        var since = age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : $"{AcquiredAt.ToLocalTime():g}";
        return $"{Who}, {Operation}, started {since} (lock id {Id})" + (RunUrl is null ? "" : $", run: {RunUrl}");
    }

    internal static string? DetectRunUrl()
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

        if (Env("DEPLOYIFY_RUN_URL") is { } custom)
        {
            return custom;
        }

        if (Env("GITHUB_RUN_ID") is { } runId && Env("GITHUB_REPOSITORY") is { } repo)
        {
            return $"{Env("GITHUB_SERVER_URL") ?? "https://github.com"}/{repo}/actions/runs/{runId}";
        }

        if (Env("CI_JOB_URL") is { } gitlab)
        {
            return gitlab;
        }

        if (Env("BUILD_BUILDID") is { } build && Env("SYSTEM_COLLECTIONURI") is { } collection && Env("SYSTEM_TEAMPROJECT") is { } project)
        {
            return $"{collection.TrimEnd('/')}/{Uri.EscapeDataString(project)}/_build/results?buildId={build}";
        }

        return null;
    }
}

/// <summary>Another run holds the lock.</summary>
public sealed class StateLockedException(string environment, LockInfo holder, DateTimeOffset now)
    : Exception($"Environment '{environment}' is locked by {holder.Describe(now)}. " +
                $"If that run is no longer going, wait for the lock to expire ({holder.ExpiresAt.ToLocalTime():T}) " +
                $"or run 'deployify state unlock {holder.Id}'.")
{
    public LockInfo Holder { get; } = holder;
}

/// <summary>
/// An environment lock built on <see cref="IStateBackend"/> compare-and-swap, so every backend
/// gets the same semantics: the lock is a key holding a <see cref="LockInfo"/> with an expiry;
/// taking it is "create if absent" (or "replace if expired"); the holder renews it in the
/// background, and a run that crashes frees it once it expires. A holder that loses the lock
/// (its renewal hits someone else's write) stops renewing and reports it; state writes are
/// compare-and-swap anyway, so it can't then overwrite the new holder's changes.
/// </summary>
public sealed class StateLease : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IStateBackend _backend;
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string>? _onWarning;
    private string _etag;
    private Task? _renewal;
    private bool _released;

    private StateLease(IStateBackend backend, string key, LockInfo info, string etag, TimeSpan ttl,
        TimeProvider time, LockInfo? previous, Action<string>? onWarning)
    {
        _backend = backend;
        Key = key;
        Info = info;
        _etag = etag;
        _ttl = ttl;
        _time = time;
        Previous = previous;
        _onWarning = onWarning;
    }

    public string Key { get; }
    public LockInfo Info { get; }

    /// <summary>An expired lock this one replaced: a run that ended without releasing it.</summary>
    public LockInfo? Previous { get; }

    /// <summary>True once someone else has taken the lock away from this holder.</summary>
    public bool Lost { get; private set; }

    /// <summary>
    /// Takes the lock at <paramref name="key"/>, waiting up to <paramref name="wait"/> while
    /// another live holder has it. Throws <see cref="StateLockedException"/> when it stays taken.
    /// </summary>
    public static async Task<StateLease> AcquireAsync(
        IStateBackend backend, string key, string environment, string operation, TimeSpan ttl, TimeSpan wait,
        bool autoRenew = true, TimeProvider? time = null, Action<string>? onWarning = null, CancellationToken ct = default)
    {
        time ??= TimeProvider.System;
        var deadline = time.GetUtcNow() + wait;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var now = time.GetUtcNow();
            var info = LockInfo.ForThisProcess(operation, now, ttl);
            var data = JsonSerializer.SerializeToUtf8Bytes(info, s_json);

            var current = await backend.ReadAsync(key, ct);
            LockInfo? holder = null;
            try
            {
                if (current is null)
                {
                    var etag = await backend.WriteAsync(key, data, null, ct);
                    return Start(new StateLease(backend, key, info, etag, ttl, time, null, onWarning), autoRenew);
                }

                holder = Parse(current.Data);
                if (holder is null || holder.ExpiresAt <= now)
                {
                    var etag = await backend.WriteAsync(key, data, current.ETag, ct);
                    return Start(new StateLease(backend, key, info, etag, ttl, time, holder, onWarning), autoRenew);
                }
            }
            catch (StateConflictException)
            {
                // Someone else got there first; look again.
                continue;
            }

            if (now >= deadline)
            {
                throw new StateLockedException(environment, holder!, now);
            }

            var pause = TimeSpan.FromSeconds(Math.Min(5, (deadline - now).TotalSeconds));
            await Task.Delay(pause, time, ct);
        }
    }

    /// <summary>The current holder, or null when the lock is free (an expired holder counts as free).</summary>
    public static async Task<LockInfo?> PeekAsync(IStateBackend backend, string key, TimeProvider? time = null, CancellationToken ct = default)
    {
        var current = await backend.ReadAsync(key, ct);
        var holder = current is null ? null : Parse(current.Data);
        return holder is not null && holder.ExpiresAt > (time ?? TimeProvider.System).GetUtcNow() ? holder : null;
    }

    /// <summary>
    /// Removes the lock if <paramref name="lockId"/> still holds it. Returns false when the lock
    /// is already free; throws when someone else holds it now, so a stale id can't free a live lock.
    /// </summary>
    public static async Task<bool> ForceUnlockAsync(IStateBackend backend, string key, string lockId, CancellationToken ct = default)
    {
        var current = await backend.ReadAsync(key, ct);
        if (current is null)
        {
            return false;
        }

        var holder = Parse(current.Data);
        if (holder is not null && holder.Id != lockId)
        {
            throw new InvalidOperationException(
                $"The lock is held by {holder.Describe(DateTimeOffset.UtcNow)}, not by lock id {lockId}.");
        }

        await backend.DeleteAsync(key, current.ETag, ct);
        return true;
    }

    /// <summary>Pushes the expiry out by one TTL. Marks the lease lost when someone else has taken it.</summary>
    public async Task<bool> RenewAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Lost || _released)
            {
                return false;
            }

            Info.ExpiresAt = _time.GetUtcNow() + _ttl;
            try
            {
                _etag = await _backend.WriteAsync(Key, JsonSerializer.SerializeToUtf8Bytes(Info, s_json), _etag, ct);
                return true;
            }
            catch (StateConflictException)
            {
                Lost = true;
                _onWarning?.Invoke("The state lock was taken over by another run (it had expired). " +
                                   "State writes are compare-and-swap, so this run can't overwrite theirs; check the result when it finishes.");
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops renewing and deletes the lock. Never throws: an unreleased lock simply expires.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_released)
        {
            return;
        }

        await _stop.CancelAsync();
        if (_renewal is not null)
        {
            try
            {
                await _renewal;
            }
            catch
            {
                // Renewal errors were already reported.
            }
        }

        await _gate.WaitAsync();
        try
        {
            _released = true;
            if (!Lost)
            {
                await _backend.DeleteAsync(Key, _etag, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _onWarning?.Invoke($"Could not release the state lock ({ex.Message}); it expires at {Info.ExpiresAt.ToLocalTime():T}.");
        }
        finally
        {
            _gate.Release();
            _stop.Dispose();
        }
    }

    private static StateLease Start(StateLease lease, bool autoRenew)
    {
        if (autoRenew)
        {
            lease._renewal = lease.RenewLoopAsync();
        }

        return lease;
    }

    private async Task RenewLoopAsync()
    {
        var interval = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, _ttl.Ticks / 3));
        while (!_stop.IsCancellationRequested && !Lost)
        {
            try
            {
                await Task.Delay(interval, _time, _stop.Token);
                await RenewAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A blip: try again next time round, while the lock hasn't expired yet.
                _onWarning?.Invoke($"Could not renew the state lock: {ex.Message}");
            }
        }
    }

    internal static LockInfo? Parse(byte[] data)
    {
        try
        {
            return JsonSerializer.Deserialize<LockInfo>(data, s_json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
