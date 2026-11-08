using System.Collections.Generic;
using System.Linq;
using Deployify.Core.Interfaces;

namespace Deployify.Core.Rollouts;

/// <summary>Orders services into waves so every service is deployed after the services it uses.</summary>
public static class RolloutPlanner
{
    /// <summary>
    /// Splits <paramref name="services"/> into waves. A service lands in the first wave after all
    /// of its dependencies (<see cref="ServiceDependency.From"/> is used by
    /// <see cref="ServiceDependency.To"/>). Edges to unknown services are ignored; services caught
    /// in a cycle go into one final wave. Within a wave, the input order is kept.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Waves(
        IEnumerable<string> services, IEnumerable<ServiceDependency> dependencies)
    {
        var order = services.Distinct().ToList();
        var known = order.ToHashSet();
        var needs = order.ToDictionary(s => s, _ => new HashSet<string>());
        foreach (var d in dependencies)
        {
            if (d.From != d.To && known.Contains(d.From) && known.Contains(d.To))
            {
                needs[d.To].Add(d.From);
            }
        }

        var waves = new List<IReadOnlyList<string>>();
        var done = new HashSet<string>();
        while (done.Count < order.Count)
        {
            var wave = order.Where(s => !done.Contains(s) && needs[s].All(done.Contains)).ToList();
            if (wave.Count == 0)
            {
                // A cycle: deploy what is left together rather than not at all.
                wave = [.. order.Where(s => !done.Contains(s))];
            }

            waves.Add(wave);
            done.UnionWith(wave);
        }

        return waves;
    }
}
