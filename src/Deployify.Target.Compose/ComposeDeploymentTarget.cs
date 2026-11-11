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
    ILogger<ComposeDeploymentTarget> logger) : IDeploymentTarget, IRolloutTarget, IResourceLifecycle
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
        var services = await _generator.GenerateAsync(manifest, config, cfg, ct);
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
        var services = await _generator.GenerateAsync(manifest, config, cfg, ct);
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
        var services = await _generator.GenerateAsync(manifest, config, cfg, ct);
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
        var services = await _generator.GenerateAsync(manifest, config, cfg, ct);
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

    // ── Rollout (health-gated apply) ──────────────────────────────────────────

    private const string ConfigFilesKey = "configFiles";

    private static IEnumerable<string>? Profiles(ComposeTargetConfig cfg) => cfg.GenerateProfiles ? new[] { "prod" } : null;

    public async Task<string?> GetUnavailableReasonAsync(EnvironmentConfig config, CancellationToken ct = default)
        => await CliAvailableAsync(ct) ? null : "neither 'docker compose' nor 'docker-compose' is installed";

    /// <summary>
    /// Records the image ID the service's containers run, and the compose files they were
    /// created from (Compose keeps those paths in a container label).
    /// </summary>
    public async Task<ServiceSnapshot> CaptureAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
    {
        var containers = await Runner(GetConfig(config)).GetServiceContainersAsync(service, ct);
        var current = containers.FirstOrDefault(c => c.Status == "running") ?? containers.FirstOrDefault();
        if (current is null)
        {
            return new ServiceSnapshot { ServiceName = service, Existed = false };
        }

        var data = new Dictionary<string, string>();
        if (current.ConfigFiles is { } files)
        {
            data[ConfigFilesKey] = files;
            if (current.Env is { } env && await BackUpEnvironmentAsync(files, service, current.ImageId, env, ct) is { } backup)
            {
                data[EnvBackupKey] = backup;
            }
        }

        return new ServiceSnapshot { ServiceName = service, Existed = true, Revision = current.ImageId, Data = data };
    }

    private const string EnvBackupKey = "envBackup";
    private const int EnvBackupsKept = 5;

    /// <summary>
    /// Saves the container's environment next to the compose files, in
    /// <c>.deployify-rollback/{service}.{image}.json</c>, readable by the owner only like the
    /// generated .env files, since it holds the same secrets. The journal records only the path.
    /// </summary>
    private async Task<string?> BackUpEnvironmentAsync(
        string configFiles, string service, string imageId, IReadOnlyList<string> env, CancellationToken ct)
    {
        try
        {
            var first = configFiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            var dir = Path.Combine(Path.GetDirectoryName(first)!, ".deployify-rollback");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{service}.{ComposeRolloutHealth.ShortHex(imageId)}.json");
            await WriteOwnerOnlyAsync(path, JsonSerializer.Serialize(env), ct);

            // Keep the newest few per service.
            foreach (var old in new DirectoryInfo(dir).GetFiles($"{service}.*.json")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(EnvBackupsKept))
            {
                old.Delete();
            }

            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not save the environment of {Name} ({Message}); a rollback will restore its image only.",
                service, ex.Message);
            return null;
        }
    }

    private static async Task WriteOwnerOnlyAsync(string path, string content, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, content, ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public async Task<AppliedRevision> ApplyServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var mergedPath = Path.Combine(outputDirectory, "compose.yml");
        if (!File.Exists(mergedPath))
        {
            return new AppliedRevision
            {
                ServiceName = service, Success = false,
                Error = $"compose.yml not found at {mergedPath}. Run 'deployify plan' first."
            };
        }

        var runner = Runner(cfg);
        await runner.EnsureNetworkAsync(ct);
        if (cfg.Volumes.Count > 0)
        {
            await runner.EnsureVolumesAsync(cfg.Volumes.Keys, ct);
        }

        var (ok, output) = await runner.UpServiceAsync([mergedPath], service, Profiles(cfg), ct);
        if (!ok)
        {
            return new AppliedRevision { ServiceName = service, Success = false, Error = output.Trim() };
        }

        var containers = await runner.GetServiceContainersAsync(service, ct);
        var container = containers.FirstOrDefault(c => c.Status == "running") ?? containers.FirstOrDefault();
        return container is null
            ? new AppliedRevision { ServiceName = service, Success = false, Error = "no container was created" }
            : new AppliedRevision { ServiceName = service, Success = true, Revision = container.ImageId };
    }

    public async Task<HealthObservation> ObserveAsync(
        EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
        => ComposeRolloutHealth.Evaluate(await Runner(GetConfig(config)).GetServiceContainersAsync(service, ct), revision);

    /// <summary>
    /// Brings the service back up on its previous image ID, through a small override file next to
    /// the compose file that pins the image and never pulls. A service this rollout created has its
    /// containers removed. The old image must still be in the local image store.
    /// </summary>
    public async Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var runner = Runner(cfg);
        var service = snapshot.ServiceName;
        var containers = await runner.GetServiceContainersAsync(service, ct);

        if (snapshot is not { Existed: true, Revision: { } previous })
        {
            if (!snapshot.Existed)
            {
                await runner.RemoveContainersAsync(containers.Select(c => c.Id), ct);
                logger.LogInformation("Removed the containers of new service {Name}.", service);
            }

            return null;
        }

        if (containers.Count > 0 && containers.All(c => c.ImageId == previous))
        {
            return previous; // the rollout never changed the image
        }

        var files = (snapshot.Data.TryGetValue(ConfigFilesKey, out var raw) ? raw : "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (files.Count == 0 || !files.All(File.Exists))
        {
            throw new InvalidOperationException(
                $"The compose files {service} was created from are gone ({(files.Count == 0 ? "not recorded" : string.Join(", ", files))}), " +
                "so it can't be brought back up.");
        }

        IReadOnlyList<string>? environment = null;
        if (snapshot.Data.TryGetValue(EnvBackupKey, out var envBackup))
        {
            if (File.Exists(envBackup))
            {
                environment = JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(envBackup, ct));
            }
            else
            {
                logger.LogWarning("The saved environment of {Name} ({Path}) is gone; restoring its image only.", service, envBackup);
            }
        }

        // Owner-only: with the environment it holds the same secrets as the .env files.
        var overridePath = Path.Combine(Path.GetDirectoryName(files[0])!, $"rollback.{service}.override.yml");
        await WriteOwnerOnlyAsync(overridePath, ComposeRolloutHealth.PinImageOverride(service, previous, environment), ct);

        var (ok, output) = await runner.UpServiceAsync([.. files, overridePath], service, Profiles(cfg), ct);
        if (!ok)
        {
            throw new InvalidOperationException(
                $"Could not bring {service} back up on {ComposeRolloutHealth.Short(previous)}: {output.Trim()}");
        }

        logger.LogInformation("Brought {Name} back up on image {Image}.", service, previous);
        return previous; // image IDs are immutable: wait for this very one
    }

    public Task<bool> HasNativeRollbackAsync(EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>
    /// The rollout brings services up one at a time with --no-deps, so finish with the same
    /// whole-project up as a plain apply, which removes containers of services no longer in
    /// the manifest. Every remaining service is already up to date, so nothing else changes.
    /// </summary>
    public async Task CompleteAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var (ok, output) = await Runner(cfg).UpAsync(
            Path.Combine(outputDirectory, "compose.yml"), Profiles(cfg), forceRecreate: false, ct: ct);
        if (!ok)
        {
            throw new InvalidOperationException(output.Trim());
        }
    }

    // ── Publish ───────────────────────────────────────────────────────────────

    // ── Lifecycle (destroy / prune) ───────────────────────────────────────────

    /// <summary>
    /// The services with containers in this Compose project. The project name is the ownership
    /// marker: Compose labels every container with it. Dapr sidecars (<c>svc-dapr</c>) count as
    /// part of their service.
    /// </summary>
    public async Task<IReadOnlyList<ManagedService>> ListManagedAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        if (!await CliAvailableAsync(ct))
        {
            throw new InvalidOperationException("docker compose was not found on PATH.");
        }

        var services = await Runner(cfg).GetProjectServicesAsync(ct);
        var set = services.ToHashSet(StringComparer.Ordinal);
        return
        [
            .. services
                .Where(s => !(s.EndsWith("-dapr", StringComparison.Ordinal) && set.Contains(s[..^5])))
                .Select(s => new ManagedService(s))
        ];
    }

    /// <summary>Stops and removes the service's containers, and its Dapr sidecar's.</summary>
    public async Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default)
    {
        var runner = Runner(GetConfig(config));
        foreach (var name in new[] { service, service + "-dapr" })
        {
            var containers = await runner.GetServiceContainersAsync(name, ct);
            await runner.RemoveContainersAsync(containers.Select(c => c.Id), ct);
        }
    }

    /// <summary>The project network, and with --volumes the project's and the declared named volumes.</summary>
    public Task<IReadOnlyList<string>> DestroyEnvironmentAsync(EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        return Runner(cfg).RemoveProjectResourcesAsync(cfg.Volumes.Keys, options.RemoveVolumes, ct);
    }

    public Task<IReadOnlyList<string>> DescribeKeptAsync(EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
    {
        var kept = new List<string> { "images in the local image store" };
        if (!options.RemoveVolumes)
        {
            kept.Add("volumes (pass --volumes to remove them)");
        }

        return Task.FromResult<IReadOnlyList<string>>(kept);
    }

    public async Task PublishAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string appHostDirectory,
        CancellationToken ct = default)
        => await publisher.PublishAsync(manifest, config, appHostDirectory, ct);

    // ── Explain ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var services = await _generator.GenerateAsync(manifest, config, cfg, ct);

        return [
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

    public async Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var services = await _generator.GenerateAsync(manifest, config, cfg, ct);

        return [
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
        var services = await _generator.GenerateAsync(manifest, config, cfg, ct);
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
