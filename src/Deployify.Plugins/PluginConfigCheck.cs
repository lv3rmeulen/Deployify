using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Deployify.Core.Models;
using Deployify.Plugin.Contracts;
using Json.Schema;

namespace Deployify.Plugins;

/// <summary>
/// Checks an environment's settings for a plugin target against the JSON Schema the plugin
/// declared, so a typo or a wrong type is reported by Deployify, before the plugin runs.
/// </summary>
public static class PluginConfigCheck
{
    /// <summary>Problems with the target's <c>TargetConfig</c> section; empty when it is fine or the plugin declared no schema.</summary>
    public static IReadOnlyList<string> Check(PluginTargetInfo target, EnvironmentConfig config)
    {
        if (target.ConfigSchema is not { } schemaJson)
        {
            return [];
        }

        var key = target.ConfigKey ?? target.Id;
        var section = config.TargetConfig.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        var instance = section switch
        {
            null => JsonSerializer.SerializeToElement(new { }),
            JsonElement element => element,
            var other => JsonSerializer.SerializeToElement(other, other.GetType())
        };

        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(schemaJson.GetRawText());
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return [$"the plugin's config schema can't be read ({ex.Message})"];
        }

        var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid)
        {
            return [];
        }

        var errors = (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => (Keyword: e.Key, Location: d.InstanceLocation.ToString(), Message: e.Value)))
            .ToList();

        // "properties" and "items" only say that something below failed; the error below says what.
        var specific = errors.Where(e => e.Keyword is not ("properties" or "items")).ToList();
        return [.. (specific.Count > 0 ? specific : errors).Select(e => Describe(key, e.Location, e.Message)).Distinct()];
    }

    private static string Describe(string key, string location, string message)
    {
        var path = location is "" or "#" ? key : $"{key}{location.TrimStart('#').Replace('/', '.')}";
        return $"TargetConfig.{path}: {message}";
    }
}
