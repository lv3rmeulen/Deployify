using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.State;

namespace Deployify.Core.Lifecycle;

public static class InventoryRecorder
{
    /// <summary>
    /// Records every service of the manifest in the inventory, with its dependencies, before
    /// apply touches anything. Recording first means a run that dies part-way still leaves the
    /// services it may have created on record, so a later destroy finds them.
    /// </summary>
    public static Task<EnvironmentInventory> RecordDesiredAsync(
        StateSession state, IDeploymentTarget target, AspireManifest manifest, EnvironmentConfig config,
        TimeProvider? time = null, CancellationToken ct = default)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var services = target.Explain(manifest, config).Select(e => e.Name).ToList();
        var deps = target.GetDependencies(manifest, config)
            .GroupBy(d => d.To)
            .ToDictionary(g => g.Key, g => g.Select(d => d.From).ToList());

        return state.Inventory.UpdateAsync(inv =>
        {
            inv.Target = target.DisplayName;
            foreach (var s in services)
            {
                inv.Upsert(s, deps.TryGetValue(s, out var d) ? d : [], now);
            }
        }, ct);
    }

    /// <summary>
    /// Adds services the platform marks as Deployify's (for this environment) but the inventory
    /// doesn't list yet: deployments made before Deployify kept an inventory. Returns the names added.
    /// </summary>
    public static async Task<IReadOnlyList<string>> AdoptAsync(
        StateSession state, IDeploymentTarget target, IReadOnlyList<ManagedService> managed, string environment,
        IReadOnlyCollection<string>? only = null, TimeProvider? time = null, CancellationToken ct = default)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var added = new List<string>();
        await state.Inventory.UpdateAsync(inv =>
        {
            added.Clear();
            inv.Target ??= target.DisplayName;
            foreach (var m in managed.Where(m => m.Environment is null || m.Environment == environment)
                         .Where(m => only is null || only.Contains(m.Name))
                         .Where(m => inv.Find(m.Name) is null))
            {
                inv.Upsert(m.Name, [], now, m.TargetId);
                added.Add(m.Name);
            }
        }, ct);
        return added;
    }
}
