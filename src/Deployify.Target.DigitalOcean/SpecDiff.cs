using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Deployify.Core.Models;

namespace Deployify.Target.DigitalOcean;

/// <summary>
/// Compares the spec Deployify wants with the one App Platform runs, component by component.
/// Only the fields Deployify sets are compared: the platform fills in defaults of its own, and
/// those aren't changes. Secret values can't be compared (App Platform returns them encrypted),
/// so a secret only counts as changed when it's added or removed.
/// </summary>
public static class SpecDiff
{
    private static readonly string[] s_componentArrays = ["services", "workers", "jobs", "static_sites", "functions", "databases"];

    public static List<DiffEntry> Compare(JsonObject desired, JsonObject? live, IReadOnlyDictionary<string, string> resourceByComponent)
    {
        string Display(string component) => resourceByComponent.GetValueOrDefault(component, component);

        var want = Components(desired);
        var have = live is null ? [] : Components(live);
        var entries = new List<DiffEntry>();

        foreach (var (name, (kind, component)) in want)
        {
            if (!have.TryGetValue(name, out var current))
            {
                entries.Add(new DiffEntry { ServiceName = Display(name), Kind = DiffKind.Added, NewValue = kind });
                continue;
            }

            if (current.Kind != kind)
            {
                entries.Add(new DiffEntry
                {
                    ServiceName = Display(name), Kind = DiffKind.Changed, Field = "kind", OldValue = current.Kind, NewValue = kind
                });
                continue;
            }

            var differences = new List<(string Field, string? Old, string? New)>();
            CompareObject(Normalize(component), Normalize(current.Node), "", differences);
            if (differences.Count == 0)
            {
                entries.Add(new DiffEntry { ServiceName = Display(name), Kind = DiffKind.Unchanged });
            }
            else
            {
                entries.AddRange(differences.Select(d => new DiffEntry
                {
                    ServiceName = Display(name), Kind = DiffKind.Changed, Field = d.Field, OldValue = d.Old, NewValue = d.New
                }));
            }
        }

        foreach (var (name, (kind, _)) in have.Where(h => !want.ContainsKey(h.Key)))
        {
            entries.Add(new DiffEntry { ServiceName = Display(name), Kind = DiffKind.Removed, OldValue = kind });
        }

        // Ingress belongs to the app, not one component; report it once under the app's name.
        var wantIngress = desired["ingress"]?.ToJsonString() ?? "";
        var haveIngress = live?["ingress"] is { } li && desired["ingress"] is { } di
            ? Project(di, li)?.ToJsonString() ?? ""
            : live?["ingress"]?.ToJsonString() ?? "";
        if (live is not null && wantIngress != haveIngress)
        {
            entries.Add(new DiffEntry
            {
                ServiceName = desired["name"]?.GetValue<string>() ?? "app",
                Kind = DiffKind.Changed,
                Field = "ingress",
                OldValue = haveIngress,
                NewValue = wantIngress
            });
        }

        return entries;
    }

    private static Dictionary<string, (string Kind, JsonObject Node)> Components(JsonObject spec)
    {
        var result = new Dictionary<string, (string, JsonObject)>(StringComparer.Ordinal);
        foreach (var array in s_componentArrays)
        {
            foreach (var node in spec[array]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (node["name"]?.GetValue<string>() is { } name)
                {
                    result[name] = (array.TrimEnd('s').Replace("_site", " site"), node);
                }
            }
        }

        return result;
    }

    /// <summary>Envs keyed by name, secrets reduced to a marker, so order and encryption don't count.</summary>
    private static JsonObject Normalize(JsonObject component)
    {
        var copy = component.DeepClone().AsObject();
        if (copy["envs"] is JsonArray envs)
        {
            var byKey = new JsonObject();
            foreach (var env in envs.OfType<JsonObject>().OrderBy(e => e["key"]?.GetValue<string>(), StringComparer.Ordinal))
            {
                var key = env["key"]?.GetValue<string>();
                if (key is null)
                {
                    continue;
                }

                var type = env["type"]?.GetValue<string>() ?? "GENERAL";
                byKey[key] = type == "SECRET" ? "(secret)" : env["value"]?.DeepClone();
            }

            copy["envs"] = byKey;
        }

        return copy;
    }

    /// <summary>Records every field of <paramref name="want"/> whose value differs in <paramref name="have"/>.</summary>
    private static void CompareObject(JsonObject want, JsonObject have, string prefix, List<(string, string?, string?)> differences)
    {
        foreach (var (key, value) in want)
        {
            var field = prefix.Length == 0 ? key : $"{prefix}.{key}";
            var current = have[key];
            if (value is JsonObject wo && current is JsonObject ho && key != "envs")
            {
                CompareObject(wo, ho, field, differences);
            }
            else if (key == "envs" && value is JsonObject we)
            {
                var he = current as JsonObject ?? [];
                foreach (var (env, v) in we)
                {
                    if (!JsonNode.DeepEquals(v, he[env]))
                    {
                        differences.Add(($"env.{env}", Show(he[env]), Show(v)));
                    }
                }

                foreach (var (env, v) in he.Where(e => !we.ContainsKey(e.Key)))
                {
                    differences.Add(($"env.{env}", Show(v), null));
                }
            }
            else if (!JsonNode.DeepEquals(value, current))
            {
                differences.Add((field, Show(current), Show(value)));
            }
        }
    }

    /// <summary>The part of <paramref name="have"/> that has the shape of <paramref name="want"/>.</summary>
    private static JsonNode? Project(JsonNode want, JsonNode have)
    {
        if (want is JsonObject wo && have is JsonObject ho)
        {
            var result = new JsonObject();
            foreach (var (key, value) in wo)
            {
                if (ho[key] is { } h)
                {
                    result[key] = value is null ? h.DeepClone() : Project(value, h);
                }
            }

            return result;
        }

        if (want is JsonArray wa && have is JsonArray ha)
        {
            var result = new JsonArray();
            for (var i = 0; i < ha.Count; i++)
            {
                result.Add(i < wa.Count && wa[i] is { } w && ha[i] is { } h ? Project(w, h) : ha[i]?.DeepClone());
            }

            return result;
        }

        return have.DeepClone();
    }

    private static string? Show(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString()
    };
}
