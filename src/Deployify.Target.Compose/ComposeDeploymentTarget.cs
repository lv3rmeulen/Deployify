using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
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
    private readonly ComposeFileGenerator _generator = new();

    /// <summary>Starts docker; replaceable in tests.</summary>
    internal IProcessRunner Processes { get; init; } = ProcessRunner.Default;

    private Task<bool> CliAvailableAsync(CancellationToken ct) => ComposeRunner.IsAvailableAsync(Processes, ct);

    private ComposeRunner Runner(ComposeTargetConfig cfg) => new(cfg, logger, Processes);

    public DeploymentTargetType TargetType => DeploymentTargetType.Compose;
    public string DisplayName => "Docker Compose";

    // ── Ping ─────────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(
        EnvironmentConfig config, CancellationToken ct = default)
    {
        if (!await CliAvailableAsync(ct))
        {
            return (false, "Neither 'docker compose' nor 'docker-compose' found on PATH.");
        }

        var cfg = GetConfig(config);
        var runner = Runner(cfg);
        return await runner.PingAsync(ct);
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

        var cliAvailable = await CliAvailableAsync(ct);
        var runner = cliAvailable ? Runner(cfg) : null;

        // Validate each per-service file
        foreach (var svc in services)
        {
            var filePath = await _generator.WriteServiceFileAsync(svc, outputDirectory, cfg, ct);

            if (runner is not null)
            {
                var (ok, output) = await runner.ValidateAsync(filePath, ct);
                results.Add(new ValidationResult
                {
                    ArtifactName = filePath,
                    Errors = ok ? [] : [output]
                });
            }
            else
            {
                // No CLI — just confirm the file was written
                results.Add(new ValidationResult { ArtifactName = filePath, Errors = [] });
            }
        }

        // Also validate the merged file
        if (cfg.GenerateMergedFile)
        {
            var mergedPath = await _generator.WriteMergedFileAsync(services, outputDirectory, cfg, ct);
            if (runner is not null)
            {
                var (ok, output) = await runner.ValidateAsync(mergedPath, ct);
                results.Add(new ValidationResult
                {
                    ArtifactName = mergedPath,
                    Errors = ok ? [] : [output]
                });
            }
            else
            {
                results.Add(new ValidationResult { ArtifactName = mergedPath, Errors = [] });
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
        return PlanResult.FromDiff(await CompareWithRunningAsync(services, cfg, outputDirectory, ct));
    }

    /// <summary>
    /// Writes the compose files (apply uses them) and compares them with what's running; plan
    /// and diff both use this, so they agree. A service that isn't running is Added. For a
    /// running one, <c>up --dry-run</c> (Compose 2.18+) says whether it would be recreated;
    /// without dry-run support it is Unknown rather than guessed. Without a compose CLI the
    /// running set can't be read, and all services are treated as new, with a warning.
    /// </summary>
    private async Task<List<DiffEntry>> CompareWithRunningAsync(
        IReadOnlyList<ComposeService> services, ComposeTargetConfig cfg, string outputDirectory, CancellationToken ct)
    {
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
        string? dryRun = null;
        var mergedPath = Path.Combine(outputDirectory, "compose.yml");

        if (!await CliAvailableAsync(ct))
        {
            logger.LogWarning("docker compose not found — assuming all services are new.");
        }
        else if (File.Exists(mergedPath))
        {
            var runner = Runner(cfg);
            running = await runner.GetRunningServicesAsync(mergedPath, ct);
            // Same profiles as apply, or services in the "prod" profile would be left out.
            dryRun = await runner.DryRunUpAsync(mergedPath, cfg.GenerateProfiles ? ["prod"] : null, ct);
        }

        // Dapr sidecars run as "<service>-dapr"; they belong to their service.
        var runningSet = running
            .Where(r => !(r.EndsWith("-dapr", StringComparison.Ordinal) && desired.Contains(r[..^5])))
            .ToHashSet();

        var entries = new List<DiffEntry>();
        foreach (var name in desired)
        {
            if (!runningSet.Contains(name))
            {
                entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Added });
            }
            else if (dryRun is null)
            {
                entries.Add(new DiffEntry
                {
                    ServiceName = name, Kind = DiffKind.Unknown,
                    Reason = "this docker compose has no 'up --dry-run' (needs Compose 2.18+)"
                });
            }
            else if (WouldRecreate(dryRun, cfg.ProjectName, name))
            {
                entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Changed, Field = "config" });
            }
            else
            {
                entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Unchanged });
            }
        }

        foreach (var name in runningSet.Except(desired))
        {
            entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Removed });
        }

        return entries;
    }

    /// <summary>
    /// <c>up --dry-run</c> names every container it would create or recreate, e.g.
    /// <c>DRY-RUN MODE -  Container shop-api-1  Recreate</c>.
    /// </summary>
    internal static bool WouldRecreate(string dryRunOutput, string projectName, string serviceName)
        => System.Text.RegularExpressions.Regex.IsMatch(dryRunOutput,
            $@"\b{System.Text.RegularExpressions.Regex.Escape(projectName)}[-_]{System.Text.RegularExpressions.Regex.Escape(serviceName)}[-_]\d+\s+(Recreate|Create)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
            return
            [
                .. services.Select(s => new DeploymentResult { ServiceName = s.Name, Success = false, Error = $"compose.yml not found at {mergedPath}. Run 'deployify plan' first." })
            ];
        }

        var runner = Runner(cfg);

        // Ensure infrastructure exists
        await runner.EnsureNetworkAsync(ct);
        if (cfg.Volumes.Count > 0)
        {
            await runner.EnsureVolumesAsync(cfg.Volumes.Keys, ct);
        }

        // Bring everything up using the file plan already wrote
        var profiles = cfg.GenerateProfiles ? new[] { "prod" } : null;
        var (ok, output) = await runner.UpAsync(mergedPath, profiles, forceRecreate: false, ct: ct);

        if (!ok)
        {
            logger.LogError("docker compose up failed: {Output}", output);
            return
            [
                .. services.Select(s => new DeploymentResult { ServiceName = s.Name, Success = false, Error = output.Trim() })
            ];
        }

        logger.LogInformation("docker compose up succeeded:\n{Output}", output);

        var nowRunning = (await runner.GetRunningServicesAsync(mergedPath, ct)).ToHashSet();

        return
        [
            .. services.Select(s => new DeploymentResult { ServiceName = s.Name, DeploymentId = $"{cfg.ProjectName}/{s.Name}", Success = nowRunning.Contains(s.Name), Error = nowRunning.Contains(s.Name) ? null : "Service did not start — check logs." })
        ];
    }

    // ── Publish ───────────────────────────────────────────────────────────────

    public async Task PublishAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string appHostDirectory,
        CancellationToken ct = default)
        => await publisher.PublishAsync(manifest, config, appHostDirectory, ct);

    // ── Explain ───────────────────────────────────────────────────────────────

    public IReadOnlyList<ServiceExplanation> Explain(AspireManifest manifest, EnvironmentConfig config)
    {
        var cfg = GetConfig(config);
        var services = _generator.Generate(manifest, config, cfg);

        return
        [
            .. services.Select(svc => new ServiceExplanation
            {
                Name = svc.Name,
                Image = svc.Image,
                ImageTag = svc.ImageTag,
                Count = svc.Replicas,
                Env = ResolvedEnvironment.ForDisplay(svc.Env, svc.SecretEnv),
                Ports = [.. svc.Ports.Select(p => $"{p.HostPort}→{p.ContainerPort}/{p.Protocol}")],
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
            })
        ];
    }

    // ── Graph ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<ServiceDependency> GetDependencies(
        AspireManifest manifest, EnvironmentConfig config)
    {
        var cfg = GetConfig(config);
        var services = _generator.Generate(manifest, config, cfg);

        return
        [
            .. services
                .SelectMany(svc => svc.DependsOn.Select(dep =>
                    new ServiceDependency { From = dep, To = svc.Name }))
        ];
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
        return await CompareWithRunningAsync(services, cfg, outputDirectory, ct);
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
