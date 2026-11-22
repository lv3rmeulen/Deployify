using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deployify.Core.State;

/// <summary>
/// Format versions of the files Deployify keeps state in, and the checks that go with them.
/// Every file records the version it was written in. A file without one was written before
/// versioning (the 1.0.0 alphas) and counts as version 0; it is upgraded in memory when read
/// and written back in the current format the next time Deployify saves it. A file from a
/// newer version is refused rather than read and then overwritten with what this version
/// understands of it.
/// </summary>
/// <remarks>
/// To change a format: bump its constant, and teach its reader to upgrade the previous
/// version (each reader upgrades one version at a time, oldest first).
/// </remarks>
public static class StateFormat
{
    /// <summary>
    /// deployify-state.json. 0: alpha, no version field. 1: <c>FormatVersion</c> added.
    /// 2: environments can extend each other (<c>Extends</c>). A file is written as version 2 only
    /// when an environment extends another, so a release that can't merge layers refuses it
    /// instead of deploying an environment without its base's settings; otherwise it is written
    /// as <see cref="StateFileWithoutLayers"/>.
    /// </summary>
    public const int StateFile = 2;

    /// <summary>The version a state file in which no environment extends another is written in.</summary>
    public const int StateFileWithoutLayers = 1;

    /// <summary>{env}/inventory.json. 0: alpha, no version field. 1: <c>formatVersion</c> added.</summary>
    public const int Inventory = 1;

    /// <summary>{env}/images.json. 0: alpha, a bare map of images. 1: <c>{ formatVersion, images }</c>.</summary>
    public const int Images = 1;

    /// <summary>{env}/parameters.json. 0: alpha, a bare map of values. 1: <c>{ formatVersion, parameters }</c>.</summary>
    public const int Parameters = 1;

    /// <summary>{env}/releases/{time}-{id}.json, one release record each. 1: the first version; there is no version 0.</summary>
    public const int Releases = 1;

    /// <summary>Property name in the camelCase backend files.</summary>
    public const string VersionProperty = "formatVersion";

    /// <summary>
    /// The version recorded in <paramref name="root"/>'s <paramref name="property"/>, or 0 when
    /// it has none. Throws <see cref="StateFormatException"/> for a version this Deployify can't
    /// read: newer than <paramref name="supported"/>, or not a whole number.
    /// </summary>
    public static int ReadVersion(JsonObject root, string property, int supported, string what)
    {
        if (!root.TryGetPropertyValue(property, out var node) || node is null)
        {
            return 0;
        }

        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number ||
            !value.TryGetValue<int>(out var version) || version < 0)
        {
            throw new StateFormatException(
                $"{what} has an invalid {property} ({node.ToJsonString()}); expected a whole number. " +
                "Fix or restore the file; Deployify did not change it.");
        }

        EnsureSupported(version, supported, what);
        return version;
    }

    /// <summary>Throws <see cref="StateFormatException"/> when <paramref name="version"/> is newer than <paramref name="supported"/>.</summary>
    public static void EnsureSupported(int version, int supported, string what)
    {
        if (version > supported)
        {
            throw new StateFormatException(
                $"{what} is in format version {version}, but this Deployify ({ToolVersion}) only understands " +
                $"versions up to {supported}. It was written by a newer Deployify; update Deployify " +
                "(dotnet tool update --global deployify) to work with it. Deployify did not change the file.");
        }
    }

    /// <summary>Parses <paramref name="data"/> as a JSON object, or throws <see cref="StateFormatException"/>.</summary>
    public static JsonObject ParseObject(ReadOnlySpan<byte> data, string what)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(data);
        }
        catch (JsonException ex)
        {
            throw new StateFormatException($"{what} is not valid JSON: {ex.Message}", ex);
        }

        return node as JsonObject
               ?? throw new StateFormatException($"{what} is not a JSON object.");
    }

    /// <summary>
    /// Reads a backend file that is a map of entries: version 0 is the bare map, version 1 and
    /// later wrap it as <c>{ "formatVersion": n, "<paramref name="entriesProperty"/>": { ... } }</c>.
    /// A version-0 map can never hold a numeric <c>formatVersion</c> (its values are strings or
    /// objects), so the two shapes can't be confused.
    /// </summary>
    public static JsonObject ReadEntries(byte[] data, string entriesProperty, int supported, string what)
    {
        var root = ParseObject(data, what);
        var isEnvelope = root.TryGetPropertyValue(VersionProperty, out var v) &&
                         v?.GetValueKind() is JsonValueKind.Number;
        if (!isEnvelope)
        {
            // Version 0: the whole object is the map.
            return root;
        }

        ReadVersion(root, VersionProperty, supported, what);
        return root[entriesProperty] switch
        {
            JsonObject entries => entries,
            null => new JsonObject(),
            _ => throw new StateFormatException($"{what} has an invalid '{entriesProperty}'; expected an object.")
        };
    }

    /// <summary>Wraps <paramref name="entries"/> in the current envelope; see <see cref="ReadEntries"/>.</summary>
    public static byte[] WriteEntries<T>(int version, string entriesProperty, T entries, JsonSerializerOptions options)
    {
        var root = new JsonObject
        {
            [VersionProperty] = version,
            [entriesProperty] = JsonSerializer.SerializeToNode(entries, options)
        };
        return JsonSerializer.SerializeToUtf8Bytes(root, options);
    }

    /// <summary>Reads parameters.json (any supported version).</summary>
    public static Dictionary<string, string> ReadParameters(byte[] data, string what)
    {
        var entries = ReadEntries(data, "parameters", Parameters, what);
        try
        {
            return new Dictionary<string, string>(
                entries.Deserialize<Dictionary<string, string>>() ?? [], StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new StateFormatException($"{what} does not hold parameter values: {ex.Message}", ex);
        }
    }

    /// <summary>Writes parameters.json in the current format.</summary>
    public static byte[] WriteParameters(IReadOnlyDictionary<string, string> values, JsonSerializerOptions options)
        => WriteEntries(Parameters, "parameters", values, options);

    /// <summary>The running Deployify's version, for messages.</summary>
    public static string ToolVersion { get; } = ReadToolVersion();

    private static string ReadToolVersion()
    {
        var version = typeof(StateFormat).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(version))
        {
            return "unknown version";
        }

        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }
}

/// <summary>
/// A state file is in a format this Deployify can't read, typically because a newer Deployify
/// wrote it. The message says which file and what to do; nothing was changed.
/// </summary>
public sealed class StateFormatException(string message, Exception? inner = null) : Exception(message, inner);
