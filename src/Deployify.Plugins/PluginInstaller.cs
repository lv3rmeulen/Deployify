using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Plugin.Contracts;

namespace Deployify.Plugins;

/// <summary>A plugin package that has been downloaded, checked and unpacked, but not yet put in place.</summary>
public sealed record StagedPlugin
{
    public required string TargetId { get; init; }

    public required string Version { get; init; }

    /// <summary>The index entry's display name.</summary>
    public required string Name { get; init; }

    /// <summary>The package platform: a runtime ID or <see cref="PluginFiles.AnyPlatform"/>.</summary>
    public required string PackagePlatform { get; init; }

    /// <summary>The unpacked files, in a staging folder.</summary>
    public required PluginFiles Files { get; init; }

    /// <summary>SHA-256 of <see cref="Files"/>, as the lock file records it. Computed before anything ran.</summary>
    public required string Hash { get; init; }

    /// <summary>Who signed the package.</summary>
    public required PluginSigner Signer { get; init; }

    /// <summary>Why the version was yanked, when it was.</summary>
    public string? Yanked { get; init; }

    /// <summary>The folder it is installed into: <c>&lt;plugins folder&gt;/&lt;id&gt;</c>.</summary>
    public required string Destination { get; init; }

    internal string StagingRoot { get; init; } = string.Empty;

    /// <summary>The entry file's path once installed.</summary>
    public string InstalledEntryPath => Path.Combine(Destination, Path.GetFileName(Files.EntryPath));
}

/// <summary>
/// Installs plugins from a <see cref="PluginIndex"/>: downloads the package for this platform,
/// checks its SHA-256 against the index and its signature against the publisher the index names,
/// unpacks it into a staging folder and, once the caller is satisfied, moves it to
/// <c>&lt;plugins folder&gt;/&lt;id&gt;/</c>, where <see cref="PluginCatalog"/> finds it and the lock
/// hash covers the whole folder.
/// </summary>
public sealed class PluginInstaller(PluginFetcher? fetcher = null, BlobSignatureVerifier? verifier = null)
{
    private const string StagingFolder = ".plugin-staging";

    private readonly PluginFetcher _fetcher = fetcher ?? new PluginFetcher();
    private readonly BlobSignatureVerifier _verifier = verifier ?? new BlobSignatureVerifier();

