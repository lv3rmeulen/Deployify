using System.Text.Json;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.AzureContainerApps;

/// <summary>
/// IDeploymentTarget implementation for Azure Container Apps.
///
/// Output structure:
///   {outputDir}/
///     {service}.containerapp.json    ← Container App ARM/YAML spec
///
/// Apply strategy:
///   1. Write app specs to disk (PlanAsync)
///   2. Deploy via `az containerapp create/update --yaml` (ApplyAsync)
///
/// Requires the Azure CLI to be installed and logged in.
/// </summary>
public sealed class AcaDeploymentTarget(
    IPublisher publisher,
    ILogger<AcaDeploymentTarget> logger) : IDeploymentTarget
{
    private readonly AcaManifestGenerator _generator = new AcaManifestGenerator();

    public DeploymentTargetType TargetType => DeploymentTargetType.AzureContainerApps;
    public string DisplayName => "Azure Container Apps";

    // ── Ping ──────────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(
        EnvironmentConfig config, CancellationToken ct = default)
    {
        if (!await AcaCliClient.IsAvailableAsync())
        {
	        return (false, "Azure CLI ('az') not found on PATH. Install it from https://aka.ms/install-azure-cli");
        }

        var cfg = GetConfig(config);
        var client = new AcaCliClient(cfg, logger);
        return await client.PingAsync(ct);
    }

    // ── Generate ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var apps = _generator.Generate(manifest, config, cfg);
        var paths = new List<string>();
        foreach (var app in apps)
        {
	        paths.Add(await _generator.WriteAppSpecAsync(app, config, cfg, outputDirectory, ct));
        }

        return paths;
    }

    // ── Validate ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var apps = _generator.Generate(manifest, config, cfg);
        var results = new List<ValidationResult>();
        var client = new AcaCliClient(cfg, logger);

        foreach (var app in apps)
        {
            var path = await _generator.WriteAppSpecAsync(app, config, cfg, outputDirectory, ct);
            var errors = await client.ValidateAppSpecAsync(path, ct);
            results.Add(new ValidationResult { ArtefactName = path, Errors = errors });
        }

        return results;
    }

    // ── Plan ─────────────────────────────────────────────────────────────────

    public async Task<PlanResult> PlanAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var apps = _generator.Generate(manifest, config, cfg);

        // Write specs to disk — apply reads these
        foreach (var app in apps)
        {
	        await _generator.WriteAppSpecAsync(app, config, cfg, outputDirectory, ct);
        }

        var desired = apps.Select(a => a.Name).ToHashSet();
        var client = new AcaCliClient(cfg, logger);
        HashSet<string> running;
        try { running = (await client.GetRunningAppNamesAsync(ct)).ToHashSet(); }
        catch { running = []; }

        return new PlanResult
        {
            ToCreate = desired.Except(running).Select(n => (object)n).ToList(),
            ToUpdate = desired.Intersect(running).Select(n => (object)n).ToList(),
            ToDelete = running.Except(desired).ToList(),
            Unchanged = []
        };
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var apps = _generator.Generate(manifest, config, cfg);
        var client = new AcaCliClient(cfg, logger);
        var results = new List<DeploymentResult>();

        foreach (var app in apps)
        {
            var specPath = Path.Combine(outputDirectory, $"{app.Name}.containerapp.json");
            if (!File.Exists(specPath))
            {
                results.Add(new DeploymentResult
                {
                    ServiceName = app.Name,
                    Success = false,
                    Error = $"App spec not found: {specPath}. Run 'deployify plan' first."
                });
                continue;
            }

            try
            {
                var resourceId = await client.DeployAppAsync(specPath, app.Name, ct);
                logger.LogInformation("Deployed Container App '{Name}' → {Id}", app.Name, resourceId);

                results.Add(new DeploymentResult
                {
                    ServiceName = app.Name,
                    DeploymentId = resourceId,
                    Success = true
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to deploy Container App '{Name}'.", app.Name);
                results.Add(new DeploymentResult
                {
                    ServiceName = app.Name,
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        return results;
    }

    // ── Publish ───────────────────────────────────────────────────────────────

    public async Task PublishAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string appHostDirectory,
        CancellationToken ct = default) =>
        await publisher.PublishAsync(manifest, config, appHostDirectory, ct);

    // ── Explain ───────────────────────────────────────────────────────────────

    public IReadOnlyList<ServiceExplanation> Explain(
        AspireManifest manifest, EnvironmentConfig config)
    {
        var cfg = GetConfig(config);
        var apps = _generator.Generate(manifest, config, cfg);

        return apps.Select(app => new ServiceExplanation
        {
            Name = app.Name,
            Image = app.Image,
            ImageTag = app.ImageTag,
            Count = app.MaxReplicas,
            Env = app.Env,
            Ports = app.Ports.Select(p => $"{p.ContainerPort}/{p.Protocol}").ToList(),
            Dependencies = app.Dependencies,
            AdditionalInfo = new Dictionary<string, string>
            {
                ["resourceGroup"] = cfg.ResourceGroup,
                ["location"] = cfg.Location,
                ["environment"] = cfg.EnvironmentName,
                ["ingress"] = app.IngressType.ToString().ToLowerInvariant(),
                ["minReplicas"] = app.MinReplicas.ToString(),
                ["maxReplicas"] = app.MaxReplicas.ToString(),
                ["cpu"] = $"{app.Cpu} cores",
                ["memory"] = app.Memory,
                ["type"] = app.IsProjectService ? "project" : "container"
            }
        }).ToList();
    }

    // ── Graph ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<ServiceDependency> GetDependencies(
        AspireManifest manifest, EnvironmentConfig config)
    {
        var cfg = GetConfig(config);
        return _generator.Generate(manifest, config, cfg)
            .SelectMany(app => app.Dependencies.Select(dep =>
                new ServiceDependency { From = dep, To = app.Name }))
            .ToList();
    }

    // ── Diff ──────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var apps = _generator.Generate(manifest, config, cfg);
        var client = new AcaCliClient(cfg, logger);
        var entries = new List<DiffEntry>();

        HashSet<string> running;
        try { running = (await client.GetRunningAppNamesAsync(ct)).ToHashSet(); }
        catch
        {
            return apps
                .Select(a => new DiffEntry { ServiceName = a.Name, Kind = DiffKind.Added })
                .ToList();
        }

        var desired = apps.Select(a => a.Name).ToHashSet();

        foreach (var app in apps)
        {
            if (!running.Contains(app.Name))
            {
                entries.Add(new DiffEntry { ServiceName = app.Name, Kind = DiffKind.Added });
                continue;
            }

            // Compare latest revision against desired image tag
            var revision = await client.GetLatestRevisionAsync(app.Name, ct);
            var kind = revision is null ? DiffKind.Changed : DiffKind.Unchanged;
            entries.Add(new DiffEntry
            {
                ServiceName = app.Name,
                Kind = kind,
                Field = kind == DiffKind.Changed ? "revision" : null
            });
        }

        foreach (var name in running.Except(desired))
        {
	        entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Removed });
        }

        return entries;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AcaTargetConfig GetConfig(EnvironmentConfig config)
    {
        if (config.TargetConfig.TryGetValue("aca", out var raw) && raw is JsonElement el)
        {
	        return el.Deserialize<AcaTargetConfig>(TargetConfigJsonOptions.Default) ?? new AcaTargetConfig();
        }

        return new AcaTargetConfig();
    }
}
