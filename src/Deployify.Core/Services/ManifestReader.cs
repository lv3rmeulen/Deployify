using System.Diagnostics;
using System.Text.Json;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging;

namespace Deployify.Core.Services;

public sealed class ManifestReader(ILogger<ManifestReader> logger) : IManifestReader
{
    private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public async Task<AspireManifest> ReadAsync(string manifestPath, CancellationToken ct = default)
    {
        if (!File.Exists(manifestPath))
        {
	        throw new FileNotFoundException($"Manifest not found at '{manifestPath}'.");
        }

        await using var stream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<AspireManifest>(stream, s_jsonOptions, ct)
               ?? throw new InvalidDataException("Failed to deserialize manifest.json.");
    }

    public async Task<string> GenerateAsync(string appHostPath, string outputPath, CancellationToken ct = default)
    {
        // Resolve: if a directory was given, look for a single .csproj inside it.
        var resolvedProject = appHostPath;
        if (Directory.Exists(appHostPath))
        {
            var projs = Directory.GetFiles(appHostPath, "*.csproj", SearchOption.TopDirectoryOnly);
            if (projs.Length == 0)
            {
	            throw new FileNotFoundException(
		            $"No .csproj found in '{appHostPath}'. " +
		            "Run 'deployify generate' or pre-generate a manifest.json in the output directory.");
            }

            resolvedProject = projs[0];
        }
        else if (!File.Exists(appHostPath))
        {
            throw new FileNotFoundException(
                $"AppHost project not found: '{appHostPath}'. " +
                "Run 'deployify generate' or pre-generate a manifest.json in the output directory.");
        }

        var manifestFile = Path.Combine(outputPath, "manifest.json");
        logger.LogInformation("Generating manifest from '{Path}'...", resolvedProject);
        var psi = new ProcessStartInfo("dotnet",
            $"run --project \"{resolvedProject}\" --publisher manifest --output-path \"{manifestFile}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start dotnet.");

        // Drain stdout and stderr concurrently to prevent pipe deadlock
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);

        if (proc.ExitCode != 0)
        {
	        throw new InvalidOperationException($"Manifest generation failed: {stderrTask.Result}");
        }

        return manifestFile;
    }
}