    /// <summary>
    /// Downloads, checks and unpacks <paramref name="targetId"/> (version <paramref name="version"/>,
    /// or the newest that isn't yanked) for <paramref name="platform"/>. Nothing in it runs. Call
    /// <see cref="CommitAsync"/> to install it or <see cref="Discard"/> to throw it away.
    /// </summary>
    /// <param name="pluginsDirectory">The plugins folder: <c>~/.deployify/plugins</c> or the project's.</param>
    public async Task<StagedPlugin> StageAsync(
        PluginIndex index, PluginIndexSource source, string targetId, string? version, string pluginsDirectory,
        string? platform = null, CancellationToken ct = default)
    {
        platform ??= PluginFiles.CurrentPlatform;
        var entry = index.Find(targetId)
            ?? throw new PluginException($"The plugin index {source.Url} has no plugin '{targetId}'. 'deployify plugin search' lists what it has.");
        targetId = index.Plugins.Keys.First(k => string.Equals(k, targetId, StringComparison.OrdinalIgnoreCase));

        var release = version is null
            ? entry.Latest() ?? throw new PluginException($"'{targetId}' has no installable version in the plugin index.")
            : entry.FindVersion(version) ?? throw new PluginException(
                $"'{targetId}' has no version {version} in the plugin index. Versions: {string.Join(", ", entry.Versions.Select(v => v.Version))}.");

        if (release.Protocol is { } protocol && Version.TryParse(protocol, out var speaks) && speaks.Major != PluginProtocol.Version.Major)
        {
            throw new PluginException(
                $"{targetId} {release.Version} speaks plugin protocol {protocol}; this Deployify speaks {PluginProtocol.Version.ToString(2)}.");
        }

        if (release.PackageFor(platform) is not var (packagePlatform, package))
        {
            throw new PluginException(
                $"{targetId} {release.Version} has no package for {platform} (it has {string.Join(", ", release.Packages.Keys)}).");
        }

        await _verifier.EnsureAvailableAsync($"Installing {targetId}", ct);

        var root = Path.GetFullPath(pluginsDirectory);
        var stagingRoot = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))!, StagingFolder, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(stagingRoot);
        try
        {
            var archive = Path.Combine(stagingRoot, "package.zip");
            var bundle = archive + PluginIndex.BundleSuffix;
            await _fetcher.DownloadAsync(source.Resolve(package.Url!), archive, ct);

            var actual = PluginLockFile.HashFile(archive);
            if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginException(
                    $"The {targetId} {release.Version} package for {packagePlatform} has SHA-256 {actual[..12]}…, but the index says " +
                    $"{package.Sha256![..12]}…. It was not installed.");
            }

            try
            {
                await _fetcher.DownloadAsync(source.Resolve(package.BundleUrl), bundle, ct);
            }
            catch (PluginException ex)
            {
                throw new PluginException($"The {targetId} {release.Version} package has no signature ({ex.Message}). It was not installed.", ex);
            }

            if (await _verifier.VerifyAsync(archive, bundle, entry.Signer!, ct) is { } problem)
            {
                throw new PluginException(
                    $"The {targetId} {release.Version} package isn't signed by its publisher ({entry.Signer!.Describe()}): {problem}. " +
                    "It was not installed.");
            }

            var folder = Path.Combine(stagingRoot, targetId);
            Extract(archive, folder);
            var entryPath = FindEntry(folder, targetId, packagePlatform)
                ?? throw new PluginException(
                    $"The {targetId} {release.Version} package has no {PluginProtocol.ExecutablePrefix}{targetId} executable or .dll at its top level.");

            var files = PluginFiles.ForOwnFolder(entryPath);
            var hash = files.ComputeHash();
            return new StagedPlugin
            {
                TargetId = targetId,
                Version = release.Version!,
                Name = entry.Name ?? targetId,
                PackagePlatform = packagePlatform,
                Files = files,
                Hash = hash,
                Signer = entry.Signer!,
                Yanked = release.Yanked,
                Destination = Path.Combine(root, targetId),
                StagingRoot = stagingRoot
            };
        }
        catch
        {
            TryDelete(stagingRoot);
            throw;
        }
    }

    /// <summary>Moves a staged plugin into its folder, replacing what was there.</summary>
    public Task CommitAsync(StagedPlugin staged, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(staged.Destination)!);
        var previous = Path.Combine(staged.StagingRoot, "previous");
        try
        {
            if (Directory.Exists(staged.Destination))
            {
                Directory.Move(staged.Destination, previous);
            }

            Directory.Move(Path.GetDirectoryName(staged.Files.EntryPath)!, staged.Destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!Directory.Exists(staged.Destination) && Directory.Exists(previous))
            {
                Directory.Move(previous, staged.Destination);
            }

            throw new PluginException($"Could not install {staged.TargetId} into {staged.Destination}: {ex.Message}", ex);
        }

        Discard(staged);
        return Task.CompletedTask;
    }

    /// <summary>Removes a staged plugin that won't be installed.</summary>
    public void Discard(StagedPlugin staged)
    {
        TryDelete(staged.StagingRoot);
        var parent = Path.GetDirectoryName(staged.StagingRoot)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            TryDelete(parent);
        }
    }

    /// <summary>Unpacks <paramref name="archive"/>, refusing entries that would land outside <paramref name="folder"/>.</summary>
    internal static void Extract(string archive, string folder)
    {
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var item in zip.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(root, item.FullName.Replace('\\', '/')));
                if (!target.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    throw new PluginException($"The plugin package has an entry outside its folder ({item.FullName}); it was not installed.");
                }

                // Unix symlinks are stored with mode 0120000 in the upper half of the external attributes.
                if (((item.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    throw new PluginException($"The plugin package has a symbolic link ({item.FullName}); it was not installed.");
                }

                if (item.FullName.EndsWith('/') || item.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                item.ExtractToFile(target, overwrite: false);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new PluginException($"The plugin package isn't a valid .zip: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The plugin's entry point at the top of <paramref name="folder"/>: the executable for this OS,
    /// or the framework-dependent <c>.dll</c>. A Unix executable zipped without its mode is made executable.
    /// </summary>
    internal static string? FindEntry(string folder, string targetId, string packagePlatform)
    {
        var stem = PluginProtocol.ExecutablePrefix + targetId;
        if (!OperatingSystem.IsWindows() && packagePlatform != PluginFiles.AnyPlatform)
        {
            var unix = Path.Combine(folder, stem);
            if (File.Exists(unix) && (File.GetUnixFileMode(unix) & UnixFileMode.UserExecute) == 0)
            {
                File.SetUnixFileMode(unix, File.GetUnixFileMode(unix) | UnixFileMode.UserExecute);
            }
        }

        // The same choice PluginCatalog makes when it scans the folder later, so the platform the
        // lock hash is recorded under is the one it is looked up under.
        var catalog = new PluginCatalog(userDirectory: null, getVariable: _ => null);
        return Directory.EnumerateFiles(folder)
            .OrderBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault(f => string.Equals(catalog.TargetIdFor(f, allowDll: true), targetId, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
