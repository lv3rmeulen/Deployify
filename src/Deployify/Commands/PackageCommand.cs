using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Deployify.Target.Nomad;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Zips the published build output for each project service and uploads the zip
/// to the configured artifact store. The artifact URL is written back to
/// <c>deployify-state.json</c> so the next <c>generate</c>/<c>plan</c> picks it up
/// and emits the correct <c>artifact { source = "..." }</c> block in the HCL.
///
/// Only applicable when the active environment targets Nomad with
/// <c>deploymentMode: RawExec</c> and an <c>artifactStore</c> is configured.
/// </summary>
public static class PackageCommand
{
    public static Command Build()
    {
        var publishDirOpt = new Option<string?>("--publish-dir")
        {
            Description = "Directory containing the published binaries. " +
                "Defaults to the dotnet publish output directory (bin/Release/net*/publish) " +
                "for each project found in the manifest."
        };

        var cmd = new Command("package",
            "Zip build output and upload to the artifact store (raw_exec Nomad deployments only).");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(GlobalOptions.DryRun);
        cmd.Options.Add(publishDirOpt);
        var imageTagOpt = new Option<string?>("--image-tag", "-i")
        {
            Description = "Version used in the artifact file names (defaults to the environment's image tag)."
        };
        cmd.Options.Add(imageTagOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var envOverride = parseResult.GetValue(GlobalOptions.Environment);
            var targetOpt = parseResult.GetValue(GlobalOptions.Target);
            var dryRun = parseResult.GetValue(GlobalOptions.DryRun);
            var publishDir = parseResult.GetValue(publishDirOpt);
            var tagOverride = parseResult.GetValue(imageTagOpt);

            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt, ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
                return 1;
            }

            var (_, config, envName, targetType, _, directory) = resolved;
            if (tagOverride is not null)
            {
                config.ImageTag = tagOverride;
            }

            if (targetType != DeploymentTargetType.Nomad)
            {
                AnsiConsole.MarkupLine("[yellow]! package is only applicable to Nomad targets.[/]");
                return 0;
            }

            // Read Nomad target config
            NomadTargetConfig? nomadCfg = null;
            if (config.TargetConfig.TryGetValue("nomad", out var raw) && raw is JsonElement el)
            {
                nomadCfg = el.Deserialize<NomadTargetConfig>(TargetConfigJsonOptions.Default);
            }

            nomadCfg ??= new NomadTargetConfig();

            if (nomadCfg.DeploymentMode != NomadDeploymentMode.RawExec)
            {
                AnsiConsole.MarkupLine("[yellow]! package is only applicable when deploymentMode is RawExec.[/]");
                return 0;
            }

            if (nomadCfg.ArtifactStore is null)
            {
                AnsiConsole.MarkupLine("[red]✗[/] No artifactStore configured in targetConfig.nomad. " +
                    "Add an artifactStore section to deployify-state.json.");
                return 1;
            }

            var manifest = await resolved.LoadManifestAsync(ct);
            if (manifest is null)
            {
                return 1;
            }

            AnsiConsole.MarkupLine(
                $"[dim]Environment: [cyan]{envName}[/] Target: [cyan]Nomad (RawExec)[/] " +
                $"Store: [cyan]{nomadCfg.ArtifactStore.Type}[/][/]\n");

            var logger = resolved.Services.GetRequiredService<ILogger<PackageLog>>();
            var store = ArtifactStoreFactory.Create(nomadCfg.ArtifactStore, logger);

            // Track uploaded URLs to write back to state
            var uploadedUrls = new Dictionary<string, string>();
            var tempZips = new List<string>();

            try
            {
                foreach (var (name, resource) in manifest.Resources)
                {
                    if (!resource.Type.StartsWith("project.v") || resource.Path is null)
                    {
                        continue;
                    }

                    var projectFilePath = ResolveProjectPath(resolved.AppHostDirectory, resource.Path);
                    var projectDir = Path.GetDirectoryName(projectFilePath)!;

                    // Find the publish output directory
                    var sourceDir = publishDir is not null
                        ? Path.GetFullPath(publishDir)
                        : FindPublishDir(projectDir);

                    if (sourceDir is null || !Directory.Exists(sourceDir))
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] Publish output not found for [cyan]{name}[/]. " +
                            "Run [cyan]dotnet publish[/] first.");
                        return 1;
                    }

