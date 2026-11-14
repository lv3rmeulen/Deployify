using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    private static readonly string[] s_secretWords = ["token", "password", "secret", "connectionstring", "apikey", "consumerkey"];

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

            if (Secrets.IsLiteral(env.State?.Consul?.Token))
            {
                found.Add($"{envName}.state.consul.token");
            }

            if (Secrets.IsLiteral(env.State?.AzureBlob?.SasToken))
            {
                found.Add($"{envName}.state.azureBlob.sasToken");
            }

            if (Secrets.IsLiteral(env.Secrets.Vault.Token))
            {
                found.Add($"{envName}.secrets.vault.token");
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

    /// <summary>
    /// Replaces every literal secret <see cref="FindLiteral"/> reports with an <c>env:NAME</c>
    /// reference, in place. Returns what changed, including the old values, so the caller can
    /// tell the user which variables to set. Well-known settings get the variable the target
    /// already reads (NOMAD_TOKEN, CONSUL_HTTP_TOKEN, ...); others get
    /// <c>DEPLOYIFY_{ENV}_{SETTING}</c>. When two environments hold different values for the
    /// same well-known variable, the second gets an <c>_{ENV}</c> suffix.
    /// </summary>
    public static IReadOnlyList<SecretMigration> MigrateToReferences(DeployifyStateFile state)
    {
        var changes = new List<SecretMigration>();
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal); // variable -> value

        string Claim(string envName, string setting, string value)
        {
            var name = WellKnownVariables.TryGetValue(setting, out var known)
                ? known
                : $"DEPLOYIFY_{Sanitize(envName)}_{Sanitize(setting)}";
            var candidate = name;
            for (var i = 2; claimed.TryGetValue(candidate, out var existing) && existing != value; i++)
            {
                candidate = i == 2 ? $"{name}_{Sanitize(envName)}" : $"{name}_{Sanitize(envName)}_{i}";
            }

            claimed[candidate] = value;
            return candidate;
        }

        string? Migrate(string envName, string setting, string? value)
        {
            if (!Secrets.IsLiteral(value))
            {
                return value;
            }

            var variable = Claim(envName, setting, value!);
            changes.Add(new SecretMigration($"{envName}.{setting}", variable, value!));
            return Secrets.EnvPrefix + variable;
        }

        foreach (var (envName, env) in state.Environments)
        {
            env.Dashboard.BrowserToken = Migrate(envName, "dashboard.browserToken", env.Dashboard.BrowserToken);

            foreach (var name in env.Parameters.Keys.ToList())
            {
                if (s_secretWords.Any(name.ToLowerInvariant().Replace("-", "").Replace("_", "").Contains))
                {
                    env.Parameters[name] = Migrate(envName, $"parameters.{name}", env.Parameters[name])!;
                }
            }

            if (env.State?.Consul is { } consul)
            {
                consul.Token = Migrate(envName, "state.consul.token", consul.Token);
            }

            if (env.State?.AzureBlob is { } blob)
            {
                blob.SasToken = Migrate(envName, "state.azureBlob.sasToken", blob.SasToken);
            }

            env.Secrets.Vault.Token = Migrate(envName, "secrets.vault.token", env.Secrets.Vault.Token);

            foreach (var key in env.TargetConfig.Keys.ToList())
            {
                var node = env.TargetConfig[key] switch
                {
                    JsonElement el => JsonNode.Parse(el.GetRawText()),
                    null => null,
                    var other => JsonSerializer.SerializeToNode(other, TargetConfigJsonOptions.Default)
                };
                if (node is not JsonObject obj)
                {
                    continue;
                }

                var before = changes.Count;
                WalkAndReplace(obj, key, (setting, value) => Migrate(envName, setting, value)!);
                if (changes.Count != before)
                {
                    env.TargetConfig[key] = JsonSerializer.SerializeToElement(obj);
                }
            }
        }

        return changes;
    }

    private static void WalkAndReplace(JsonObject obj, string path, Func<string, string, string> migrate)
    {
        foreach (var (name, value) in obj.ToList())
        {
            var setting = $"{path}.{name}";
            if (value is JsonValue v && v.TryGetValue<string>(out var s) &&
                s_secretWords.Any(name.ToLowerInvariant().Contains) && Secrets.IsLiteral(s))
            {
                obj[name] = migrate(setting, s);
            }
            else if (value is JsonObject child)
            {
                WalkAndReplace(child, setting, migrate);
            }
        }
    }

    /// <summary>Settings whose targets already read a well-known variable when unset.</summary>
    private static readonly Dictionary<string, string> WellKnownVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dashboard.browserToken"] = "DEPLOYIFY_DASHBOARD_TOKEN",
        ["state.consul.token"] = "CONSUL_HTTP_TOKEN",
        ["state.azureBlob.sasToken"] = "DEPLOYIFY_STATE_SAS_TOKEN",
        ["secrets.vault.token"] = "VAULT_TOKEN",
        ["nomad.token"] = "NOMAD_TOKEN",
        ["nomad.registryPassword"] = "DEPLOYIFY_REGISTRY_PASSWORD",
        ["nomadconsul.nomadToken"] = "NOMAD_TOKEN",
        ["nomadconsul.consulToken"] = "CONSUL_HTTP_TOKEN",
        ["nomadconsul.registryPassword"] = "DEPLOYIFY_REGISTRY_PASSWORD",
        ["iis.password"] = "IIS_PASSWORD",
        ["ovhcloud.applicationSecret"] = "OVH_APPLICATION_SECRET",
        ["ovhcloud.consumerKey"] = "OVH_CONSUMER_KEY",
        ["ovhcloud.clientSecret"] = "OVH_CLIENT_SECRET"
    };

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }

        return sb.ToString();
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

/// <summary>A literal secret moved out of the state file: where it was, the variable that replaces it, and its value.</summary>
public sealed record SecretMigration(string Path, string Variable, string Value);
