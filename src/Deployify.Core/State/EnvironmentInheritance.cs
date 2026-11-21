using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;

namespace Deployify.Core.State;

/// <summary>
/// Layered environments: an environment that sets <see cref="EnvironmentConfig.Extends"/> starts
/// from the named environment's settings and holds only what differs.
/// </summary>
/// <remarks>
/// <para>
/// Layers are merged as JSON before the environment is read. Objects merge key by key (keys ignore
/// case, like everywhere else in the state file); anything else, arrays included, replaces the
/// base's value; <c>null</c> removes the base's value, so the setting falls back to its default.
/// </para>
/// <para>
/// When the file is saved, an extending environment is written back as its differences: the keys
/// it set itself, plus whatever this run changed in it. Settings it inherited and didn't change
/// stay inherited, so a later change to the base still reaches it.
/// </para>
/// <para>
/// Not inherited: <see cref="EnvironmentConfig.Extends"/>, <see cref="EnvironmentConfig.Abstract"/>
/// and <see cref="SecretsConfig.References"/>, which records where apply wrote this environment's
/// own secrets.
/// </para>
/// </remarks>
public static class EnvironmentInheritance
{
    /// <summary>How many environments one chain may hold, the environment itself included.</summary>
    public const int MaxDepth = 10;

    internal const string ExtendsKey = nameof(EnvironmentConfig.Extends);
    internal const string AbstractKey = nameof(EnvironmentConfig.Abstract);

    /// <summary>True when any environment in <paramref name="environments"/> extends another.</summary>
    public static bool AnyLayered(JsonObject environments, string what)
        => environments.Any(e => e.Value is JsonObject env && ReadExtends(env, e.Key, what) is not null);

