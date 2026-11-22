using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Core.State;

/// <summary>
/// An environment's release history, kept in its deployment state as one small object per
/// release: <c>{prefix}/{env}/releases/{yyyyMMddTHHmmssfffZ}-{id}.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// One object per release instead of one growing file: each record is written with a
/// create-only write, so two CI jobs releasing at once never conflict and never retry, and no
/// object grows past the backends' value limits (512 KB in Consul by default, about 1 MiB for a
/// Kubernetes Secret). The key starts with the time, so listing the prefix in ordinal order is
/// listing the history oldest first.
/// </para>
/// <para>
/// With the local backend the history lives only on the machine that ran the release; a team
/// that wants a shared history needs remote state.
/// </para>
/// </remarks>
public static class ReleaseLog
{
    /// <summary>Records kept per environment when nothing else is configured.</summary>
    public const int DefaultKeep = 200;

    /// <summary>The <c>keep</c> value that turns the history off: nothing is recorded.</summary>
    public const int Disabled = -1;

    /// <summary>True when <paramref name="keep"/> is <see cref="Disabled"/>.</summary>
    public static bool IsDisabled(int keep) => keep == Disabled;

    /// <summary>
    /// Why <paramref name="keep"/> isn't a valid <c>state.history.keep</c>, or null when it is: a
    /// number of records, 0 for all of them, or -1 for none. Other negative numbers are refused
    /// rather than read as "off", so a typo can't quietly stop the audit trail.
    /// </summary>
    public static string? Validate(int keep)
        => keep >= Disabled ? null
            : $"state.history.keep is {keep}. Use the number of release records to keep per environment, " +
              "0 to keep them all, or -1 to record none.";

    private const string TimeFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static string Prefix(StateSession state) => state.Root + "releases/";

    public static string Key(StateSession state, ReleaseRecord record)
        => Prefix(state) + record.At.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture) + "-" + record.Id + ".json";

    /// <summary>A new record id: 12 lowercase hex characters.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Stores <paramref name="record"/>, then deletes the oldest records beyond
    /// <paramref name="keep"/> (0 keeps everything). With <see cref="Disabled"/> nothing is stored
    /// and false is returned; records already stored are left alone. Any other negative value throws.
    /// The record is written create-only: an existing record is never overwritten.
    /// </summary>
    public static async Task<bool> AppendAsync(StateSession state, ReleaseRecord record, int keep = DefaultKeep, CancellationToken ct = default)
    {
        if (Validate(keep) is { } problem)
        {
            throw new ArgumentOutOfRangeException(nameof(keep), keep, problem);
        }

        if (IsDisabled(keep))
        {
            return false;
        }

        if (record.Environment != state.Environment)
        {
            throw new ArgumentException(
                $"The record is for environment '{record.Environment}' but the state session is for '{state.Environment}'.",
                nameof(record));
        }

        if (string.IsNullOrEmpty(record.Id) || record.Id.Contains('/'))
        {
            throw new ArgumentException("A release record needs an id without slashes.", nameof(record));
        }

        await state.Backend.WriteAsync(Key(state, record), Write(record), expectedETag: null, ct);

        if (keep > 0)
        {
            await PruneAsync(state, keep, ct);
        }

        return true;
    }

    /// <summary>
    /// The newest <paramref name="limit"/> records, newest first. Throws
    /// <see cref="StateFormatException"/> when one was written by a newer Deployify.
    /// </summary>
    public static async Task<IReadOnlyList<ReleaseRecord>> ListAsync(StateSession state, int limit = 20, CancellationToken ct = default)
    {
        var keys = await RecordKeysAsync(state, ct);
        var records = new List<ReleaseRecord>();
        foreach (var key in keys.AsEnumerable().Reverse().Take(Math.Max(0, limit)))
        {
            // Pruned by another run between the listing and the read: skip it.
            if (await state.Backend.ReadAsync(key, ct) is { } blob)
            {
                records.Add(Read(blob.Data, $"Release record '{key}' in {state.Backend.Description}"));
            }
        }

        return records;
    }

    /// <summary>Deletes the oldest records beyond <paramref name="keep"/> and returns how many it deleted.</summary>
    public static async Task<int> PruneAsync(StateSession state, int keep, CancellationToken ct = default)
    {
        var keys = await RecordKeysAsync(state, ct);
        var excess = keys.Count - keep;
        for (var i = 0; i < excess; i++)
        {
            // Unconditional: records never change, and a key another run already pruned is not an error.
            await state.Backend.DeleteAsync(keys[i], expectedETag: null, ct);
        }

        return Math.Max(0, excess);
    }

    /// <summary>Serializes a record in the current format.</summary>
    public static byte[] Write(ReleaseRecord record)
    {
        var node = JsonSerializer.SerializeToNode(record, s_json)!.AsObject();
        var root = new JsonObject { [StateFormat.VersionProperty] = StateFormat.Releases };
        foreach (var (name, value) in node.ToList())
        {
            node.Remove(name);
            root[name] = value;
        }

        return JsonSerializer.SerializeToUtf8Bytes(root, s_json);
    }

    /// <summary>
    /// Reads a record of any supported format version. Throws <see cref="StateFormatException"/>
    /// for one written by a newer Deployify, or one that isn't a release record.
    /// </summary>
    public static ReleaseRecord Read(byte[] data, string what)
    {
        var root = StateFormat.ParseObject(data, what);
        if (StateFormat.ReadVersion(root, StateFormat.VersionProperty, StateFormat.Releases, what) < 1)
        {
            // Release records were versioned from the start, so there is no version 0 to upgrade.
            throw new StateFormatException($"{what} has no {StateFormat.VersionProperty}, so it is not a release record.");
        }

        try
        {
            return root.Deserialize<ReleaseRecord>(s_json)
                   ?? throw new StateFormatException($"{what} is empty.");
        }
        catch (JsonException ex)
        {
            throw new StateFormatException($"{what} is not a valid release record: {ex.Message}", ex);
        }
    }

    /// <summary>Record keys oldest first. Anything else under the prefix is ignored.</summary>
    private static async Task<List<string>> RecordKeysAsync(StateSession state, CancellationToken ct)
    {
        var prefix = Prefix(state);
        return [.. (await state.Backend.ListAsync(prefix, ct))
            .Where(k => k.Length > prefix.Length && k.EndsWith(".json", StringComparison.Ordinal) &&
                        k.IndexOf('/', prefix.Length) < 0)
            .Order(StringComparer.Ordinal)];
    }
}
