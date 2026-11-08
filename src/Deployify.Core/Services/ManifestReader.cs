using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Deployify.Core.Services;

public sealed class ManifestReader(ILogger<ManifestReader> logger) : IManifestReader
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
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
        var result = await ProcessRunner.Default.RunAsync(new ProcessSpec("dotnet",
            ["run", "--project", resolvedProject, "--publisher", "manifest", "--output-path", manifestFile])
        {
            Log = logger
        }, ct);

        if (!result.Ok)
        {
            throw new InvalidOperationException($"Manifest generation failed: {result.ErrorOrOutput.Trim()}");
        }

        return manifestFile;
    }
}
