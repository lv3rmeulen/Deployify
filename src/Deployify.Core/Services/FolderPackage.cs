using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Deployify.Core.Services;

/// <summary>
/// A project published to a folder (<c>dotnet publish -r … -o …</c>) instead of an image, for
/// targets that run apps straight on a server (IIS, the host target). Lives at
/// {outputDir}/{service}/package, with a hash of its contents in package.sha256 that plan and
/// apply use to tell whether the server already runs this build.
/// </summary>
public static class FolderPackage
{
    public const string PackageFolder = "package";
    public const string PackageHashFile = "package.sha256";

    public static string PackageDirectory(string outputDirectory, string service)
        => Path.Combine(outputDirectory, service, PackageFolder);

    /// <summary>The hash publish recorded, or null when the service hasn't been published.</summary>
    public static string? ReadPackageHash(string outputDirectory, string service)
    {
        var hashFile = Path.Combine(outputDirectory, service, PackageHashFile);
        return File.Exists(hashFile) && Directory.Exists(PackageDirectory(outputDirectory, service))
            ? File.ReadAllText(hashFile).Trim()
            : null;
    }

    /// <summary>
    /// Publishes <paramref name="projectPath"/> into a clean package folder and records its hash.
    /// Returns the package folder.
    /// </summary>
    public static async Task<string> PublishAsync(
        IProcessRunner processes, ILogger logger, string service, string projectPath, string outputDirectory,
        string runtime, bool selfContained, IReadOnlyList<string>? extraArgs, CancellationToken ct)
    {
        var packageDir = PackageDirectory(outputDirectory, service);
        if (Directory.Exists(packageDir))
        {
            Directory.Delete(packageDir, recursive: true); // no files left over from an earlier build
        }

        logger.LogInformation("Publishing {Name} ({Runtime}) to {Dir}", service, runtime, packageDir);
        (await processes.RunAsync(new ProcessSpec("dotnet", PublishArgs(projectPath, runtime, selfContained, packageDir, extraArgs))
            {
                Log = logger, LogTag = "publish"
            }, ct))
            .EnsureSuccess($"dotnet publish {service}");

        await File.WriteAllTextAsync(Path.Combine(outputDirectory, service, PackageHashFile), ComputePackageHash(packageDir), ct);
        return packageDir;
    }

    public static List<string> PublishArgs(
        string projectPath, string runtime, bool selfContained, string packageDir, IReadOnlyList<string>? extraArgs = null)
        =>
        [
            "publish", projectPath,
            "-c", "Release",
            "-r", runtime,
            "--self-contained", selfContained ? "true" : "false",
            "-o", packageDir,
            .. extraArgs ?? []
        ];

    /// <summary>SHA-256 over every file's relative path and contents, in a fixed order. Empty or missing folder: the hash of nothing.</summary>
    public static string ComputePackageHash(string directory)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (Directory.Exists(directory))
        {
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(f => (Full: f, Relative: Path.GetRelativePath(directory, f).Replace('\\', '/')))
                .OrderBy(f => f.Relative, StringComparer.Ordinal);
            foreach (var (full, relative) in files)
            {
                sha.AppendData(Encoding.UTF8.GetBytes(relative + "\n"));
                using var stream = File.OpenRead(full);
                sha.AppendData(SHA256.HashData(stream));
            }
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>
    /// The project file for a manifest path. Aspire writes paths relative to the AppHost and
    /// sometimes with one ../ too many; try both, as the image publisher does.
    /// </summary>
    public static string ResolveProjectPath(string? appHostDirectory, string resourcePath)
    {
        var normalized = resourcePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || appHostDirectory is null)
        {
            return normalized;
        }

        var resolved = Path.GetFullPath(Path.Combine(appHostDirectory, normalized));
        if (File.Exists(resolved))
        {
            return resolved;
        }

        var parent = ".." + Path.DirectorySeparatorChar;
        if (normalized.StartsWith(parent, StringComparison.Ordinal))
        {
            var stripped = Path.GetFullPath(Path.Combine(appHostDirectory, normalized[parent.Length..]));
            if (File.Exists(stripped))
            {
                return stripped;
            }
        }

        return resolved;
    }

    /// <summary>
    /// Release folder name: the image tag (made safe for a folder name) and the start of the
    /// fingerprint, so every distinct build-plus-settings gets its own immutable folder.
    /// </summary>
    public static string ReleaseName(string imageTag, string fingerprint)
    {
        var tag = new string([.. imageTag.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_')]);
        return $"{(tag.Length == 0 ? "release" : tag)}-{fingerprint[..12]}";
    }

    /// <summary>
    /// The app a package starts: the name of its <c>*.runtimeconfig.json</c> (the assembly name,
    /// which is also the apphost's name). Null when the folder holds no app.
    /// </summary>
    public static string? EntryPoint(string packageDirectory)
    {
        if (!Directory.Exists(packageDirectory))
        {
            return null;
        }

        const string suffix = ".runtimeconfig.json";
        return Directory.EnumerateFiles(packageDirectory, "*" + suffix, SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n![..^suffix.Length])
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
