using System.Collections.Generic;
using System.Text.Json;

namespace Deployify.Target.AzureContainerApps;

/// <summary>A Container App carrying Deployify's ownership tag.</summary>
public sealed record AcaManagedApp(string Name, string Id, string? Environment);

/// <summary>
/// The Azure tags that mark a Container App as Deployify's. Apps are tagged after every deploy,
/// so apps from before tagging are adopted on their next deploy. Untagged apps are never
/// deleted by destroy or prune.
/// </summary>
public static class AcaOwnership
{
    public const string ManagedByTag = "deployify-managed-by";
    public const string ManagedByValue = "deployify";
    public const string EnvironmentTag = "deployify-env";

    /// <summary>The tagged apps in an <c>az containerapp list</c> response.</summary>
    public static IReadOnlyList<AcaManagedApp> ParseManaged(string listJson)
    {
        using var doc = JsonDocument.Parse(listJson);
        var result = new List<AcaManagedApp>();
        foreach (var app in doc.RootElement.EnumerateArray())
        {
            if (!app.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object ||
                !tags.TryGetProperty(ManagedByTag, out var by) || by.GetString() != ManagedByValue)
            {
                continue;
            }

            result.Add(new AcaManagedApp(
                app.GetProperty("name").GetString()!,
                app.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                tags.TryGetProperty(EnvironmentTag, out var env) ? env.GetString() : null));
        }

        return result;
    }
}
