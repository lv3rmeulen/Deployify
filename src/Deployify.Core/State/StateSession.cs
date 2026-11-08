using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Rollouts;
using Deployify.Core.Services;

namespace Deployify.Core.State;

/// <summary>
/// One command's view of an environment's deployment state. The inventory and the lock always
/// live in the backend (local files by default). With a remote backend the backend is also the
/// authoritative copy of the generated parameter values and the rollout journals; commands keep
/// working on the local files as before, and the session copies them in when it opens
/// (<see cref="PullAsync"/>) and back out as they change.
/// </summary>
/// <remarks>
/// Keys: <c>{prefix}/{env}/inventory.json</c>, <c>{prefix}/{env}/parameters.json</c>,
/// <c>{prefix}/{env}/lock.json</c> and <c>{prefix}/{env}/rollouts/{id}.json</c>.
/// </remarks>
public sealed class StateSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private readonly TimeProvider _time;
    private readonly Action<string>? _onWarning;
    private Dictionary<string, string>? _pulledParameters;

    public StateSession(string stateDirectory, string environment, StateBackendConfig? config,
        IStateBackend? backend = null, TimeProvider? time = null, Action<string>? onWarning = null)
    {
        StateDirectory = stateDirectory;
        Environment = environment;
        Config = config ?? new StateBackendConfig();
        Backend = backend ?? StateBackendFactory.Create(Config, stateDirectory);
        _time = time ?? TimeProvider.System;
        _onWarning = onWarning;

        var prefix = string.IsNullOrWhiteSpace(Config.Prefix) ? "" : Config.Prefix.Trim('/') + "/";
        Root = $"{prefix}{environment}/";
        Inventory = new InventoryStore(Backend, Root + "inventory.json", environment, _time);
    }

    public string StateDirectory { get; }
    public string Environment { get; }
    public StateBackendConfig Config { get; }
    public IStateBackend Backend { get; }

    /// <summary>Key prefix of this environment, ending in a slash.</summary>
    public string Root { get; }

    public bool IsRemote => Config.IsRemote;

    public InventoryStore Inventory { get; }

    public string LockKey => Root + "lock.json";
    public string ParametersKey => Root + "parameters.json";
    public string RolloutsPrefix => Root + "rollouts/";

    /// <summary>The lock this session holds, or null.</summary>
    public StateLease? Lease { get; private set; }

    /// <summary>
    /// Takes the environment lock for <paramref name="operation"/>. Throws
    /// <see cref="StateLockedException"/> when another live run holds it.
    /// </summary>
    public async Task<StateLease> LockAsync(string operation, CancellationToken ct = default, bool autoRenew = true)
    {
        if (Lease is not null)
        {
            return Lease;
        }

        Lease = await StateLease.AcquireAsync(Backend, LockKey, Environment, operation,
            TimeSpan.FromSeconds(Math.Max(10, Config.LockTtlSeconds)),
            TimeSpan.FromSeconds(Math.Max(0, Config.LockWaitSeconds)),
            autoRenew, _time, _onWarning, ct);
        if (Lease.Previous is { } previous)
        {
            _onWarning?.Invoke($"Took over an expired lock: {previous.Describe(_time.GetUtcNow())} ended without releasing it.");
        }

        return Lease;
    }

    /// <summary>
    /// Copies remote parameters and journals into the local files. A no-op for local state.
    /// Parameters already in remote state replace the local section for this environment;
    /// when remote state has none yet, the local ones are kept and pushed on the next apply.
    /// </summary>
    public async Task PullAsync(CancellationToken ct = default)
    {
        if (!IsRemote)
        {
            return;
        }

        var blob = await Backend.ReadAsync(ParametersKey, ct);
        if (blob is not null)
        {
            var remote = JsonSerializer.Deserialize<Dictionary<string, string>>(blob.Data) ?? [];
            if (!SameValues(remote, ParameterStore.ReadSection(StateDirectory, Environment)))
            {
                ParameterStore.WriteSection(StateDirectory, Environment, remote);
            }

            _pulledParameters = remote;
        }
        else
        {
            _pulledParameters = [];
        }

        await PullJournalsAsync(ct);
    }

    /// <summary>
    /// Copies this environment's generated parameter values to remote state when they changed
    /// since <see cref="PullAsync"/>. Values made in this run win over remote ones of the same
    /// name: they are what was just deployed.
    /// </summary>
    public async Task PushParametersAsync(CancellationToken ct = default)
    {
        if (!IsRemote)
        {
            return;
        }

        var local = ParameterStore.ReadSection(StateDirectory, Environment);
        if (_pulledParameters is not null && SameValues(local, _pulledParameters))
        {
            return;
        }

        await PutAsync(ParametersKey, current =>
        {
            var merged = current is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, string>>(current) ?? [];
            foreach (var (k, v) in local)
            {
                merged[k] = v;
            }

            return JsonSerializer.SerializeToUtf8Bytes(merged, s_json);
        }, ct);
        _pulledParameters = local;
    }

    /// <summary>Hook for <see cref="RolloutJournal"/>: copies each save to remote state. Null for local state.</summary>
    public Func<RolloutRecord, CancellationToken, Task>? JournalSink
        => IsRemote ? PushJournalAsync : null;

    public async Task PushJournalAsync(RolloutRecord record, CancellationToken ct = default)
    {
        var data = RolloutJournal.Serialize(record);
        await PutAsync(RolloutsPrefix + record.Id + ".json", _ => data, ct);
    }

    /// <summary>
    /// Writes a key whatever it holds now, re-reading on conflict. <paramref name="build"/> gets
    /// the current value (or null) and returns the new one.
    /// </summary>
    public async Task PutAsync(string key, Func<byte[]?, byte[]> build, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var current = await Backend.ReadAsync(key, ct);
            try
            {
                await Backend.WriteAsync(key, build(current?.Data), current?.ETag, ct);
                return;
            }
            catch (StateConflictException) when (attempt < 4)
            {
            }
        }
    }

    /// <summary>
    /// Downloads journals of this environment that aren't on disk yet, and ones whose local copy
    /// still says Running (another machine may have finished them since).
    /// </summary>
    private async Task PullJournalsAsync(CancellationToken ct)
    {
        var dir = Path.Combine(StateDirectory, RolloutJournal.DirectoryName);
        foreach (var key in await Backend.ListAsync(RolloutsPrefix, ct))
        {
            var id = Path.GetFileNameWithoutExtension(key[RolloutsPrefix.Length..]);
            if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                continue;
            }

            var path = Path.Combine(dir, id + ".json");
            if (File.Exists(path))
            {
                try
                {
                    if (await RolloutJournal.ReadAsync(path, ct) is { State: not RolloutState.Running })
                    {
                        continue;
                    }
                }
                catch (JsonException)
                {
                    // Unreadable locally: take the remote copy.
                }
            }

            if (await Backend.ReadAsync(key, ct) is { } blob && RolloutJournal.Deserialize(blob.Data) is { } record &&
                record.Environment == Environment)
            {
                Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, blob.Data, ct);
                File.Move(tmp, path, overwrite: true);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Lease is not null)
        {
            await Lease.DisposeAsync();
            Lease = null;
        }

        (Backend as IDisposable)?.Dispose();
    }

    private static bool SameValues(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