                    var zipFileName = $"{name}-{config.ImageTag}.zip";
                    var zipPath = Path.Combine(Path.GetTempPath(), zipFileName);
                    tempZips.Add(zipPath);

                    AnsiConsole.Markup($"[bold]Packaging[/] [cyan]{name}[/]... ");

                    if (dryRun)
                    {
                        AnsiConsole.MarkupLine($"[dim](dry-run) would zip {sourceDir} → {zipFileName}[/]");
                        continue;
                    }

                    // Create zip
                    if (File.Exists(zipPath))
                    {
                        File.Delete(zipPath);
                    }

                    await ZipFile.CreateFromDirectoryAsync(sourceDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false, ct);
                    var sizeMb = new FileInfo(zipPath).Length / 1024.0 / 1024.0;
                    AnsiConsole.Markup($"[dim]{sizeMb:F1} MB[/] → uploading... ");

                    // Upload
                    var url = await store.UploadAsync(name, config.ImageTag, zipPath, ct);
                    uploadedUrls[name] = url;

                    AnsiConsole.MarkupLine("[green]✓[/]");
                    AnsiConsole.MarkupLine($"  [dim]{url}[/]");
                }

                if (!dryRun && uploadedUrls.Count > 0)
                {
                    // Write artifact URLs back into state so generate/plan picks them up
                    await WriteArtifactUrlsToStateAsync(directory, envName, uploadedUrls, config, ct);
                    AnsiConsole.MarkupLine("\n[green]✓[/] Artifact URLs saved to deployify-state.json.");
                    AnsiConsole.MarkupLine("[dim]Run [cyan]deployify generate[/] or [cyan]deployify plan[/] to update the HCL.[/]");
                }
            }
            finally
            {
                foreach (var zip in tempZips)
                {
                    try
                    {
                        if (File.Exists(zip))
                        {
                            File.Delete(zip);
                        }
                    }
                    catch
                    {
                        /* ignore */
                    }
                }
            }

            return 0;
        });

        return cmd;
    }

    private static string? FindPublishDir(string projectDir)
    {
        // Look for dotnet publish output: bin/Release/net*/publish
        var binRelease = Path.Combine(projectDir, "bin", "Release");
        if (!Directory.Exists(binRelease))
        {
            return null;
        }

        foreach (var tfm in Directory.GetDirectories(binRelease))
        {
            var publishDir = Path.Combine(tfm, "publish");
            if (Directory.Exists(publishDir))
            {
                return publishDir;
            }
        }

        return null;
    }

    private static string ResolveProjectPath(string appHostDirectory, string resourcePath)
    {
        var normalized = resourcePath.Replace('/', Path.DirectorySeparatorChar)
                                     .Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(appHostDirectory, normalized));
    }

    /// <summary>
    /// Writes the uploaded artifact URLs into a service-overrides-like structure in
    /// the state file so the job generator can pick them up on the next generate/plan.
    ///
    /// URLs are stored as <c>serviceOverrides[name].artifactUrl</c>.
    /// </summary>
    private static async Task WriteArtifactUrlsToStateAsync(
        string directory,
        string envName,
        Dictionary<string, string> uploadedUrls,
        EnvironmentConfig config,
        CancellationToken ct)
    {
        // Ensure serviceOverrides exist for each service
        foreach (var (name, url) in uploadedUrls)
        {
            if (!config.ServiceOverrides.TryGetValue(name, out var ov))
            {
                ov = new ServiceOverride();
                config.ServiceOverrides[name] = ov;
            }

            ov.ArtifactUrl = url;
        }

        // Go through the StateManager so the file keeps its format and every other
        // environment is preserved.
        var stateManager = new StateManager();
        var stateFile = await stateManager.LoadFileAsync(directory, ct);
        stateFile.Environments[envName] = config;
        await stateManager.SaveFileAsync(stateFile, directory, ct);
    }

    /// <summary>Log category for this command (a static class can't be a type argument).</summary>
    private sealed class PackageLog;
}
