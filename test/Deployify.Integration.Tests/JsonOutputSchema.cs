using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Shouldly;

namespace Deployify.Integration.Tests;

/// <summary>
/// Checks <c>--output json</c> documents against the published schema,
/// docs/public/schemas/deployify-output-v1.json.
/// </summary>
/// <remarks>
/// The published schema lets objects have properties it doesn't list, because consumers must
/// accept properties a later 1.x release adds. The tests are stricter: they check against a copy
/// that rejects unlisted properties, so a property added to the output without being added to
/// the schema (and the docs) fails here.
/// </remarks>
internal static class JsonOutputSchema
{
    public static readonly string SchemaPath = Path.Combine(RepositoryRoot(), "docs", "public", "schemas", "deployify-output-v1.json");

    private static readonly Lazy<JsonNode> Published = new(() => JsonNode.Parse(File.ReadAllText(SchemaPath))!);

    private static readonly Lazy<JsonSchema> Strict = new(() =>
    {
        var node = Published.Value.DeepClone();
        DisallowUnlistedProperties(node);
        return JsonSchema.FromText(node.ToJsonString());
    });

    public static JsonNode Document => Published.Value;

    /// <summary>Fails with every violation when <paramref name="document"/> doesn't match the schema.</summary>
    public static void Validate(JsonElement document)
    {
        var result = Strict.Value.Evaluate(document, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        });

        if (result.IsValid)
        {
            return;
        }

        var errors = (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} ({d.EvaluationPath}): {e.Value}"));
        result.IsValid.ShouldBeTrue(
            "Output doesn't match docs/public/schemas/deployify-output-v1.json:\n" + string.Join("\n", errors) +
            "\n\n" + document.GetRawText());
    }

    /// <summary>
    /// Adds <c>unevaluatedProperties: false</c> to every object schema that lists properties.
    /// <c>if</c>/<c>then</c> branches have no <c>type</c>, so they're left alone and the root's
    /// conditional <c>result</c> still counts as evaluated.
    /// </summary>
    private static void DisallowUnlistedProperties(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["type"] is JsonValue type && type.ToString() == "object" && obj.ContainsKey("properties"))
                {
                    obj["unevaluatedProperties"] = false;
                }

                foreach (var (_, child) in obj.ToList())
                {
                    DisallowUnlistedProperties(child);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    DisallowUnlistedProperties(child);
                }

                break;
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Deployify.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root (Deployify.slnx) from the test binaries.");
    }
}
