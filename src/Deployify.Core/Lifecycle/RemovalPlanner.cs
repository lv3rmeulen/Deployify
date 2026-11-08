using System;
using System.Collections.Generic;
using System.Linq;
using Deployify.Core.Interfaces;
using Deployify.Core.Rollouts;
using Deployify.Core.State;

namespace Deployify.Core.Lifecycle;

/// <summary>What destroy or prune will do, decided before anything is touched.</summary>
public sealed class RemovalPlan
{
    /// <summary>Services to delete, dependents before the services they use.</summary>
    public List<string> ToDelete { get; init; } = [];

    /// <summary>Recorded in state but no longer on the platform: only dropped from the inventory.</summary>
    public List<string> AlreadyGone { get; init; } = [];

    /// <summary>Marked as Deployify's on the platform but not in this environment's state: left alone.</summary>
    public List<string> NotInState { get; init; } = [];

    /// <summary>Marked as belonging to another environment: left alone.</summary>
    public List<string> OtherEnvironment { get; init; } = [];

    /// <summary>Services Deployify manages here in total (deleted or not), for the prune safety check.</summary>
    public int ManagedCount { get; init; }

    public bool IsEmpty => ToDelete.Count == 0 && AlreadyGone.Count == 0;
}

/// <summary>
/// The deletion rule shared by destroy and prune: a service is deleted only when it is both in
/// the environment's inventory and marked as Deployify's on the platform (and, where the
/// platform records it, marked for this environment).
/// </summary>
public static class RemovalPlanner
{
    /// <param name="candidates">The services the caller wants gone (all of them for destroy, those left out of the manifest for prune).</param>
    /// <param name="inventory">The environment's state.</param>
    /// <param name="managed">What the platform marks as Deployify's (<see cref="IResourceLifecycle.ListManagedAsync"/>).</param>
    /// <param name="environment">This environment's name, checked against a platform-side environment tag.</param>
    public static RemovalPlan Build(
        IEnumerable<string> candidates, EnvironmentInventory inventory, IReadOnlyList<ManagedService> managed, string environment)
    {
        var mine = managed.Where(m => m.Environment is null || m.Environment == environment)
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var others = managed.Where(m => m.Environment is not null && m.Environment != environment)
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var recorded = inventory.Services.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        var wanted = candidates.Distinct(StringComparer.Ordinal).ToList();
        var delete = wanted.Where(s => recorded.Contains(s) && mine.Contains(s)).ToList();

        return new RemovalPlan
        {
            ToDelete = Order(delete, inventory),
            AlreadyGone = [.. wanted.Where(s => recorded.Contains(s) && !mine.Contains(s) && !others.Contains(s)).Order(StringComparer.Ordinal)],
            NotInState = [.. wanted.Where(s => !recorded.Contains(s) && mine.Contains(s)).Order(StringComparer.Ordinal)],
            OtherEnvironment = [.. wanted.Where(others.Contains).Order(StringComparer.Ordinal)],
            ManagedCount = mine.Count
        };
    }

    /// <summary>
    /// Why a prune of <paramref name="plan"/> should not run without <c>--force-prune</c>, or null.
    /// It refuses to remove every service, or more than half of them (when that is more than one).
    /// </summary>
    public static string? PruneSafetyCheck(RemovalPlan plan)
    {
        var n = plan.ToDelete.Count;
        if (n == 0)
        {
            return null;
        }

        if (n >= plan.ManagedCount)
        {
            return $"the prune would remove every service Deployify manages here ({n})";
        }

        if (n > 1 && n * 2 > plan.ManagedCount)
        {
            return $"the prune would remove {n} of {plan.ManagedCount} services";
        }

        return null;
    }

    /// <summary>Dependents first: the reverse of the deploy order, from the dependencies recorded in the inventory.</summary>
    private static List<string> Order(List<string> services, EnvironmentInventory inventory)
    {
        var deps = services.SelectMany(s => inventory.Find(s)?.DependsOn.Select(d => new ServiceDependency { From = d, To = s }) ?? []);
        var waves = RolloutPlanner.Waves(services.Order(StringComparer.Ordinal), deps);
        return [.. waves.Reverse().SelectMany(w => w)];
    }
}
