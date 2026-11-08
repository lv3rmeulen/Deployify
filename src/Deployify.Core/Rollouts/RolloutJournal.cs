using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;

namespace Deployify.Core.Rollouts;

public enum RolloutState
{
    Running,
    Succeeded,
    Failed,
    RollingBack,
    RolledBack,
    RollbackFailed
}

public enum ServiceRolloutState
{
    Pending,
    Deploying,
    Verifying,
    Healthy,
    Failed,
    RevertedByPlatform,
    RolledBack,
    RollbackFailed
}

/// <summary>Everything one rollout did, as written to <c>.deployify/rollouts/{id}.json</c>.</summary>
public sealed class RolloutRecord
{
    public string Id { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public RolloutState State { get; set; } = RolloutState.Running;
    public string? Reason { get; set; }
    public List<List<string>> Waves { get; set; } = [];
    public List<ServiceRolloutRecord> Services { get; set; } = [];
    public List<RolloutEvent> Events { get; set; } = [];

    public ServiceRolloutRecord Service(string name)
        => Services.First(s => s.Name == name);
}

public sealed class ServiceRolloutRecord
{
    public string Name { get; set; } = string.Empty;
    public ServiceRolloutState State { get; set; } = ServiceRolloutState.Pending;

    /// <summary>What ran before; the rollback target.</summary>
    public ServiceSnapshot? Snapshot { get; set; }

    /// <summary>The revision this rollout deployed.</summary>
    public string? Revision { get; set; }

    public string? Detail { get; set; }
}

public sealed record RolloutEvent(DateTimeOffset At, string? Service, string Message);

/// <summary>
/// The on-disk record of one rollout, plus a per-environment lock so two rollouts can't change
/// the same environment at once. Every change is saved straight away, so if the process dies
/// part-way, the file says exactly which services were touched and what to restore them to.
/// Nothing secret goes in: snapshots hold references only.
/// </summary>
public sealed class RolloutJournal : IAsyncDisposable
{
    public const string DirectoryName = ".deployify/rollouts";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly FileStream _lock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private RolloutJournal(RolloutRecord record, string path, FileStream lockFile)
    {
        Record = record;
        Path = path;
        _lock = lockFile;
    }

    public RolloutRecord Record { get; }

    /// <summary>Full path of the journal file.</summary>
    public string Path { get; }

    /// <summary>
    /// Takes the environment's lock and writes the initial journal. Throws
    /// <see cref="InvalidOperationException"/> when another rollout holds the lock.
    /// </summary>
    public static async Task<RolloutJournal> BeginAsync(
        string stateDirectory, string environment, string target,
        IReadOnlyList<IReadOnlyList<string>> waves, DateTimeOffset? now = null, CancellationToken ct = default)
    {
        var started = now ?? DateTimeOffset.UtcNow;
        var dir = System.IO.Path.Combine(stateDirectory, DirectoryName);
        Directory.CreateDirectory(dir);
        var safeEnv = Sanitize(environment);

        var lockPath = System.IO.Path.Combine(dir, $"{safeEnv}.lock");
        FileStream lockFile;
        try
        {
            lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            throw new InvalidOperationException(
                $"Another rollout to '{environment}' is in progress (lock: {lockPath}). " +
                "If no other deployify process is running, delete the lock file and try again.");
        }

        var id = $"{started.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{safeEnv}";
        var record = new RolloutRecord
        {
            Id = id,
            Environment = environment,
            Target = target,
            StartedAt = started,
            Waves = [.. waves.Select(w => w.ToList())],
            Services = [.. waves.SelectMany(w => w).Select(s => new ServiceRolloutRecord { Name = s })]
        };

        var journal = new RolloutJournal(record, System.IO.Path.Combine(dir, $"{id}.json"), lockFile);
        await journal.SaveAsync(ct);
        return journal;
    }

    /// <summary>Adds an event and saves.</summary>
    public Task LogAsync(string? service, string message)
    {
        Record.Events.Add(new RolloutEvent(DateTimeOffset.UtcNow, service, message));
        return SaveAsync();
    }

    /// <summary>
    /// Writes the journal through a temp file. Never cancelled by default: a rollback that is
    /// running must still be recorded.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(Record, s_jsonOptions);
            var tmp = Path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, Path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task<RolloutRecord?> ReadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RolloutRecord>(stream, s_jsonOptions, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.DisposeAsync();
        _gate.Dispose();
    }

    private static string Sanitize(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var clean = string.Concat(name.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c));
        return clean.Length == 0 ? "default" : clean;
    }
}
