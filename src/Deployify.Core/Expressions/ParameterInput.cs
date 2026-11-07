using System;
using System.Collections.Generic;
using System.Text.Json;
using Deployify.Core.Models;

namespace Deployify.Core.Expressions;

/// <summary>
/// One entry of a <c>parameter.v0</c> resource's <c>inputs</c> object:
/// <code>
/// "inputs": {
///   "value": {
///     "type": "string",
///     "secret": true,
///     "default": { "generate": { "minLength": 22, "special": false } }
///   }
/// }
/// </code>
/// </summary>
public sealed class ParameterInput
{
    public string Name { get; init; } = "value";

    public string Type { get; init; } = "string";

    public bool Secret { get; init; }

    /// <summary><c>default.value</c>: a literal fallback.</summary>
    public string? DefaultValue { get; init; }

    /// <summary><c>default.generate</c>: how to generate a value when none is supplied.</summary>
    public GenerateOptions? Generate { get; init; }

    /// <summary>Reads every input declared on a resource. Returns an empty map when there are none.</summary>
    public static IReadOnlyDictionary<string, ParameterInput> ReadAll(AspireResource resource)
    {
        var result = new Dictionary<string, ParameterInput>(StringComparer.Ordinal);
        if (resource.ExtensionData is null ||
            !resource.ExtensionData.TryGetValue("inputs", out var inputs) ||
            inputs.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var prop in inputs.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                result[prop.Name] = Read(prop.Name, prop.Value);
            }
        }

        return result;
    }

    private static ParameterInput Read(string name, JsonElement el)
    {
        string? defaultValue = null;
        GenerateOptions? generate = null;

        if (el.TryGetProperty("default", out var def) && def.ValueKind == JsonValueKind.Object)
        {
            if (def.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
            {
                defaultValue = v.GetString();
            }

            if (def.TryGetProperty("generate", out var g) && g.ValueKind == JsonValueKind.Object)
            {
                generate = GenerateOptions.Read(g);
            }
        }

        return new ParameterInput
        {
            Name = name,
            Type = el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "string",
            Secret = el.TryGetProperty("secret", out var s) && s.ValueKind == JsonValueKind.True,
            DefaultValue = defaultValue,
            Generate = generate
        };
    }
}

/// <summary>
/// Mirrors Aspire's <c>GenerateParameterDefault</c>: a minimum length, which character classes
/// may be used, and how many characters of each class are required.
/// </summary>
public sealed class GenerateOptions
{
    public int MinLength { get; init; } = 22;

    public bool Lower { get; init; } = true;

    public bool Upper { get; init; } = true;

    public bool Numeric { get; init; } = true;

    public bool Special { get; init; } = true;

    public int MinLower { get; init; }

    public int MinUpper { get; init; }

    public int MinNumeric { get; init; }

    public int MinSpecial { get; init; }

    internal static GenerateOptions Read(JsonElement el) => new()
    {
        MinLength = Int(el, "minLength") ?? 22,
        Lower = Bool(el, "lower") ?? true,
        Upper = Bool(el, "upper") ?? true,
        Numeric = Bool(el, "numeric") ?? true,
        Special = Bool(el, "special") ?? true,
        MinLower = Int(el, "minLower") ?? 0,
        MinUpper = Int(el, "minUpper") ?? 0,
        MinNumeric = Int(el, "minNumeric") ?? 0,
        MinSpecial = Int(el, "minSpecial") ?? 0
    };

    private static int? Int(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool? Bool(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
