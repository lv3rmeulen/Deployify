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

namespace Deployify.Target.AwsEcs;

/// <summary>
/// IDeploymentTarget implementation for AWS Elastic Container Service (ECS).
///
/// Output structure:
///   {outputDir}/
///     {service}.taskdef.json    ← ECS task definition (RegisterTaskDefinition shape)
///
/// Apply strategy:
///   1. Register task definition with ECS
///   2. Create or update the ECS service in the cluster
///
/// Requires the AWS CLI to be installed and configured.
/// Credentials are resolved via the standard AWS credential chain
/// (env vars → ~/.aws/credentials → instance profile → etc.).
/// </summary>
public sealed class EcsDeploymentTarget(
    IPublisher publisher,
    ILogger<EcsDeploymentTarget> logger) : IDeploymentTarget
{
    private readonly EcsTaskDefinitionGenerator _generator = new();

    public DeploymentTargetType TargetType => DeploymentTargetType.AwsEcs;
    public string DisplayName => "AWS ECS (Fargate)";

    // ── Ping ──────────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(
        EnvironmentConfig config, CancellationToken ct = default)
    {
        if (!await EcsCliClient.IsAvailableAsync(ct))
        {
            return (false, "AWS CLI ('aws') not found on PATH. Install it from https://aws.amazon.com/cli/");
        }

        var cfg = GetConfig(config);
        var client = new EcsCliClient(cfg, logger);
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
        var services = _generator.Generate(manifest, config, cfg);
        var paths = new List<string>();
        foreach (var svc in services)
        {
            paths.Add(await _generator.WriteTaskDefinitionAsync(svc, config, cfg, outputDirectory, ct));
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
        var client = new EcsCliClient(cfg, logger);

        foreach (var svc in services)
        {
            var path = await _generator.WriteTaskDefinitionAsync(svc, config, cfg, outputDirectory, ct);
            var errors = await client.ValidateTaskDefinitionAsync(path, ct);
            results.Add(new ValidationResult { ArtifactName = path, Errors = errors });
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

        // Write task definitions to disk — apply reads these
        foreach (var svc in services)
        {
            await _generator.WriteTaskDefinitionAsync(svc, config, cfg, outputDirectory, ct);
        }

        var desired = services.Select(s => s.Name).ToHashSet();
        var client = new EcsCliClient(cfg, logger);
        HashSet<string> running;
        try
        {
            running = [.. await client.GetRunningServiceNamesAsync(ct)];
        }
        catch
        {
            running = [];
        }

        return new PlanResult
        {
            ToCreate = [.. desired.Except(running).Select(n => (object)n)],
            ToUpdate = [.. desired.Intersect(running).Select(n => (object)n)],
            ToDelete = [.. running.Except(desired)],
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
        var client = new EcsCliClient(cfg, logger);
        foreach (var svc in services)
        {
            client.ServicePorts[svc.Name] = [.. svc.PortMappings.Select(p => (p.Name, p.ContainerPort))];
        }

        var results = new List<DeploymentResult>();

        foreach (var svc in services)
        {
            var taskDefPath = Path.Combine(outputDirectory, $"{svc.Name}.taskdef.json");
            if (!File.Exists(taskDefPath))
            {
                results.Add(new DeploymentResult
                {
                    ServiceName = svc.Name,
                    Success = false,
                    Error = $"Task definition not found: {taskDefPath}. Run 'deployify plan' first."
                });
                continue;
            }

            try
            {
                foreach (var (key, value) in svc.SecretEnv)
                {
                    await client.PutSecureStringParameterAsync(
                        EcsTaskDefinitionGenerator.SecretParameterName(cfg, svc.Name, key), value.ToString(), ct);
                }

                var taskDefArn = await client.RegisterTaskDefinitionAsync(taskDefPath, ct);
                logger.LogInformation("Registered task definition: {Arn}", taskDefArn);

                var serviceArn = await client.UpsertServiceAsync(
                    svc.Name, taskDefArn, svc.DesiredCount, ct);

                if (cfg.WaitForStable)
                {
                    await client.WaitForStableAsync(svc.Name, ct);
                }

                results.Add(new DeploymentResult
                {
                    ServiceName = svc.Name,
                    DeploymentId = serviceArn,
                    Success = true
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to deploy ECS service '{Name}'.", svc.Name);
                results.Add(new DeploymentResult
                {
                    ServiceName = svc.Name,
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
        CancellationToken ct = default)
        => await publisher.PublishAsync(manifest, config, appHostDirectory, ct);

    // ── Explain ───────────────────────────────────────────────────────────────

    public IReadOnlyList<ServiceExplanation> Explain(
        AspireManifest manifest, EnvironmentConfig config)
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
                Count = svc.DesiredCount,
                Env = ResolvedEnvironment.ForDisplay(svc.Env, svc.SecretEnv),
                Ports = [.. svc.PortMappings.Select(p => $"{p.ContainerPort}/{p.Protocol}")],
                Dependencies = svc.Dependencies,
                AdditionalInfo = new Dictionary<string, string>
                {
                    ["cluster"] = cfg.ClusterName,
                    ["region"] = cfg.Region,
                    ["launchType"] = cfg.LaunchType.ToString(),
                    ["cpu"] = $"{svc.Cpu} units",
                    ["memory"] = $"{svc.Memory} MiB",
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
        return
        [
            .. _generator.Generate(manifest, config, cfg)
                .SelectMany(svc => svc.Dependencies.Select(dep =>
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
        var client = new EcsCliClient(cfg, logger);
        var entries = new List<DiffEntry>();

        HashSet<string> running;
        try
        {
            running = [.. await client.GetRunningServiceNamesAsync(ct)];
        }
        catch
        {
            return
            [
                .. services
                    .Select(s => new DiffEntry { ServiceName = s.Name, Kind = DiffKind.Added })
            ];
        }

        var desired = services.Select(s => s.Name).ToHashSet();

        foreach (var svc in services)
        {
            if (!running.Contains(svc.Name))
            {
                entries.Add(new DiffEntry { ServiceName = svc.Name, Kind = DiffKind.Added });
                continue;
            }

            // Compare running task definition ARN family revision against desired
            var runningArn = await client.GetRunningTaskDefinitionArnAsync(svc.Name, ct);
            var runningFamily = runningArn?.Split('/').LastOrDefault()?.Split(':').FirstOrDefault();

            var kind = runningFamily != svc.Name ? DiffKind.Changed : DiffKind.Unchanged;
            entries.Add(new DiffEntry
            {
                ServiceName = svc.Name,
                Kind = kind,
                Field = kind == DiffKind.Changed ? "taskDefinition" : null
            });
        }

        foreach (var name in running.Except(desired))
        {
            entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Removed });
        }

        return entries;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EcsTargetConfig GetConfig(EnvironmentConfig config)
    {
        if (config.TargetConfig.TryGetValue("ecs", out var raw) && raw is JsonElement el)
        {
            return el.Deserialize<EcsTargetConfig>(TargetConfigJsonOptions.Default) ?? new EcsTargetConfig();
        }

        return new EcsTargetConfig();
    }
}
