using System.Text.Json;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Compose;

/// <summary>
/// IDeploymentTarget implementation for Docker Compose.
///
/// Output structure:
///   {outputDir}/
///     compose.yml               ← merged file (all services, applied at once)
///     {service}/
///       compose.yml             ← per-service file
///       .env                    ← env vars (if GenerateEnvFile = true)
///
/// Apply strategy:
///   1. Write per-service compose files
///   2. Merge into a single compose.yml
///   3. Ensure shared network + declared volumes exist
///   4. docker compose up -d --remove-orphans
///
/// CLI resolution: "docker compose" (plugin) → "docker-compose" (standalone).
/// </summary>
public sealed class ComposeDeploymentTarget(
    IPublisher publisher,
    ILogger<ComposeDeploymentTarget> logger) : IDeploymentTarget
{
    private readonly ComposeFileGenerator _generator = new ComposeFileGenerator();

    public DeploymentTargetType TargetType => DeploymentTargetType.Compose;
    public string DisplayName => "Docker Compose";

    // ── Ping ─────────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(
        EnvironmentConfig config, CancellationToken ct = default)
    {
        if (!await ComposeRunner.IsAvailableAsync())
        {
	        return (false, "Neither 'docker compose' nor 'docker-compose' found on PATH.");
        }

        var cfg = GetConfig(config);
        var runner = new ComposeRunner(cfg, logger);
        return await runner.PingAsync();
    }

    // ── Generate ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var services = _generator.Generate(manifest, config, cfg);
        var paths = new List<string>();

        foreach (var svc in services)
        {
	        paths.Add(await _generator.WriteServiceFileAsync(svc, outputDirectory, cfg, ct));
        }

        if (cfg.GenerateMergedFile)
        {
	        paths.Add(await _generator.WriteMergedFileAsync(services, outputDirectory, cfg, ct));
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
        var services = _generator.Generate(manifest, config, cfg);
        var results = new List<ValidationResult>();

        var cliAvailable = await ComposeRunner.IsAvailableAsync();
        var runner = cliAvailable ? new ComposeRunner(cfg, logger) : null;

        // Validate each per-service file
        foreach (var svc in services)
        {
            var filePath = await _generator.WriteServiceFileAsync(svc, outputDirectory, cfg, ct);

            if (runner is not null)
            {
                var (ok, output) = await runner.ValidateAsync(filePath);
                results.Add(new ValidationResult
                {
                    ArtefactName = filePath,
                    Errors = ok ? [] : [output]
                });
            }
            else
            {
                // No CLI — just confirm the file was written
                results.Add(new ValidationResult { ArtefactName = filePath, Errors = [] });
            }
        }

        // Also validate the merged file
        if (cfg.GenerateMergedFile)
        {
            var mergedPath = await _generator.WriteMergedFileAsync(services, outputDirectory, cfg, ct);
            if (runner is not null)
            {
                var (ok, output) = await runner.ValidateAsync(mergedPath);
                results.Add(new ValidationResult
                {
                    ArtefactName = mergedPath,
                    Errors = ok ? [] : [output]
                });
            }
            else
            {
                results.Add(new ValidationResult { ArtefactName = mergedPath, Errors = [] });
                logger.LogWarning("docker compose not found — skipping config validation.");
            }
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
        var services = _generator.Generate(manifest, config, cfg);

        // Generate all compose files to disk — apply will use these
        foreach (var svc in services)
        {
	        await _generator.WriteServiceFileAsync(svc, outputDirectory, cfg, ct);
        }

        if (cfg.GenerateMergedFile)
        {
	        await _generator.WriteMergedFileAsync(services, outputDirectory, cfg, ct);
        }

        var desired = services.Select(s => s.Name).ToHashSet();
        IReadOnlyList<string> running = [];

        if (await ComposeRunner.IsAvailableAsync())
        {
            var runner = new ComposeRunner(cfg, logger);
            var mergedPath = Path.Combine(outputDirectory, "compose.yml");
            if (File.Exists(mergedPath))
            {
	            running = await runner.GetRunningServicesAsync(mergedPath);
            }
        }

        var runningSet = running.ToHashSet();

        return new PlanResult
        {
            ToCreate = desired.Except(runningSet).Select(n => (object)n).ToList(),
            ToUpdate = desired.Intersect(runningSet).Select(n => (object)n).ToList(),
            ToDelete = runningSet.Except(desired).ToList(),
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
        var services = _generator.Generate(manifest, config, cfg);
        var mergedPath = Path.Combine(outputDirectory, "compose.yml");

        if (!File.Exists(mergedPath))
        {
            return services.Select(s => new DeploymentResult
            {
                ServiceName = s.Name,
                Success = false,
                Error = $"compose.yml not found at {mergedPath}. Run 'deployify plan' first."
            }).ToList();
        }

        var runner = new ComposeRunner(cfg, logger);

        // Ensure infrastructure exists
        await runner.EnsureNetworkAsync();
        if (cfg.Volumes.Count > 0)
        {
	        await runner.EnsureVolumesAsync(cfg.Volumes.Keys);
        }

        // Bring everything up using the file plan already wrote
        var profiles = cfg.GenerateProfiles ? new[] { "prod" } : null;
        var (ok, output) = await runner.UpAsync(mergedPath, profiles, forceRecreate: false);

        if (!ok)
        {
            logger.LogError("docker compose up failed: {Output}", output);
            return services.Select(s => new DeploymentResult
            {
                ServiceName = s.Name,
                Success = false,
                Error = output.Trim()
            }).ToList();
        }

        logger.LogInformation("docker compose up succeeded:\n{Output}", output);

        var nowRunning = (await runner.GetRunningServicesAsync(mergedPath)).ToHashSet();

        return services.Select(s => new DeploymentResult
        {
            ServiceName = s.Name,
            DeploymentId = $"{cfg.ProjectName}/{s.Name}",
            Success = nowRunning.Contains(s.Name),
            Error = nowRunning.Contains(s.Name) ? null : "Service did not start — check logs."
        }).ToList();
    }

    // ── Publish ───────────────────────────────────────────────────────────────

    public async Task PublishAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string appHostDirectory,
        CancellationToken ct = default) =>
        await publisher.PublishAsync(manifest, config, appHostDirectory, ct);

    // ── Explain ───────────────────────────────────────────────────────────────

    public IReadOnlyList<ServiceExplanation> Explain(AspireManifest manifest, EnvironmentConfig config)
    {
        var cfg = GetConfig(config);
        var services = _generator.Generate(manifest, config, cfg);

        return services.Select(svc => new ServiceExplanation
        {
            Name = svc.Name,
            Image = svc.Image,
            ImageTag = svc.ImageTag,
            Count = svc.Replicas,
            Env = svc.Env,
            Ports = svc.Ports.Select(p => $"{p.HostPort}→{p.ContainerPort}/{p.Protocol}").ToList(),
            Dependencies = svc.DependsOn,
            AdditionalInfo = new Dictionary<string, string>
            {
                ["restart"] = svc.RestartPolicy,
                ["profiles"] = svc.Profiles.Count > 0 ? string.Join(", ", svc.Profiles) : "none",
                ["volumes"] = svc.NamedVolumes.Count + svc.BindMounts.Count > 0
                                  ? $"{svc.NamedVolumes.Count} named, {svc.BindMounts.Count} bind"
                                  : "none",
                ["network"] = cfg.ProjectName,
                ["type"] = svc.IsProjectService ? "project" : "container"
            }
        }).ToList();
    }

    // ── Graph ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<ServiceDependency> GetDependencies(
        AspireManifest manifest, EnvironmentConfig config)
    {
        var cfg = GetConfig(config);
        var services = _generator.Generate(manifest, config, cfg);

        return services
            .SelectMany(svc => svc.DependsOn.Select(dep =>
                new ServiceDependency { From = dep, To = svc.Name }))
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
        var services = _generator.Generate(manifest, config, cfg);
        var entries = new List<DiffEntry>();

        if (!await ComposeRunner.IsAvailableAsync())
        {
            // No CLI — report all services as unknown
            return services.Select(s => new DiffEntry
            {
                ServiceName = s.Name,
                Kind = DiffKind.Unchanged,
                Field = "compose CLI not available — cannot diff live state"
            }).ToList();
        }

        var runner = new ComposeRunner(cfg, logger);
        var mergedPath = Path.Combine(outputDirectory, "compose.yml");

        // Write files so we have something to compare
        foreach (var svc in services)
        {
	        await _generator.WriteServiceFileAsync(svc, outputDirectory, cfg, ct);
        }

        await _generator.WriteMergedFileAsync(services, outputDirectory, cfg, ct);

        IReadOnlyList<string> running = [];
        if (File.Exists(mergedPath))
        {
            try { running = await runner.GetRunningServicesAsync(mergedPath); }
            catch { running = []; }
        }

        var runningSet = running.ToHashSet();
        var desiredSet = services.Select(s => s.Name).ToHashSet();

        // Try docker compose up --dry-run for rich diff (Compose v2.18+)
        var dryRunOutput = await runner.DryRunUpAsync(mergedPath,
            cfg.GenerateProfiles ? new[] { "prod" } : null);

        if (dryRunOutput is not null)
        {
            // Parse dry-run output for changed/unchanged markers
            var changedServices = dryRunOutput
                .Split('\n')
                .Where(l => l.Contains("Recreate") || l.Contains("Create") || l.Contains("Update"))
                .Select(l => l.Trim().Split(' ')[0])
                .Where(desiredSet.Contains)
                .ToHashSet();

            foreach (var svc in services)
            {
                var kind = !runningSet.Contains(svc.Name) ? DiffKind.Added
                         : changedServices.Contains(svc.Name) ? DiffKind.Changed
                         : DiffKind.Unchanged;

                entries.Add(new DiffEntry
                {
                    ServiceName = svc.Name,
                    Kind = kind,
                    Field = kind == DiffKind.Changed ? "config" : null
                });
            }
        }
        else
        {
            // Fallback: name-only diff
            foreach (var svc in services)
            {
	            entries.Add(new DiffEntry
	            {
		            ServiceName = svc.Name,
		            Kind = runningSet.Contains(svc.Name) ? DiffKind.Unchanged : DiffKind.Added
	            });
            }
        }

        // Services running but not in manifest
        foreach (var name in runningSet.Except(desiredSet))
        {
	        entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Removed });
        }

        return entries;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ComposeTargetConfig GetConfig(EnvironmentConfig config)
    {
        if (config.TargetConfig.TryGetValue("compose", out var raw) && raw is JsonElement el)
        {
	        return el.Deserialize<ComposeTargetConfig>(TargetConfigJsonOptions.Default) ?? new ComposeTargetConfig();
        }

        return new ComposeTargetConfig();
    }
}
