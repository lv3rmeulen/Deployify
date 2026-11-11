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
/// <c>deployify-plugins.lock.json</c>, next to deployify-state.json: which plugin version (and
/// optionally which exact file) each plugin target must use. Commit it, so every machine and CI
/// run deploys with the same plugin. Written by <c>deployify plugin lock</c>.
/// </summary>
/// <remarks>
/// The hash covers the file Deployify starts. For a self-contained executable or a <c>.dll</c>
/// that is the plugin itself. A <c>dotnet tool</c> shim differs per machine, so lock those by
/// version only (<c>deployify plugin lock --no-hash</c>).
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

    /// <summary>SHA-256 of the file Deployify starts; checked before starting it. Null to lock by version only.</summary>
    public string? Sha256 { get; init; }
}
