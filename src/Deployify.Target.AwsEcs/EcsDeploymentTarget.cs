using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.SecretBackends;
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
    ILogger<EcsDeploymentTarget> logger) : IDeploymentTarget, ISecretStoreProvider, IRolloutTarget
{
    private readonly EcsTaskDefinitionGenerator _generator = new();

    /// <summary>Process runner for the aws CLI; tests swap in a fake.</summary>
    internal IProcessRunner? Runner { get; init; }

    private EcsCliClient ClientFor(EcsTargetConfig cfg, IEnumerable<EcsService>? services = null)
    {
        var client = new EcsCliClient(cfg, logger, Runner);
        foreach (var svc in services ?? Enumerable.Empty<EcsService>())
        {
            client.ServicePorts[svc.Name] = [.. svc.PortMappings.Select(p => (p.Name, p.ContainerPort))];
        }

        return client;
    }

    // ── Secrets ───────────────────────────────────────────────────────────────

    public IReadOnlyCollection<SecretBackendType> SupportedSecretBackends { get; } = [SecretBackendType.Ssm];

    public ISecretStore CreateSecretStore(EnvironmentConfig config, ILogger storeLogger)
    {
        var cfg = GetConfig(config);
        return new SsmSecretStore(new EcsCliClient(cfg, storeLogger), EcsTaskDefinitionGenerator.GeneratedParametersPath(cfg));
    }

    /// <summary>
    /// Stores a service's secret env vars as SecureStrings. With the ssm backend, unchanged
    /// values aren't rewritten and each parameter's version is recorded in the state file.
    /// </summary>
    internal static async Task SyncSecretsAsync(
        EcsService svc, EnvironmentConfig config, EcsTargetConfig cfg, ISsmParameterClient client, CancellationToken ct)
    {
        foreach (var (key, value) in svc.SecretEnv)
        {
            var name = EcsTaskDefinitionGenerator.SecretParameterName(cfg, svc.Name, key);
            if (!config.Secrets.IsExternal)
            {
                await client.PutSecureStringParameterAsync(name, value.ToString(), ct);
                continue;
            }

            var (version, _) = await SsmSecrets.PutIfChangedAsync(client, name, new SensitiveValue(value.ToString()), ct);
            config.Secrets.References[$"{svc.Name}/{key}"] = new SecretReference
            {
                Backend = SecretBackendType.Ssm,
                Location = name,
                Version = version.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
        }
    }

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
        var client = ClientFor(cfg);
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
        var client = ClientFor(cfg);

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
        var client = ClientFor(cfg);
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
            ToCreate = [.. desired.Except(running)],
            ToUpdate = [.. desired.Intersect(running)],
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
        var client = ClientFor(cfg, services);
        var results = new List<DeploymentResult>();

        foreach (var svc in services)
        {
            var (result, _) = await ApplyOneAsync(svc, config, cfg, client, outputDirectory, cfg.WaitForStable, ct);
            results.Add(result);
        }

        return results;
    }

    /// <summary>Registers the task definition and creates or updates one service.</summary>
    private async Task<(DeploymentResult Result, string? TaskDefinitionArn)> ApplyOneAsync(
        EcsService svc, EnvironmentConfig config, EcsTargetConfig cfg, EcsCliClient client,
        string outputDirectory, bool waitForStable, CancellationToken ct)
    {
        var taskDefPath = Path.Combine(outputDirectory, $"{svc.Name}.taskdef.json");
        if (!File.Exists(taskDefPath))
        {
            return (new DeploymentResult
            {
                ServiceName = svc.Name,
                Success = false,
                Error = $"Task definition not found: {taskDefPath}. Run 'deployify plan' first."
            }, null);
        }

        string? taskDefArn = null;
        try
        {
            await SyncSecretsAsync(svc, config, cfg, client, ct);

            taskDefArn = await client.RegisterTaskDefinitionAsync(taskDefPath, ct);
            logger.LogInformation("Registered task definition: {Arn}", taskDefArn);

            var serviceArn = await client.UpsertServiceAsync(
                svc.Name, taskDefArn, svc.DesiredCount, ct);

            if (waitForStable)
            {
                await client.WaitForStableAsync(svc.Name, ct);
            }

            return (new DeploymentResult
            {
                ServiceName = svc.Name,
                DeploymentId = serviceArn,
                Success = true
            }, taskDefArn);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deploy ECS service '{Name}'.", svc.Name);
            return (new DeploymentResult
            {
                ServiceName = svc.Name,
                Success = false,
                Error = ex.Message
            }, taskDefArn);
        }
    }

    // ── Rollout (health-gated apply) ──────────────────────────────────────────

    public async Task<ServiceSnapshot> CaptureAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
    {
        var status = await ClientFor(GetConfig(config)).DescribeServiceAsync(service, ct);
        if (status is null || status.Status == "INACTIVE" || status.TaskDefinition is null)
        {
            return new ServiceSnapshot { ServiceName = service, Existed = false };
        }

        return new ServiceSnapshot
        {
            ServiceName = service,
            Existed = true,
            Revision = status.TaskDefinition,
            Data = new Dictionary<string, string>
            {
                [DesiredCountKey] = status.DesiredCount.ToString(CultureInfo.InvariantCulture)
            }
        };
    }

    public async Task<AppliedRevision> ApplyServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var services = _generator.Generate(manifest, config, cfg);
        var svc = services.FirstOrDefault(s => s.Name == service);
        if (svc is null)
        {
            return new AppliedRevision { ServiceName = service, Success = false, Error = "not an ECS service in this manifest" };
        }

        // The rollout engine does its own health gate, so don't block on services-stable here.
        var (result, taskDefArn) = await ApplyOneAsync(
            svc, config, cfg, ClientFor(cfg, services), outputDirectory, waitForStable: false, ct);
        return new AppliedRevision
        {
            ServiceName = service,
            Success = result.Success,
            Revision = taskDefArn,
            Error = result.Error
        };
    }

    public async Task<HealthObservation> ObserveAsync(
        EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var status = await ClientFor(cfg).DescribeServiceAsync(service, ct);
        return EcsRolloutHealth.Evaluate(status, revision, cfg.DeploymentCircuitBreaker);
    }

    public async Task RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var client = ClientFor(cfg);

        if (snapshot is { Existed: true, Revision: { } previous })
        {
            var count = snapshot.Data.TryGetValue(DesiredCountKey, out var raw) &&
                        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : cfg.DefaultDesiredCount;
            await client.RestoreServiceAsync(snapshot.ServiceName, previous, count, ct);
            logger.LogInformation("Rolled {Name} back to {Arn}.", snapshot.ServiceName, previous);
            return;
        }

        // A service this rollout created has nothing to go back to: stop its tasks.
        if (await client.DescribeServiceAsync(snapshot.ServiceName, ct) is { Status: not "INACTIVE" })
        {
            await client.ScaleServiceAsync(snapshot.ServiceName, 0, ct);
            logger.LogInformation("Scaled new service {Name} to zero.", snapshot.ServiceName);
        }
    }

    public bool HasNativeRollback(EnvironmentConfig config) => GetConfig(config).DeploymentCircuitBreaker;

    private const string DesiredCountKey = "desiredCount";

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
        var client = ClientFor(cfg);
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
