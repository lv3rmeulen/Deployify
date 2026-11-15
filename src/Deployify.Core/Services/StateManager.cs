using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.State;

namespace Deployify.Core.Services;

/// <summary>
/// Reads and writes deployify-state.json. When a <see cref="SecretRegistry"/> is supplied, a save
/// is refused if the serialized file contains any secret value resolved during this run.
/// </summary>
public sealed class StateManager(SecretRegistry? secrets = null) : IStateManager
{
    private const string StateFileName = "deployify-state.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public bool Exists(string directory)
        => File.Exists(Path.Combine(directory, StateFileName));

    public async Task<DeployifyStateFile> LoadFileAsync(string directory, CancellationToken ct = default)
    {
        var path = Path.Combine(directory, StateFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"State file not found at '{path}'. Run 'deployify init' first.");
        }

        return Read(await File.ReadAllBytesAsync(path, ct), path);
    }

    /// <summary>
    /// Reads a state file of any supported format version, upgraded to the current one (it is
    /// written in the current format on the next save). Throws <see cref="StateFormatException"/>
    /// for a file written by a newer Deployify.
    /// </summary>
    public static DeployifyStateFile Read(byte[] data, string path)
    {
        var root = StateFormat.ParseObject(data, path);
        var version = StateFormat.ReadVersion(root, nameof(DeployifyStateFile.FormatVersion), StateFormat.StateFile, path);

        // Upgrades, one version at a time. 0 -> 1 only added the version field.
        if (version < 1)
        {
            version = 1;
        }

        root[nameof(DeployifyStateFile.FormatVersion)] = version;
        var file = root.Deserialize<DeployifyStateFile>(s_jsonOptions)
                   ?? throw new InvalidDataException("Failed to deserialize state file.");
        file.FormatVersion = StateFormat.StateFile;
        return file;
    }

    public async Task SaveFileAsync(DeployifyStateFile stateFile, string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, StateFileName);
        // Write to a temp file and move it into place, so a failure part-way through
        // can never leave a truncated state file behind.
        stateFile.FormatVersion = StateFormat.StateFile;
        var json = JsonSerializer.Serialize(stateFile, s_jsonOptions);
        if (secrets is { Count: > 0 })
        {
            SecretLeakScanner.ThrowIfAny(
                SecretLeakScanner.ScanText(StateFileName, json, secrets.Snapshot()), "The state file");
        }

        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct);

        File.Move(tmp, path, overwrite: true);
    }

    public async Task<(EnvironmentConfig Config, string EnvName)> LoadActiveAsync(
        string directory, string? envOverride = null, CancellationToken ct = default)
    {
        var file = await LoadFileAsync(directory, ct);
        var envName = envOverride ?? file.ActiveEnvironment;
        if (!file.Environments.TryGetValue(envName, out var config))
        {
            throw new KeyNotFoundException(
                $"Environment '{envName}' not found. Available: {string.Join(", ", file.Environments.Keys)}");
        }

        config.Name = envName;
        return (config, envName);
    }
}
