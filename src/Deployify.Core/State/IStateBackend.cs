using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Core.State;

/// <summary>
/// A key/value store for deployment state with compare-and-swap writes. That one primitive is
/// enough for everything else: <see cref="StateLease"/> builds the environment lock on top of
/// it, and <see cref="InventoryStore"/> uses it so a write never silently overwrites one it
/// didn't read. Keys are relative paths such as <c>deployify/prod/inventory.json</c>.
/// </summary>
public interface IStateBackend
{
    /// <summary>Human-readable location, e.g. <c>s3://bucket</c>, for messages.</summary>
    string Description { get; }

    /// <summary>The value and its version tag, or null when the key doesn't exist.</summary>
    Task<StateBlob?> ReadAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="data"/> only if the key is still at <paramref name="expectedETag"/>
    /// (null: only if the key doesn't exist yet), and returns the new tag. Throws
    /// <see cref="StateConflictException"/> when someone else wrote in between.
    /// </summary>
    Task<string> WriteAsync(string key, byte[] data, string? expectedETag, CancellationToken ct = default);

    /// <summary>
    /// Deletes the key if it is still at <paramref name="expectedETag"/>; with null, deletes it
    /// whatever it holds. A key that is already gone is not an error. Throws
    /// <see cref="StateConflictException"/> when the tag no longer matches.
    /// </summary>
    Task DeleteAsync(string key, string? expectedETag, CancellationToken ct = default);

    /// <summary>All keys that start with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default);
}

/// <summary>A stored value with the version tag a conditional write must quote.</summary>
public sealed record StateBlob(byte[] Data, string ETag);

/// <summary>Someone else changed the key since it was read; re-read and try again.</summary>
public sealed class StateConflictException(string key, string? detail = null)
    : Exception($"State '{key}' was changed by someone else since it was read{(detail is null ? "." : $": {detail}")}")
{
    public string Key { get; } = key;
}

/// <summary>The backend itself failed (unreachable, access denied, bad configuration).</summary>
public sealed class StateBackendException(string message, Exception? inner = null) : Exception(message, inner);
