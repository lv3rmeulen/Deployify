using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Plugins;

/// <summary>
/// <c>deployify-plugins.lock.json</c>, next to deployify-state.json: which plugin, version and
/// exact files each plugin target must use. Commit it, so every machine and CI run deploys with
/// the same plugin. Written by <c>deployify plugin lock</c>. A plugin target it doesn't lock with
/// a hash for this platform is refused unless unverified plugins are allowed.
/// </summary>
/// <remarks>
/// See <see cref="PluginFiles"/> for what the hash covers. Platform-specific plugins (a
/// self-contained executable) get one hash per runtime ID, so run <c>deployify plugin lock</c>
/// once on each platform that deploys.
/// </remarks>
public sealed class PluginLockFile
{
    public const string FileName = "deployify-plugins.lock.json";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Format version of the file.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Locked plugins by target ID.</summary>
    public SortedDictionary<string, PluginLock> Plugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string PathFor(string projectDirectory) => Path.Combine(projectDirectory, FileName);

    /// <summary>The project's lock file, or null when it has none.</summary>
    public static PluginLockFile? Load(string projectDirectory)
    {
        var path = PathFor(projectDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var file = JsonSerializer.Deserialize<PluginLockFile>(File.ReadAllText(path), s_json) ?? new PluginLockFile();
            file.Plugins = new SortedDictionary<string, PluginLock>(file.Plugins, StringComparer.OrdinalIgnoreCase);
            return file;
        }
        catch (JsonException ex)
        {
            throw new PluginException($"{FileName} can't be read: {ex.Message}", ex);
        }
    }

    public async Task SaveAsync(string projectDirectory, CancellationToken ct = default)
        => await File.WriteAllTextAsync(PathFor(projectDirectory), JsonSerializer.Serialize(this, s_json) + Environment.NewLine, ct);

    public PluginLock? Find(string targetId) => Plugins.GetValueOrDefault(targetId);

    /// <summary>SHA-256 of a file, lowercase hex.</summary>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

/// <summary>One locked plugin.</summary>
public sealed record PluginLock
{
    /// <summary>The plugin name from its handshake.</summary>
    public required string Plugin { get; init; }

    /// <summary>The plugin version from its handshake; checked on every start.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// SHA-256 of the plugin's files (see <see cref="PluginFiles"/>) by platform: a runtime ID such
    /// as <c>linux-x64</c>, or <see cref="PluginFiles.AnyPlatform"/> for a plugin that is the same
    /// everywhere. Checked before the plugin starts.
    /// </summary>
    [JsonConverter(typeof(PlatformHashesConverter))]
    public SortedDictionary<string, string> Sha256 { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The locked hash for <paramref name="platform"/>, or null when there is none.</summary>
    public string? HashFor(string platform) => Sha256.GetValueOrDefault(platform);
}

/// <summary>
/// Reads <c>sha256</c> as an object of platform → hash. A single string, as earlier pre-releases
/// wrote, is read as no hashes: it didn't say which platform or which files it covered.
/// </summary>
internal sealed class PlatformHashesConverter : JsonConverter<SortedDictionary<string, string>>
{
    public override bool HandleNull => true;

    public override SortedDictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
            case JsonTokenType.String:
                return hashes;
            case JsonTokenType.StartObject:
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var platform = reader.GetString()!;
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } hash)
                    {
                        hashes[platform] = hash;
                    }
                }

                return hashes;
            default:
                throw new JsonException("'sha256' must be an object of platform → SHA-256.");
        }
    }

    public override void Write(Utf8JsonWriter writer, SortedDictionary<string, string> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (platform, hash) in value)
        {
            writer.WriteString(platform, hash);
        }

        writer.WriteEndObject();
    }
}
