using System.Text.Json;
using System.Text.Json.Serialization;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;

namespace Deployify.Core.Services;

public sealed class StateManager : IStateManager
{
    private const string StateFileName = "deployify-state.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public bool Exists(string directory) =>
        File.Exists(Path.Combine(directory, StateFileName));

    public async Task<DeployifyStateFile> LoadFileAsync(string directory, CancellationToken ct = default)
    {
        var path = Path.Combine(directory, StateFileName);
        if (!File.Exists(path))
        {
	        throw new FileNotFoundException($"State file not found at '{path}'. Run 'deployify init' first.");
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<DeployifyStateFile>(stream, s_jsonOptions, ct)
               ?? throw new InvalidDataException("Failed to deserialize state file.");
    }

    public async Task SaveFileAsync(DeployifyStateFile stateFile, string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, StateFileName);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, stateFile, s_jsonOptions, ct);
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

        return (config, envName);
    }
}
