using System.Collections.Generic;
using System.Linq;

namespace Deployify.Core.Models;

public sealed class PlanResult
{
    /// <summary>Services that will be created (new deployments).</summary>
    public List<string> ToCreate { get; set; } = [];

    /// <summary>Services that will be updated (already running).</summary>
    public List<string> ToUpdate { get; set; } = [];

    /// <summary>Service names that will be removed from the target.</summary>
    public List<string> ToDelete { get; set; } = [];

    /// <summary>Service names with no changes.</summary>
    public List<string> Unchanged { get; set; } = [];

    /// <summary>
    /// Services that exist on the target but couldn't be compared, with the reason. Apply
    /// may or may not change them; they are never reported as "unchanged".
    /// </summary>
    public List<PlanUnknown> Unknown { get; set; } = [];

    /// <summary>
    /// Manifest resources that won't be deployed because Deployify can't deploy them
    /// (see <see cref="Deployments.ManifestAudit"/>). Not a change, so not part of
    /// <see cref="IsUpToDate"/>: a skipped resource stays skipped on every run.
    /// </summary>
    public List<PlanSkipped> Skipped { get; set; } = [];

    public int CreateCount => ToCreate.Count;
    public int UpdateCount => ToUpdate.Count;
    public int DeleteCount => ToDelete.Count;
    public int UnknownCount => Unknown.Count;

    /// <summary>True only when every service is known to be up to date.</summary>
    public bool IsUpToDate => CreateCount + UpdateCount + DeleteCount + UnknownCount == 0;

    /// <summary>Builds a plan from per-service diff entries, so plan and diff can't disagree.</summary>
    public static PlanResult FromDiff(IEnumerable<DiffEntry> entries)
    {
        var plan = new PlanResult();
        foreach (var e in entries)
        {
            switch (e.Kind)
            {
                case DiffKind.Added:
                    plan.ToCreate.Add(e.ServiceName);
                    break;
                case DiffKind.Changed:
                    plan.ToUpdate.Add(e.ServiceName);
                    break;
                case DiffKind.Removed:
                    plan.ToDelete.Add(e.ServiceName);
                    break;
                case DiffKind.Unknown:
                    plan.Unknown.Add(new PlanUnknown(e.ServiceName, e.Reason ?? "could not be compared"));
                    break;
                default:
                    plan.Unchanged.Add(e.ServiceName);
                    break;
            }
        }

        return plan;
    }

    /// <summary>The services with their unknown reasons, e.g. for one-line summaries.</summary>
    public IEnumerable<string> UnknownNames => Unknown.Select(u => u.ServiceName);
}

/// <summary>A service whose live state couldn't be compared, and why.</summary>
public sealed record PlanUnknown(string ServiceName, string Reason);

/// <summary>A manifest resource that won't be deployed, its type, and why.</summary>
public sealed record PlanSkipped(string Name, string Type, string Reason);

public sealed class DiffEntry
{
    public string ServiceName { get; set; } = string.Empty;
    public DiffKind Kind { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Field { get; set; }

    /// <summary>Why the service couldn't be compared; set when <see cref="Kind"/> is Unknown.</summary>
    public string? Reason { get; set; }
}

public enum DiffKind
{
    Added,
    Removed,
    Changed,
    Unchanged,

    /// <summary>Exists on the target, but the comparison itself failed.</summary>
    Unknown
}
