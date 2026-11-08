using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Deployify.Core.Expressions;
using Deployify.Core.Models;

namespace Deployify.Core.Services;

/// <summary>
/// Keeps generated parameter values (passwords from <c>default.generate</c>) in
/// <c>deployify-parameters.json</c> next to the state file, one section per environment, so
/// every deploy reuses the same value instead of rotating it. The file holds secrets: it is
/// written with owner-only permissions where the OS supports it and should be git-ignored.
/// </summary>
public sealed class ParameterStore
{
    public const string FileName = "deployify-parameters.json";

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, string>> _environments;

    private ParameterStore(string path, string environment, Dictionary<string, Dictionary<string, string>> environments)
    {
        FilePath = path;
        Environment = environment;
        _environments = environments;
        if (!_environments.TryGetValue(environment, out var values))
        {
            values = new Dictionary<string, string>(StringComparer.Ordinal);
            _environments[environment] = values;
        }

        Values = values;
    }

    public string FilePath { get; }

    public string Environment { get; }

    /// <summary>Generated values for this environment, keyed by <c>parameter.input</c>.</summary>
    public IDictionary<string, string> Values { get; }

    public static ParameterStore Load(string directory, string environment)
    {
        var path = Path.Combine(directory, FileName);
        var environments = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path));
            if (loaded is not null)
            {
                foreach (var (env, values) in loaded)
                {
                    environments[env] = new Dictionary<string, string>(values, StringComparer.Ordinal);
                }
            }
        }

        return new ParameterStore(path, environment, environments);
    }

    /// <summary>The saved values of one environment (empty when there are none), for copying to remote state.</summary>
    public static Dictionary<string, string> ReadSection(string directory, string environment)
        => new(Load(directory, environment).Values, StringComparer.Ordinal);

    /// <summary>Replaces one environment's values in the file, leaving the other environments as they are.</summary>
    public static void WriteSection(string directory, string environment, IReadOnlyDictionary<string, string> values)
    {
        var store = Load(directory, environment);
        store.Values.Clear();
        foreach (var (k, v) in values)
        {
            store.Values[k] = v;
        }

        store.Save();
    }

    /// <summary>
    /// A parameter source for <paramref name="config"/>: explicit values, then environment
    /// variables, then manifest defaults, then generated values that are saved here as they're made.
    /// </summary>
    public IParameterSource CreateSource(EnvironmentConfig config)
        => ParameterSources.Default(
            ParameterResolution.ExplicitValues(config),
            Values,
            onGenerated: (_, _) => Save());

    /// <summary>Writes the file atomically, readable by the owner only on Unix.</summary>
    public void Save()
    {
        lock (_lock)
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_environments, s_json));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(tmp, FilePath, overwrite: true);
        }
    }
}
