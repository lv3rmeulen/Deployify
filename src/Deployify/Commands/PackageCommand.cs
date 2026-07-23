using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Core.Models;
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
        var publishDirOpt = new Option<string?>("--publish-dir",
            "Directory containing the published binaries. " +
            "Defaults to the dotnet publish output directory (bin/Release/net*/publish) " +
            "for each project found in the manifest.");

        var cmd = new Command("package",
            "Zip build output and upload to the artifact store (raw_exec Nomad deployments only).");
        cmd.AddOption(GlobalOptions.ProjectPath);
        cmd.AddOption(GlobalOptions.Environment);
        cmd.AddOption(GlobalOptions.Target);
        cmd.AddOption(GlobalOptions.DryRun);
        cmd.AddOption(publishDirOpt);

        cmd.SetHandler(async ctx =>
        {
            var projectPath = ctx.ParseResult.GetValueForOption(GlobalOptions.ProjectPath)!;
            var envOverride = ctx.ParseResult.GetValueForOption(GlobalOptions.Environment);
            var targetOpt = ctx.ParseResult.GetValueForOption(GlobalOptions.Target);
            var dryRun = ctx.ParseResult.GetValueForOption(GlobalOptions.DryRun);
            var publishDir = ctx.ParseResult.GetValueForOption(publishDirOpt);

            ResolvedContext resolved;
            try { resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]✗[/] {ex.Message}"); ctx.ExitCode = 1; return; }

            var (_, config, envName, targetType, _, directory) = resolved;

            if (targetType != DeploymentTargetType.Nomad)
            {
                AnsiConsole.MarkupLine("[yellow]! package is only applicable to Nomad targets.[/]");
                return;
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
                return;
            }

            if (nomadCfg.ArtifactStore is null)
            {
                AnsiConsole.MarkupLine("[red]✗[/] No artifactStore configured in targetConfig.nomad. " +
                    "Add an artifactStore section to deployify-state.json.");
                ctx.ExitCode = 1;
                return;
            }

            var manifest = await resolved.LoadManifestAsync();
            if (manifest is null) { ctx.ExitCode = 1; return; }

            AnsiConsole.MarkupLine(
                $"[dim]Environment: [cyan]{envName}[/] Target: [cyan]Nomad (RawExec)[/] " +
                $"Store: [cyan]{nomadCfg.ArtifactStore.Type}[/][/]\n");

            var logger = resolved.Services.GetRequiredService<ILogger<PackageCommandStatic>>();
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
                        : FindPublishDir(projectDir, config);

                    if (sourceDir is null || !Directory.Exists(sourceDir))
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] Publish output not found for [cyan]{name}[/]. " +
                            "Run [cyan]dotnet publish[/] first.");
                        ctx.ExitCode = 1;
                        return;
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

                    await ZipFile.CreateFromDirectoryAsync(sourceDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                    var sizeMb = new FileInfo(zipPath).Length / 1024.0 / 1024.0;
                    AnsiConsole.Markup($"[dim]{sizeMb:F1} MB[/] → uploading... ");

                    // Upload
                    var url = await store.UploadAsync(name, config.ImageTag, zipPath, ctx.GetCancellationToken());
                    uploadedUrls[name] = url;

                    AnsiConsole.MarkupLine("[green]✓[/]");
                    AnsiConsole.MarkupLine($"  [dim]{url}[/]");
                }

                if (!dryRun && uploadedUrls.Count > 0)
                {
                    // Write artifact URLs back into state so generate/plan picks them up
                    await WriteArtifactUrlsToStateAsync(directory, envName, uploadedUrls, config);
                    AnsiConsole.MarkupLine("\n[green]✓[/] Artifact URLs saved to deployify-state.json.");
                    AnsiConsole.MarkupLine("[dim]Run [cyan]deployify generate[/] or [cyan]deployify plan[/] to update the HCL.[/]");
                }
            }
            finally
            {
                foreach (var zip in tempZips)
                {
	                try { if (File.Exists(zip))
		                {
			                File.Delete(zip);
		                }
	                } catch { /* ignore */ }
                }
            }
        });

        return cmd;
    }

    private static string? FindPublishDir(string projectDir, EnvironmentConfig _)
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
        var normalised = resourcePath.Replace('/', Path.DirectorySeparatorChar)
                                     .Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(appHostDirectory, normalised));
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
        EnvironmentConfig config)
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

        var stateFilePath = Path.Combine(directory, "deployify-state.json");
        var existing = JsonSerializer.Deserialize<DeployifyStateFile>(
            await File.ReadAllTextAsync(stateFilePath),
            s_stateManagerJsonOptions) ?? new DeployifyStateFile();

        existing.Environments[envName] = config;

        await File.WriteAllTextAsync(stateFilePath,
            JsonSerializer.Serialize(existing, s_stateManagerJsonOptions));
    }

    private static readonly JsonSerializerOptions s_stateManagerJsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // Marker type for ILogger generic
    private sealed class PackageCommandStatic;
}
