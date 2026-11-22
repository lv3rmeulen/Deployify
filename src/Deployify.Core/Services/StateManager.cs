using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        // Keys are case-insensitive ("target" and "Target" are the same key), as the stability
        // policy promises. A file that sets the same key twice in different case is refused
        // instead of one of them winning silently.
        PropertyNameCaseInsensitive = true,
        AllowDuplicateProperties = false,
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

        // The version key is case-insensitive like every other key: "formatVersion": 2 must be
        // refused as a newer format, not read as a file without a version.
        var versionKeys = root.Select(p => p.Key)
            .Where(k => string.Equals(k, nameof(DeployifyStateFile.FormatVersion), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (versionKeys.Count > 1)
        {
            throw new StateFormatException(
                $"{path} sets {nameof(DeployifyStateFile.FormatVersion)} more than once ({string.Join(", ", versionKeys)}); keep one.");
        }

        var versionKey = versionKeys.SingleOrDefault() ?? nameof(DeployifyStateFile.FormatVersion);
        var version = StateFormat.ReadVersion(root, versionKey, StateFormat.StateFile, path);

        // Upgrades, one version at a time. 0 -> 1 only added the version field.
        if (version < 1)
        {
            version = 1;
        }

        root.Remove(versionKey);
        root[nameof(DeployifyStateFile.FormatVersion)] = version;

        // Layered environments: merge each with its bases before reading it.
        Dictionary<string, JsonObject>? own = null;
        if (EnvironmentInheritance.FindKey(root, nameof(DeployifyStateFile.Environments)) is { } envKey &&
            root[envKey] is JsonObject environments && EnvironmentInheritance.AnyLayered(environments, path))
        {
            var resolved = EnvironmentInheritance.Resolve(environments, path);
            own = [];
            foreach (var (name, layer) in resolved)
            {
                if (EnvironmentInheritance.ReadExtends(layer, name, path) is not null)
                {
                    own[name] = EnvironmentInheritance.WithoutMetadata((JsonObject)environments[name]!);
                }

                environments[name] = layer.DeepClone();
            }
        }

        DeployifyStateFile file;
        try
        {
            file = root.Deserialize<DeployifyStateFile>(s_jsonOptions)
                   ?? throw new InvalidDataException("Failed to deserialize state file.");
        }
        catch (JsonException ex)
        {
            // A duplicate key, or a value of the wrong type. Path is where in the file.
            throw new StateFormatException($"{path} can't be read{(ex.Path is { } at ? $" at {at}" : "")}: {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            // Two TargetConfig sections whose names differ only in case.
            throw new StateFormatException($"{path} can't be read: {ex.Message}", ex);
        }

        if (own is not null)
        {
            foreach (var (name, layer) in own)
            {
                var config = file.Environments[name];
                config.Loaded = new EnvironmentLayer(config.Extends!, layer, Serialize(config));
            }
        }

        file.FormatVersion = StateFormat.StateFile;
        return file;
    }

    /// <summary>
    /// An environment's effective settings (bases already merged in) as the state file spells them,
    /// without <c>extends</c> and <c>abstract</c>. Drift checks keep this with each deploy.
    /// </summary>
    public static JsonObject SerializeEnvironment(EnvironmentConfig config) => Serialize(config);

    /// <summary>Reads settings written by <see cref="SerializeEnvironment"/>.</summary>
    public static EnvironmentConfig DeserializeEnvironment(JsonObject node, string what)
    {
        try
        {
            return node.Deserialize<EnvironmentConfig>(s_jsonOptions)
                   ?? throw new StateFormatException($"{what} has no environment settings.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new StateFormatException($"{what} has environment settings this Deployify can't read: {ex.Message}", ex);
        }
    }

    private static JsonObject Serialize(EnvironmentConfig config)
        => EnvironmentInheritance.WithoutMetadata(JsonSerializer.SerializeToNode(config, s_jsonOptions)!.AsObject());

    public async Task SaveFileAsync(DeployifyStateFile stateFile, string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, StateFileName);
        // Write to a temp file and move it into place, so a failure part-way through
        // can never leave a truncated state file behind.
        var json = Write(stateFile);
        if (secrets is { Count: > 0 })
        {
            SecretLeakScanner.ThrowIfAny(
                SecretLeakScanner.ScanText(StateFileName, json, secrets.Snapshot()), "The state file");
        }

        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct);

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// The state file as JSON. An environment that extends another is written as its own layer:
    /// what the file already said for it plus what changed since it was read, not its merged
    /// settings, so it keeps inheriting everything else.
    /// </summary>
    public static string Write(DeployifyStateFile stateFile)
    {
        if (EnvironmentInheritance.Validate(stateFile.Environments) is { } problem)
        {
            throw new InvalidOperationException($"Can't save {StateFileName}: {problem}");
        }

        var layered = stateFile.Environments.Values.Any(e => e.Extends is not null);
        stateFile.FormatVersion = layered ? StateFormat.StateFile : StateFormat.StateFileWithoutLayers;
        if (!layered)
        {
            return JsonSerializer.Serialize(stateFile, s_jsonOptions);
        }

        var root = JsonSerializer.SerializeToNode(stateFile, s_jsonOptions)!.AsObject();
        var environments = root[nameof(DeployifyStateFile.Environments)]!.AsObject();
        var current = stateFile.Environments.ToDictionary(
            e => e.Key, e => environments[e.Key]!.AsObject().DeepClone().AsObject(), StringComparer.Ordinal);
        foreach (var (name, config) in stateFile.Environments)
        {
            if (config.Extends is not { } extends)
            {
                continue;
            }

            var loaded = config.Loaded?.Extends == extends ? config.Loaded : null;
            var layer = new JsonObject { [nameof(EnvironmentConfig.Extends)] = extends };
            if (config.Abstract)
            {
                layer[nameof(EnvironmentConfig.Abstract)] = true;
            }

            foreach (var (key, value) in EnvironmentInheritance.OwnLayer(current[name], current[extends], loaded).ToList())
            {
                layer[key] = value?.DeepClone();
            }

            environments[name] = layer;
        }

        return root.ToJsonString(s_jsonOptions);
    }

    /// <summary>
    /// A new environment that extends <paramref name="baseName"/>: its settings start as the
    /// base's, and saving writes only what is changed afterward.
    /// </summary>
    public static EnvironmentConfig CreateExtending(EnvironmentConfig baseConfig, string baseName)
    {
        var inherited = EnvironmentInheritance.Inheritable(JsonSerializer.SerializeToNode(baseConfig, s_jsonOptions)!.AsObject());
        var config = inherited.Deserialize<EnvironmentConfig>(s_jsonOptions)!;
        config.Extends = baseName;
        return config;
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

        if (config.Abstract)
        {
            var deployable = file.Environments.Where(e => !e.Value.Abstract).Select(e => e.Key).ToList();
            throw new KeyNotFoundException(
                $"Environment '{envName}' is abstract: it only holds settings other environments extend, " +
                $"and can't be deployed. Deployable: {(deployable.Count > 0 ? string.Join(", ", deployable) : "none")}.");
        }

        config.Name = envName;
        return (config, envName);
    }
}
