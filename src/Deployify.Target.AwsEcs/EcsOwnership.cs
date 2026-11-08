using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Deployify.Target.AwsEcs;

/// <summary>A service carrying Deployify's ownership tag.</summary>
public sealed record EcsManagedService(string Name, string Arn, string? Environment);

/// <summary>
/// The tags that mark an ECS service as Deployify's. Services are tagged when they are created,
/// and existing ones on every update, so services from before tagging are adopted on their next
/// deploy. Untagged services are never deleted by destroy or prune.
/// </summary>
public static class EcsOwnership
{
    public const string ManagedByTag = "deployify-managed-by";
    public const string ManagedByValue = "deployify";
    public const string EnvironmentTag = "deployify-env";

    /// <summary>The tagged, not-yet-deleted services in a <c>describe-services --include TAGS</c> response.</summary>
    public static IReadOnlyList<EcsManagedService> ParseManaged(string describeJson)
    {
        using var doc = JsonDocument.Parse(describeJson);
        var result = new List<EcsManagedService>();
        foreach (var svc in doc.RootElement.GetProperty("services").EnumerateArray())
        {
            if (svc.TryGetProperty("status", out var status) && status.GetString() == "INACTIVE")
            {
                continue;
            }

            var tags = svc.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray()
                    .Where(x => x.TryGetProperty("key", out _) && x.TryGetProperty("value", out _))
                    .GroupBy(x => x.GetProperty("key").GetString()!)
                    .ToDictionary(g => g.Key, g => g.First().GetProperty("value").GetString())
                : [];
            if (tags.TryGetValue(ManagedByTag, out var by) && by == ManagedByValue)
            {
                result.Add(new EcsManagedService(
                    svc.GetProperty("serviceName").GetString()!,
                    svc.GetProperty("serviceArn").GetString()!,
                    tags.GetValueOrDefault(EnvironmentTag)));
            }
        }

        return result;
    }
}
