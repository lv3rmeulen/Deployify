using System.Collections.Generic;

namespace Deployify.Core.Models;

public sealed class PlanResult
{
    /// <summary>Services that will be created (new deployments).</summary>
    public List<object> ToCreate { get; set; } = [];

    /// <summary>Services that will be updated (already running).</summary>
    public List<object> ToUpdate { get; set; } = [];

    /// <summary>Service names that will be removed from the target.</summary>
    public List<string> ToDelete { get; set; } = [];

    /// <summary>Service names with no changes.</summary>
    public List<string> Unchanged { get; set; } = [];

    public int CreateCount => ToCreate.Count;
    public int UpdateCount => ToUpdate.Count;
    public int DeleteCount => ToDelete.Count;
}

public sealed class DiffEntry
{
    public string ServiceName { get; set; } = string.Empty;
    public DiffKind Kind { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Field { get; set; }
}

public enum DiffKind
{
    Added,
    Removed,
    Changed,
    Unchanged
}
