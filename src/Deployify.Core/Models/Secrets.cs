using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Deployify.Core.Models;

/// <summary>
/// Resolves secret settings without requiring them to be stored in deployify-state.json.
/// A configured value of <c>"env:NAME"</c> is read from environment variable NAME; an empty
/// value falls back to a well-known environment variable (e.g. NOMAD_TOKEN).
/// </summary>
public static class Secrets
{
    public const string EnvPrefix = "env:";

    public static string? Resolve(string? configured, string? fallbackEnvVar = null)
    {
        if (!string.IsNullOrEmpty(configured))
        {
            if (configured.StartsWith(EnvPrefix, StringComparison.Ordinal))
            {
                return NullIfEmpty(Environment.GetEnvironmentVariable(configured[EnvPrefix.Length..]));
            }

            return configured;
        }

        return fallbackEnvVar is null ? null : NullIfEmpty(Environment.GetEnvironmentVariable(fallbackEnvVar));
    }

    /// <summary>True when the value is a literal secret rather than an env: reference.</summary>
    public static bool IsLiteral(string? configured)
        => !string.IsNullOrEmpty(configured) && !configured.StartsWith(EnvPrefix, StringComparison.Ordinal);

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>Finds secrets stored as plain text in a state file.</summary>
public static class StateFileSecrets
{
    private static readonly string[] s_secretWords = ["token", "password", "secret", "connectionstring", "apikey"];

    /// <summary>
    /// Returns "environment.path" for every setting whose name looks secret and whose
    /// value is stored literally rather than as an <c>env:</c> reference.
    /// </summary>
    public static IReadOnlyList<string> FindLiteral(DeployifyStateFile state)
    {
        var found = new List<string>();
        foreach (var (envName, env) in state.Environments)
        {
            if (Secrets.IsLiteral(env.Dashboard.BrowserToken))
            {
                found.Add($"{envName}.dashboard.browserToken");
            }

            foreach (var (name, value) in env.Parameters)
            {
                if (s_secretWords.Any(name.ToLowerInvariant().Replace("-", "").Replace("_", "").Contains) && Secrets.IsLiteral(value))
                {
                    found.Add($"{envName}.parameters.{name}");
                }
            }

            foreach (var (key, value) in env.TargetConfig)
            {
                var element = value switch
                {
                    JsonElement el => el,
                    null => default,
                    _ => JsonSerializer.SerializeToElement(value, TargetConfigJsonOptions.Default)
                };
                Walk(element, $"{envName}.{key}", found);
            }
        }

        return found;
    }

    private static void Walk(JsonElement el, string path, List<string> found)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var prop in el.EnumerateObject())
        {
            var name = prop.Name.ToLowerInvariant();
            if (prop.Value.ValueKind == JsonValueKind.String &&
                s_secretWords.Any(name.Contains) && Secrets.IsLiteral(prop.Value.GetString()))
            {
                found.Add($"{path}.{prop.Name}");
            }
            else if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                Walk(prop.Value, $"{path}.{prop.Name}", found);
            }
        }
    }
}
