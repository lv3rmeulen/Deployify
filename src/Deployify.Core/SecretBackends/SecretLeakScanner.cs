using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Deployify.Core.SecretBackends;

/// <summary>Thrown when a secret value would be, or was, written somewhere it must not be.</summary>
public sealed class SecretLeakException(string message) : InvalidOperationException(message);

/// <summary>One secret found where it shouldn't be. Holds the secret's id, never the value.</summary>
public sealed record SecretLeak(string Location, string SecretId);

/// <summary>
/// Last line of defense: after artifacts are written, checks that none of them contains a
/// secret value that was resolved during the run. Whatever the generators do, a leak fails
/// the command instead of reaching git or a CI artifact.
/// </summary>
public static class SecretLeakScanner
{
    /// <summary>
    /// Values shorter than this aren't scanned for. A one- or two-character secret would match
    /// all over a YAML file; secrets that short are a problem in themselves.
    /// </summary>
    public const int MinimumLength = 4;

    public static IReadOnlyList<SecretLeak> ScanText(
        string location, string text, IReadOnlyDictionary<string, SensitiveValue> secrets)
    {
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            .. secrets
                .Where(s => s.Value.Length >= MinimumLength && text.Contains(s.Value.Reveal(), StringComparison.Ordinal))
                .Select(s => new SecretLeak(location, s.Key))
                .OrderBy(l => l.SecretId, StringComparer.Ordinal)
        ];
    }

    public static IReadOnlyList<SecretLeak> ScanFiles(
        IEnumerable<string> files, IReadOnlyDictionary<string, SensitiveValue> secrets)
    {
        if (secrets.Count == 0)
        {
            return [];
        }

        var leaks = new List<SecretLeak>();
        foreach (var file in files.Where(File.Exists))
        {
            leaks.AddRange(ScanText(file, File.ReadAllText(file), secrets));
        }

        return leaks;
    }

    /// <summary>Scans every file under <paramref name="directory"/>.</summary>
    public static IReadOnlyList<SecretLeak> ScanDirectory(
        string directory, IReadOnlyDictionary<string, SensitiveValue> secrets)
        => Directory.Exists(directory)
            ? ScanFiles(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories), secrets)
            : [];

    /// <summary>Throws a <see cref="SecretLeakException"/> naming the files and secret ids.</summary>
    public static void ThrowIfAny(IReadOnlyList<SecretLeak> leaks, string what = "Generated output")
    {
        if (leaks.Count == 0)
        {
            return;
        }

        var lines = leaks.Select(l => $"  {l.Location}: {l.SecretId}");
        throw new SecretLeakException(
            $"{what} contains secret values, which the configured secret backend must keep out:\n" +
            string.Join('\n', lines) +
            "\nThe files were not used. Please report this as a bug, and delete the files listed above.");
    }

    /// <summary>Scans <paramref name="files"/>, deletes any that leak, and throws.</summary>
    public static void EnsureClean(IEnumerable<string> files, IReadOnlyDictionary<string, SensitiveValue> secrets)
    {
        var list = files.ToList();
        var leaks = ScanFiles(list, secrets);
        if (leaks.Count == 0)
        {
            return;
        }

        foreach (var file in leaks.Select(l => l.Location).Distinct())
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Reported below either way.
            }
        }

        ThrowIfAny(leaks);
    }
}
