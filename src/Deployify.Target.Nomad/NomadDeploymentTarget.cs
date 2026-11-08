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
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Nomad;

/// <summary>
/// IDeploymentTarget implementation for HashiCorp Nomad.
/// Config is read from environmentConfig.TargetConfig["nomad"].
/// </summary>
public sealed class NomadDeploymentTarget(
    JobGenerator jobGenerator,
    IPublisher publisher,
    ILogger<NomadDeploymentTarget> logger,
    ILoggerFactory loggerFactory) : IDeploymentTarget
{
    public DeploymentTargetType TargetType => DeploymentTargetType.Nomad;
    public string DisplayName => "HashiCorp Nomad";

    public async Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        using var client = BuildClient(cfg);
        var ok = await client.PingAsync(ct);
        return ok ? (true, null) : (false, $"Cannot reach Nomad at {cfg.Address}. Is it running?");
    }

    public async Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var jobs = jobGenerator.Generate(manifest, config);
        var paths = new List<string>();
        foreach (var job in jobs)
        {
            paths.Add(await jobGenerator.WriteHclAsync(job, config, outputDirectory, ct));
        }

        return paths;
    }

    public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var jobs = jobGenerator.Generate(manifest, config);
        var results = new List<ValidationResult>();
        using var client = BuildClient(cfg);
        foreach (var job in jobs)
        {
            var hclPath = await jobGenerator.WriteHclAsync(job, config, outputDirectory, ct);
            var errors = await client.ValidateJobAsync(hclPath, ct);
            results.Add(new ValidationResult { ArtifactName = hclPath, Errors = errors });
        }

        return results;
    }

    public async Task<PlanResult> PlanAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        return PlanResult.FromDiff(await CompareWithClusterAsync(manifest, config, cfg, outputDirectory, ct));
    }

    /// <summary>
    /// Writes every job's HCL (apply reads these) and asks Nomad what submitting each would
    /// change; plan and diff both use this, so they agree. A job that isn't running is Added;
    /// one Nomad's plan API reports no change for is Unchanged, otherwise Changed. When the
    /// plan call itself fails the job is Unknown, with the reason, rather than guessed. An
    /// unreachable Nomad is treated as empty, with a warning.
    /// </summary>
    private async Task<List<DiffEntry>> CompareWithClusterAsync(
        AspireManifest manifest, EnvironmentConfig config, NomadTargetConfig cfg, string outputDirectory, CancellationToken ct)
    {
        var jobs = jobGenerator.Generate(manifest, config);
        var paths = new Dictionary<string, string>();
        foreach (var job in jobs)
        {
            paths[job.Name] = await jobGenerator.WriteHclAsync(job, config, outputDirectory, ct);
        }

        using var client = BuildClient(cfg);
        HashSet<string> running, managed;
        try
        {
            running = [.. await client.GetRunningJobNamesAsync(ct: ct)];
            managed = [.. await client.GetRunningJobNamesAsync(managedOnly: true, ct)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not reach Nomad at {Address} — assuming all services are new. ({Message})",
                cfg.Address, ex.Message);
            running = [];
            managed = [];
        }

        var entries = new List<DiffEntry>();
        foreach (var job in jobs)
        {
            if (!running.Contains(job.Name))
            {
                entries.Add(new DiffEntry { ServiceName = job.Name, Kind = DiffKind.Added });
                continue;
            }

            var (diffType, error) = await TryPlanAsync(client, paths[job.Name], ct);
            entries.Add(diffType switch
            {
                null => new DiffEntry { ServiceName = job.Name, Kind = DiffKind.Unknown, Reason = error },
                "None" => new DiffEntry { ServiceName = job.Name, Kind = DiffKind.Unchanged },
                _ => new DiffEntry { ServiceName = job.Name, Kind = DiffKind.Changed, Field = "job spec" }
            });
        }

        // Only jobs Deployify created are candidates for deletion.
        foreach (var name in managed.Except(jobs.Select(j => j.Name)))
        {
            entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Removed });
        }

        return entries;
    }

    /// <summary>Nomad's diff type ("None", "Added", "Edited"…), or null and the reason when the plan call fails.</summary>
    private async Task<(string? DiffType, string? Error)> TryPlanAsync(NomadClient client, string hclPath, CancellationToken ct)
    {
        try
        {
            return (await client.PlanJobAsync(hclPath, ct), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not plan {Path}: {Message}", Path.GetFileName(hclPath), ex.Message);
            return (null, $"Nomad could not plan the job: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var jobs = jobGenerator.Generate(manifest, config);
        var results = new List<DeploymentResult>();
        using var client = BuildClient(cfg);

        foreach (var job in jobs)
        {
            // Read the file plan already wrote — do not regenerate
            var hclPath = Path.Combine(outputDirectory, $"{job.Name}.job.hcl");
            if (!File.Exists(hclPath))
            {
                results.Add(new DeploymentResult
                {
                    ServiceName = job.Name, Success = false,
                    Error = $"Artifact not found: {hclPath}. Run 'deployify plan' first."
                });
                continue;
            }

            try
            {
                // Secrets first: the job's template block reads them when the task starts.
                if (job.SecretEnv.Count > 0)
                {
                    await client.PutVariableAsync(NomadVariables.JobPath(job.Name),
                        job.SecretEnv.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()), ct);
                }

                var evalId = await client.SubmitJobAsync(hclPath, ct);
                results.Add(new DeploymentResult { ServiceName = job.Name, DeploymentId = evalId, Success = true });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to submit job '{Name}'.", job.Name);
                results.Add(new DeploymentResult { ServiceName = job.Name, Success = false, Error = ex.Message });
            }
        }

        return results;
    }

    public async Task PublishAsync(
        AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default)
        => await publisher.PublishAsync(manifest, config, appHostDirectory, ct);

    public IReadOnlyList<ServiceExplanation> Explain(AspireManifest manifest, EnvironmentConfig config)
    {
        var cfg = GetConfig(config);
        var jobs = jobGenerator.Generate(manifest, config);
        return
        [
            .. jobs.Select(j => new ServiceExplanation
            {
                Name = j.Name,
                Image = j.Image,
                ImageTag = j.ImageTag ?? config.ImageTag,
                Count = j.Count,
                Env = ResolvedEnvironment.ForDisplay(j.Env, j.SecretEnv),
                Ports = [.. j.Ports.Select(p => p.To.HasValue ? $"{p.Label}:{p.To}" : p.Label)],
                Dependencies = j.Dependencies,
                AdditionalInfo = new Dictionary<string, string> { ["namespace"] = cfg.Namespace, ["datacenters"] = string.Join(", ", cfg.Datacenters), ["mode"] = cfg.DeploymentMode.ToString() }
            })
        ];
    }

    public IReadOnlyList<ServiceDependency> GetDependencies(AspireManifest manifest, EnvironmentConfig config)
        =>
        [
            .. jobGenerator.Generate(manifest, config)
                .SelectMany(j => j.Dependencies.Select(dep => new ServiceDependency { From = dep, To = j.Name }))
        ];

    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
        => await CompareWithClusterAsync(manifest, config, GetConfig(config), outputDirectory, ct);

    private static NomadTargetConfig GetConfig(EnvironmentConfig config)
    {
        if (config.TargetConfig.TryGetValue("nomad", out var raw) && raw is JsonElement el)
        {
            return el.Deserialize<NomadTargetConfig>(TargetConfigJsonOptions.Default) ?? new NomadTargetConfig();
        }

        return new NomadTargetConfig();
    }

    private NomadClient BuildClient(NomadTargetConfig cfg)
        => new(cfg.Address, Secrets.Resolve(cfg.Token, "NOMAD_TOKEN"),
            loggerFactory.CreateLogger<NomadClient>(), cfg.Namespace,
            Secrets.Resolve(cfg.RegistryPassword, "DEPLOYIFY_REGISTRY_PASSWORD"));
}
