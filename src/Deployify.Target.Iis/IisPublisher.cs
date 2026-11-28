using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Core.Services;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Iis;

/// <summary>
/// Publishes each project to a folder (<c>dotnet publish -r win-x64 -o …</c>) rather than an
/// image: {outputDir}/{service}/package, with a hash of its contents in package.sha256 that
/// plan and apply use to tell whether the server already runs this build. The folder layout
/// and hashing are shared with the host target (<see cref="FolderPackage"/>).
/// </summary>
public sealed class IisPublisher(ILogger logger, IProcessRunner processes)
{
    public const string PackageFolder = FolderPackage.PackageFolder;
    public const string PackageHashFile = FolderPackage.PackageHashFile;

    public static string PackageDirectory(string outputDirectory, string service)
        => FolderPackage.PackageDirectory(outputDirectory, service);

    /// <summary>The hash publish recorded, or null when the service hasn't been published.</summary>
    public static string? ReadPackageHash(string outputDirectory, string service)
        => FolderPackage.ReadPackageHash(outputDirectory, service);

    public async Task PublishAsync(IReadOnlyList<IisSite> sites, IisTargetConfig iis, string outputDirectory, CancellationToken ct)
    {
        foreach (var site in sites)
        {
            if (site.ProjectPath is null)
            {
                throw new InvalidOperationException($"{site.Service} has no project path in the manifest, so it can't be published.");
            }

            await FolderPackage.PublishAsync(processes, logger, site.Service, site.ProjectPath, outputDirectory,
                iis.Runtime, iis.SelfContained, ["-p:IsTransformWebConfigDisabled=false"], ct);
        }
    }

    internal static List<string> PublishArgs(string projectPath, IisTargetConfig iis, string packageDir)
        => FolderPackage.PublishArgs(projectPath, iis.Runtime, iis.SelfContained, packageDir, ["-p:IsTransformWebConfigDisabled=false"]);

    /// <summary>SHA-256 over every file's relative path and contents, in a fixed order. Empty or missing folder: the hash of nothing.</summary>
    public static string ComputePackageHash(string directory) => FolderPackage.ComputePackageHash(directory);

    /// <summary>
    /// The project file for a manifest path. Aspire writes paths relative to the AppHost and
    /// sometimes with one ../ too many; try both, as the image publisher does.
    /// </summary>
    public static string ResolveProjectPath(string? appHostDirectory, string resourcePath)
        => FolderPackage.ResolveProjectPath(appHostDirectory, resourcePath);
}
