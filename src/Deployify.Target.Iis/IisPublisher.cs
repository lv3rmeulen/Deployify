using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Iis;

/// <summary>
/// Publishes each project to a folder (<c>dotnet publish -r win-x64 -o …</c>) rather than an
/// image: {outputDir}/{service}/package, with a hash of its contents in package.sha256 that
/// plan and apply use to tell whether the server already runs this build.
/// </summary>
public sealed class IisPublisher(ILogger logger, IProcessRunner processes)
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

    public async Task PublishAsync(IReadOnlyList<IisSite> sites, IisTargetConfig iis, string outputDirectory, CancellationToken ct)
    {
        foreach (var site in sites)
        {
            if (site.ProjectPath is null)
            {
                throw new InvalidOperationException($"{site.Service} has no project path in the manifest, so it can't be published.");
            }

            var packageDir = PackageDirectory(outputDirectory, site.Service);
            if (Directory.Exists(packageDir))
            {
                Directory.Delete(packageDir, recursive: true); // no files left over from an earlier build
            }

            logger.LogInformation("Publishing {Name} ({Runtime}) to {Dir}", site.Service, iis.Runtime, packageDir);
            (await processes.RunAsync(new ProcessSpec("dotnet", PublishArgs(site.ProjectPath, iis, packageDir))
                {
                    Log = logger, LogTag = "publish"
                }, ct))
                .EnsureSuccess($"dotnet publish {site.Service}");

            var hash = ComputePackageHash(packageDir);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, site.Service, PackageHashFile), hash, ct);
        }
    }

    internal static List<string> PublishArgs(string projectPath, IisTargetConfig iis, string packageDir)
        =>
        [
            "publish", projectPath,
            "-c", "Release",
            "-r", iis.Runtime,
            "--self-contained", iis.SelfContained ? "true" : "false",
            "-o", packageDir,
            "-p:IsTransformWebConfigDisabled=false"
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
}
