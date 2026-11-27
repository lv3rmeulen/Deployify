using System;
using System.Collections.Generic;

namespace Deployify.Core.Versioning;

/// <summary>
/// Orders version strings the way Semantic Versioning does: major, minor and patch as numbers,
/// a release after its pre-releases, pre-release identifiers compared one by one (numbers as
/// numbers), and build metadata (<c>+…</c>) ignored. Used to pick updates for Deployify itself
/// and for plugins. Text that isn't a version sorts as 0.0.0.
/// </summary>
public static class VersionOrder
{
    /// <summary>Compares two versions by SemVer precedence.</summary>
    public static IComparer<string?> Comparer { get; } = Comparer<string?>.Create(Compare);

    public static int Compare(string? left, string? right)
    {
        var a = Parse(left);
        var b = Parse(right);
        for (var i = 0; i < 3; i++)
        {
            if (a.Core[i] != b.Core[i])
            {
                return a.Core[i].CompareTo(b.Core[i]);
            }
        }

        // A release comes after its pre-releases.
        if (a.Pre.Length == 0 || b.Pre.Length == 0)
        {
            return (a.Pre.Length == 0).CompareTo(b.Pre.Length == 0);
        }

        for (var i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
        {
            var x = a.Pre[i];
            var y = b.Pre[i];
            var xNumber = long.TryParse(x, out var xn);
            var yNumber = long.TryParse(y, out var yn);
            var order = (xNumber, yNumber) switch
            {
                (true, true) => xn.CompareTo(yn),
                (true, false) => -1, // numbers come before words
                (false, true) => 1,
                _ => string.CompareOrdinal(x, y)
            };
            if (order != 0)
            {
                return Math.Sign(order);
            }
        }

        return a.Pre.Length.CompareTo(b.Pre.Length);
    }

    /// <summary>Whether <paramref name="version"/> has a pre-release part (<c>1.2.0-beta.1</c>).</summary>
    public static bool IsPrerelease(string? version) => Parse(version).Pre.Length > 0;

    /// <summary>The version without its build metadata: <c>1.2.0-beta.1+abc123</c> is <c>1.2.0-beta.1</c>.</summary>
    public static string WithoutMetadata(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> can replace <paramref name="current"/> without breaking
    /// changes under SemVer: the same major version, and for 0.x the same minor version too.
    /// </summary>
    public static bool IsCompatible(string current, string candidate)
    {
        var a = Parse(current);
        var b = Parse(candidate);
        return a.Core[0] == b.Core[0] && (a.Core[0] != 0 || a.Core[1] == b.Core[1]);
    }

    /// <summary>
    /// Whether moving from <paramref name="current"/> to <paramref name="candidate"/> is an update
    /// Deployify offers: <paramref name="candidate"/> is newer; it is a pre-release only when
    /// <paramref name="includePrerelease"/> or <paramref name="current"/> is one; and it has no
    /// breaking changes (<see cref="IsCompatible"/>) unless <paramref name="crossMajor"/>.
    /// </summary>
    public static bool IsUpdate(string current, string candidate, bool includePrerelease = false, bool crossMajor = false)
        => Compare(candidate, current) > 0
           && (includePrerelease || IsPrerelease(current) || !IsPrerelease(candidate))
           && (crossMajor || IsCompatible(current, candidate));

    /// <summary>The major version: <c>2</c> for <c>2.1.0</c>.</summary>
    public static long Major(string version) => Parse(version).Core[0];

    private static (long[] Core, string[] Pre) Parse(string? version)
    {
        var text = WithoutMetadata((version ?? string.Empty).Trim().TrimStart('v', 'V'));
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var core = dash >= 0 ? text[..dash] : text;
        var pre = dash >= 0 ? text[(dash + 1)..] : string.Empty;
        var parts = core.Split('.');
        var numbers = new long[3];
        for (var i = 0; i < 3; i++)
        {
            numbers[i] = i < parts.Length && long.TryParse(parts[i], out var n) ? n : 0;
        }

        return (numbers, pre.Length == 0 ? [] : pre.Split('.'));
    }
}
