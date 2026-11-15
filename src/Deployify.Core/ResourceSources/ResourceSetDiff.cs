using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Deployify.Core.ResourceSources;

/// <summary>A resource that was added or changed (<see cref="Upsert"/>), or removed (<see cref="Deleted"/>).</summary>
public sealed record ResourceChange
{
    private ResourceChange(ResourceSnapshot? upsert, ResourceSnapshot? deleted) => (Upsert, Deleted) = (upsert, deleted);

    public ResourceSnapshot? Upsert { get; }

    public ResourceSnapshot? Deleted { get; }

    public static ResourceChange Added(ResourceSnapshot resource) => new(resource, null);

    public static ResourceChange Removed(ResourceSnapshot resource) => new(null, resource);
}

/// <summary>
/// Turns two complete sets of resources into the changes between them, which is what the
/// dashboard's watch stream sends after its initial data.
/// </summary>
public static class ResourceSetDiff
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    /// <summary>
    /// Changes from <paramref name="before"/> to <paramref name="after"/>: an upsert for every
    /// resource that is new or differs in any way, a deletion for every one that is gone.
    /// Resources are matched by <see cref="ResourceSnapshot.Name"/>; when <paramref name="after"/>
    /// names one twice, the last wins.
    /// </summary>
    public static IReadOnlyList<ResourceChange> Compare(
        IReadOnlyDictionary<string, ResourceSnapshot> before, IReadOnlyList<ResourceSnapshot> after)
    {
        var changes = new List<ResourceChange>();
        var current = ToMap(after);
        foreach (var (name, resource) in current)
        {
            if (!before.TryGetValue(name, out var previous) || !AreEqual(previous, resource))
            {
                changes.Add(ResourceChange.Added(resource));
            }
        }

        foreach (var (name, resource) in before)
        {
            if (!current.ContainsKey(name))
            {
                changes.Add(ResourceChange.Removed(resource));
            }
        }

        return changes;
    }

    /// <summary>The set as a map by name; for a name that appears twice, the last one wins.</summary>
    public static Dictionary<string, ResourceSnapshot> ToMap(IEnumerable<ResourceSnapshot> resources)
    {
        var map = new Dictionary<string, ResourceSnapshot>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            map[resource.Name] = resource;
        }

        return map;
    }

    /// <summary>
    /// Compares every field, lists included (records compare lists by reference, and every poll
    /// builds new ones).
    /// </summary>
    public static bool AreEqual(ResourceSnapshot a, ResourceSnapshot b)
        => ReferenceEquals(a, b) ||
           JsonSerializer.Serialize(a, s_json) == JsonSerializer.Serialize(b, s_json);

    /// <summary>Parents before children, so the dashboard never sees a child whose parent it doesn't know yet.</summary>
    public static IReadOnlyList<ResourceSnapshot> ParentsFirst(IEnumerable<ResourceSnapshot> resources)
        => [.. resources.OrderBy(r => r.Parent is null ? 0 : 1).ThenBy(r => r.Name, StringComparer.Ordinal)];
}
