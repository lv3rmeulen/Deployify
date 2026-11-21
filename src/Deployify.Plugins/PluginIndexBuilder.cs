using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Plugins;

/// <summary>What building an index produced: the index, and what was wrong or left out.</summary>
public sealed record PluginIndexBuildResult(PluginIndex Index, IReadOnlyList<string> Errors, IReadOnlyList<string> Notes);

/// <summary>
/// Builds a plugin index from a folder of entries, one <c>&lt;target-id&gt;.json</c> per plugin
/// (a <see cref="PluginIndexEntry"/>). Every package of every version is downloaded, checked
/// against its SHA-256 (filled in when the entry leaves it out), verified against the entry's
/// signer and unpacked to check it holds the plugin. The result still has to be signed:
/// <c>cosign sign-blob --bundle index.json.sigstore.json index.json</c>.
/// </summary>
public sealed class PluginIndexBuilder(PluginFetcher? fetcher = null, BlobSignatureVerifier? verifier = null, TimeProvider? clock = null)
{
    /// <summary>How long a built index is valid by default. The public index is rebuilt weekly.</summary>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(30);

    private readonly PluginFetcher _fetcher = fetcher ?? new PluginFetcher();
    private readonly BlobSignatureVerifier _verifier = verifier ?? new BlobSignatureVerifier();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Only https:// package URLs. Tests turn it off to use local files.</summary>
    internal bool RequireHttps { get; init; } = true;

    public async Task<PluginIndexBuildResult> BuildAsync(string entriesDirectory, TimeSpan? validity = null, CancellationToken ct = default)
    {
        await _verifier.EnsureAvailableAsync("Building the plugin index", ct);
        var errors = new List<string>();
        var notes = new List<string>();
        var now = _clock.GetUtcNow();
        var index = new PluginIndex
        {
            Generated = now,
            Expires = now + (validity ?? DefaultValidity)
        };

        var work = Directory.CreateTempSubdirectory("deployify-index-build-").FullName;
        try
        {
            foreach (var file in Directory.EnumerateFiles(entriesDirectory, "*.json").Order(StringComparer.Ordinal))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                PluginIndexEntry entry;
                try
                {
                    entry = JsonSerializer.Deserialize<PluginIndexEntry>(await File.ReadAllBytesAsync(file, ct), PluginIndex.Json)
                        ?? throw new JsonException("The file is empty.");
                }
                catch (JsonException ex)
                {
                    errors.Add($"{id}: {Path.GetFileName(file)} can't be read: {ex.Message}");
                    continue;
                }

                if (!string.Equals(id, id.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    errors.Add($"{id}: name the file after the target ID in lowercase.");
                    continue;
                }

                var problems = entry.Validate(id, requireHashes: false).ToList();
                foreach (var package in entry.Versions.SelectMany(v => v.Packages.Values))
                {
                    if (RequireHttps && (!IsHttps(package.Url) || (package.Bundle is not null && !IsHttps(package.Bundle))))
                    {
                        problems.Add($"package URLs must be absolute https:// URLs ({package.Url}).");
                    }
                }

                if (problems.Count > 0)
                {
                    errors.AddRange(problems.Distinct().Select(p => $"{id}: {p}"));
                    continue;
                }

                if (entry.Versions.Count == 0)
                {
                    notes.Add($"{id}: no versions yet, left out.");
                    continue;
                }

                var ok = true;
                foreach (var version in entry.Versions)
                {
                    foreach (var (platform, package) in version.Packages)
                    {
                        var what = $"{id} {version.Version} ({platform})";
                        if (await CheckPackageAsync(id, platform, package, entry.Signer!, Path.Combine(work, Guid.NewGuid().ToString("N")), ct) is { } error)
                        {
                            errors.Add($"{what}: {error}");
                            ok = false;
                        }
                    }
                }

                if (ok)
                {
                    index.Plugins[id] = entry;
                }
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }

        return new PluginIndexBuildResult(index, errors, notes);
    }

    /// <summary>Downloads, hashes, verifies and unpacks one package. Fills in its SHA-256. Null when it is sound.</summary>
    private async Task<string?> CheckPackageAsync(
        string id, string platform, PluginPackage package, PluginSigner signer, string work, CancellationToken ct)
    {
        Directory.CreateDirectory(work);
        var archive = Path.Combine(work, "package.zip");
        var bundle = archive + PluginIndex.BundleSuffix;
        try
        {
            await _fetcher.DownloadAsync(package.Url!, archive, ct);
            var hash = PluginLockFile.HashFile(archive);
            if (package.Sha256 is { } listed && !string.Equals(listed, hash, StringComparison.OrdinalIgnoreCase))
            {
                return $"the package has SHA-256 {hash}, but the entry lists {listed}.";
            }

            package.Sha256 = hash;
            await _fetcher.DownloadAsync(package.BundleUrl, bundle, ct);
            if (await _verifier.VerifyAsync(archive, bundle, signer, ct) is { } problem)
            {
                return $"the package isn't signed by {signer.Describe()}: {problem}";
            }

            var folder = Path.Combine(work, id);
            PluginInstaller.Extract(archive, folder);
            if (PluginInstaller.FindEntry(folder, id, platform) is not { } entry)
            {
                return $"the package has no deployify-target-{id} executable or .dll at its top level.";
            }

            var stem = Path.Combine(folder, Plugin.Contracts.PluginProtocol.ExecutablePrefix + id);
            if (platform == PluginFiles.AnyPlatform && (File.Exists(stem) || File.Exists(stem + ".exe") || !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                return "a package for any platform must be framework-dependent: publish it with -p:UseAppHost=false, so " +
                       "the .dll is the plugin on every OS.";
            }

            return null;
        }
        catch (PluginException ex)
        {
            return ex.Message;
        }
    }

    private static bool IsHttps(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