    /// <summary>
    /// Every environment in <paramref name="environments"/> (the raw <c>Environments</c> object of
    /// a state file) merged with its bases. Throws <see cref="StateFormatException"/> for a base
    /// that doesn't exist, a cycle, or a chain deeper than <see cref="MaxDepth"/>.
    /// </summary>
    public static Dictionary<string, JsonObject> Resolve(JsonObject environments, string what)
    {
        var resolved = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (name, node) in environments)
        {
            if (node is JsonObject env)
            {
                ThrowIfDuplicateKeys(env, $"Environments.{name}", what);
            }
        }

        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, _) in environments)
        {
            ResolveOne(name, environments, resolved, depths, [], what);
        }

        return resolved;
    }

    private static JsonObject? ResolveOne(
        string name, JsonObject environments, Dictionary<string, JsonObject> resolved,
        Dictionary<string, int> depths, List<string> chain, string what)
    {
        if (resolved.TryGetValue(name, out var done))
        {
            return done;
        }

        if (environments[name] is not JsonObject own)
        {
            // Not an object: left for the deserializer to report.
            return null;
        }

        if (chain.Contains(name, StringComparer.Ordinal))
        {
            throw new StateFormatException(
                $"{what}: environments extend each other in a loop ({string.Join(" → ", chain.Append(name))}). " +
                "Point one of them somewhere else.");
        }

        chain.Add(name);
        var extends = ReadExtends(own, name, what);
        JsonObject result;
        if (extends is null)
        {
            result = (JsonObject)own.DeepClone();
            depths[name] = 1;
        }
        else
        {
            if (environments[extends] is null && !environments.ContainsKey(extends))
            {
                var near = environments.Select(e => e.Key)
                    .FirstOrDefault(k => string.Equals(k, extends, StringComparison.OrdinalIgnoreCase));
                throw new StateFormatException(
                    $"{what}: environment '{name}' extends '{extends}', which doesn't exist" +
                    (near is not null ? $" (environment names are case-sensitive; did you mean '{near}'?)" : "") +
                    $". Environments: {string.Join(", ", environments.Select(e => e.Key))}.");
            }

            var baseResolved = ResolveOne(extends, environments, resolved, depths, chain, what)
                ?? throw new StateFormatException($"{what}: environment '{extends}' is not an object.");
            depths[name] = depths[extends] + 1;
            if (depths[name] > MaxDepth)
            {
                throw new StateFormatException(
                    $"{what}: environment '{name}' extends more than {MaxDepth - 1} levels deep. Flatten the chain.");
            }

            result = Inheritable(baseResolved);
            Merge(result, own);
        }

        chain.RemoveAt(chain.Count - 1);
        resolved[name] = result;
        return result;
    }

    /// <summary>The base environment named in <paramref name="env"/>, or null.</summary>
    internal static string? ReadExtends(JsonObject env, string name, string what)
    {
        var key = FindKey(env, ExtendsKey);
        if (key is null || env[key] is null)
        {
            return null;
        }

        if (env[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } s)
        {
            return s;
        }

        throw new StateFormatException(
            $"{what}: '{ExtendsKey}' of environment '{name}' must be the name of another environment.");
    }

    /// <summary>
    /// A copy of a resolved environment holding only what an environment extending it inherits.
    /// </summary>
    public static JsonObject Inheritable(JsonObject resolved)
    {
        var copy = (JsonObject)resolved.DeepClone();
        RemoveKey(copy, ExtendsKey);
        RemoveKey(copy, AbstractKey);
        if (FindKey(copy, nameof(EnvironmentConfig.Secrets)) is { } secretsKey && copy[secretsKey] is JsonObject secrets)
        {
            RemoveKey(secrets, nameof(SecretsConfig.References));
        }

        return copy;
    }

    /// <summary>
    /// Merges <paramref name="overlay"/> into <paramref name="target"/>: objects key by key,
    /// everything else replaced, <c>null</c> removing the key.
    /// </summary>
    public static void Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay.ToList())
        {
            var existing = FindKey(target, key);
            if (value is null)
            {
                if (existing is not null)
                {
                    target.Remove(existing);
                }

                continue;
            }

            if (value is JsonObject overlayObject && existing is not null && target[existing] is JsonObject targetObject)
            {
                Merge(targetObject, overlayObject);
                continue;
            }

            if (existing is not null)
            {
                target.Remove(existing);
            }

            if (value is JsonObject newObject)
            {
                // Nothing under it to remove: drop removal markers rather than write nulls.
                var copy = new JsonObject();
                Merge(copy, newObject);
                target[key] = copy;
            }
            else
            {
                target[key] = value.DeepClone();
            }
        }
    }

    /// <summary>
    /// What changed from <paramref name="from"/> to <paramref name="to"/>, as an overlay for
    /// <see cref="Merge"/>: changed and added keys with their new value, removed keys as <c>null</c>.
    /// </summary>
    public static JsonObject Diff(JsonObject from, JsonObject to)
    {
        var diff = new JsonObject();
        foreach (var (key, toValue) in to)
        {
            if (toValue is null)
            {
                continue;
            }

            var fromKey = FindKey(from, key);
            if (fromKey is null || from[fromKey] is null)
            {
                diff[key] = toValue.DeepClone();
            }
            else if (from[fromKey] is JsonObject fromObject && toValue is JsonObject toObject)
            {
                var inner = Diff(fromObject, toObject);
                if (inner.Count > 0)
                {
                    diff[key] = inner;
                }
            }
            else if (!JsonNode.DeepEquals(from[fromKey], toValue))
            {
                diff[key] = toValue.DeepClone();
            }
        }

        foreach (var (key, fromValue) in from)
        {
            if (fromValue is not null && (FindKey(to, key) is not { } toKey || to[toKey] is null))
            {
                diff[key] = null;
            }
        }

        return diff;
    }

    /// <summary>
    /// Applies <paramref name="patch"/> (from <see cref="Diff"/>) to an environment's own layer,
    /// keeping removals as <c>null</c> so they still hide the base's value.
    /// </summary>
    internal static void Patch(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch.ToList())
        {
            var existing = FindKey(target, key);
            if (value is JsonObject patchObject && existing is not null && target[existing] is JsonObject targetObject)
            {
                Patch(targetObject, patchObject);
                continue;
            }

            if (existing is not null)
            {
                target.Remove(existing);
            }

            target[key] = value?.DeepClone();
        }
    }

    /// <summary>
    /// Drops removals (<c>null</c>) of keys the base doesn't have and objects left empty, which
    /// change nothing.
    /// </summary>
    internal static void Prune(JsonObject own, JsonObject? baseNode)
    {
        foreach (var (key, value) in own.ToList())
        {
            var baseKey = baseNode is null ? null : FindKey(baseNode, key);
            var baseValue = baseKey is null ? null : baseNode![baseKey];
            if (value is null)
            {
                if (baseValue is null)
                {
                    own.Remove(key);
                }
            }
            else if (value is JsonObject inner)
            {
                Prune(inner, baseValue as JsonObject);
                if (inner.Count == 0)
                {
                    own.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// The layer to write for an extending environment.
    /// </summary>
    /// <param name="current">The environment as it is now, serialized.</param>
    /// <param name="baseNow">The base as it is now, serialized (resolved).</param>
    /// <param name="loaded">What was read from the file, when this environment was loaded with the same base.</param>
    internal static JsonObject OwnLayer(JsonObject current, JsonObject baseNow, EnvironmentLayer? loaded)
    {
        var now = WithoutMetadata(current);
        var inheritable = Inheritable(baseNow);
        JsonObject own;
        if (loaded is not null)
        {
            // Keep what the file said, plus what this run changed.
            own = (JsonObject)loaded.Own.DeepClone();
            Patch(own, Diff(loaded.Resolved, now));
        }
        else
        {
            own = Diff(inheritable, now);
        }

        own = WithoutMetadata(own);
        Prune(own, inheritable);
        return own;
    }

    internal static JsonObject WithoutMetadata(JsonObject env)
    {
        var copy = (JsonObject)env.DeepClone();
        RemoveKey(copy, ExtendsKey);
        RemoveKey(copy, AbstractKey);
        return copy;
    }

    /// <summary>
    /// Checks the bases of the environments in <paramref name="environments"/>: each must exist,
    /// with no loops and no chain deeper than <see cref="MaxDepth"/>. Returns the problem, or null.
    /// </summary>
    public static string? Validate(IReadOnlyDictionary<string, EnvironmentConfig> environments)
    {
        foreach (var (name, _) in environments)
        {
            var chain = new List<string> { name };
            var current = environments[name];
            while (current.Extends is { } next)
            {
                if (!environments.TryGetValue(next, out var parent))
                {
                    return $"Environment '{chain[^1]}' extends '{next}', which doesn't exist.";
                }

                if (chain.Contains(next, StringComparer.Ordinal))
                {
                    return $"Environments extend each other in a loop ({string.Join(" → ", chain.Append(next))}).";
                }

                chain.Add(next);
                if (chain.Count > MaxDepth)
                {
                    return $"Environment '{name}' extends more than {MaxDepth - 1} levels deep.";
                }

                current = parent;
            }
        }

        return null;
    }

    /// <summary>The environment's chain of bases, nearest first.</summary>
    public static IReadOnlyList<string> BasesOf(string name, IReadOnlyDictionary<string, EnvironmentConfig> environments)
    {
        var bases = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { name };
        while (environments.TryGetValue(name, out var env) && env.Extends is { } next && seen.Add(next))
        {
            bases.Add(next);
            name = next;
        }

        return bases;
    }

    /// <summary>The environments that extend <paramref name="name"/> directly.</summary>
    public static IReadOnlyList<string> ExtendedBy(string name, IReadOnlyDictionary<string, EnvironmentConfig> environments)
        => [.. environments.Where(e => e.Value.Extends == name).Select(e => e.Key)];

    private static void ThrowIfDuplicateKeys(JsonObject node, string at, string what)
    {
        foreach (var group in node.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var keys = group.Select(p => p.Key).ToList();
            if (keys.Count > 1)
            {
                throw new StateFormatException(
                    $"{what} sets {at}.{group.Key} more than once ({string.Join(", ", keys)}); keep one.");
            }

            if (group.First().Value is JsonObject inner)
            {
                ThrowIfDuplicateKeys(inner, $"{at}.{group.Key}", what);
            }
        }
    }

    internal static string? FindKey(JsonObject node, string key)
    {
        if (node.ContainsKey(key))
        {
            return key;
        }

        foreach (var (k, _) in node)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return k;
            }
        }

        return null;
    }

    private static void RemoveKey(JsonObject node, string key)
    {
        while (FindKey(node, key) is { } k)
        {
            node.Remove(k);
        }
    }
}

/// <summary>
/// How an extending environment was read: its own layer from the file and the result of merging
/// it with its bases (serialized after reading, so it compares with what a save serializes).
/// </summary>
internal sealed record EnvironmentLayer(string Extends, JsonObject Own, JsonObject Resolved);
