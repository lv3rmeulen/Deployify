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
using Deployify.Target.Nomad;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.NomadConsul;

/// <summary>
/// Deployment target for Nomad jobs that use Consul Connect service mesh.
///
/// Each Aspire service is rendered as a Nomad job with:
///   - <c>network { mode = "bridge" }</c>
///   - A <c>service { connect { sidecar_service { upstreams { ... } } } }</c> block
///     that registers the service in the Consul catalog and wires upstream
///     dependencies through the Envoy sidecar proxy
///   - Upstream loopback ports injected as env vars so applications can
///     discover peers at <c>localhost:&lt;port&gt;</c>
///
/// Plan pings both Nomad and Consul to check connectivity.
/// Apply submits jobs to Nomad; Consul registration happens automatically
/// through Nomad's built-in Consul integration.
/// </summary>
public sealed class NomadConsulDeploymentTarget(
    IPublisher publisher,
    ILogger<NomadConsulDeploymentTarget> logger,
    ILoggerFactory loggerFactory) : IDeploymentTarget, IRolloutTarget, IResourceLifecycle
{
    private readonly NomadRollout _rollout = new(logger);

    /// <summary>Per service: whether the job registers a Consul health check.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _consulChecked = new();

    private readonly Lazy<ConsulJobGenerator> _generator = new(
        () => new ConsulJobGenerator(loggerFactory.CreateLogger<ConsulJobGenerator>()));

    private ConsulJobGenerator Generator => _generator.Value;

    public DeploymentTargetType TargetType => DeploymentTargetType.NomadConsul;
    public string DisplayName => "HashiCorp Nomad + Consul Connect";

    // ── Ping ─────────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(
        EnvironmentConfig config, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);

        // Check Nomad
        using var nomadClient = BuildNomadClient(cfg);
        if (!await nomadClient.PingAsync(ct))
        {
            return (false, $"Cannot reach Nomad at {cfg.NomadAddress}.");
        }

        // Check Consul
        using var consulClient = BuildConsulClient(cfg);
        if (!await consulClient.PingAsync(ct))
        {
            return (false, $"Cannot reach Consul at {cfg.ConsulAddress}.");
        }

        return (true, null);
    }

    // ── Generate ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var jobs = Generator.Generate(manifest, config, cfg);
        var paths = new List<string>();
        foreach (var job in jobs)
        {
            paths.Add(await Generator.WriteHclAsync(job, config, cfg, outputDirectory, ct));
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
        var jobs = Generator.Generate(manifest, config, cfg);
        var results = new List<ValidationResult>();
        using var nomadClient = BuildNomadClient(cfg);

        foreach (var job in jobs)
        {
            var hclPath = await Generator.WriteHclAsync(job, config, cfg, outputDirectory, ct);
            IReadOnlyList<string> errors;
            try
            {
                errors = await nomadClient.ValidateJobAsync(hclPath, ct);
            }
            catch
            {
                errors = [];
            } // Nomad unreachable — skip live validation

            results.Add(new ValidationResult { ArtifactName = hclPath, Errors = errors });
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
        AspireManifest manifest, EnvironmentConfig config, NomadConsulTargetConfig cfg, string outputDirectory, CancellationToken ct)
    {
        var jobs = Generator.Generate(manifest, config, cfg);
        var paths = new Dictionary<string, string>();
        foreach (var job in jobs)
        {
            paths[job.Name] = await Generator.WriteHclAsync(job, config, cfg, outputDirectory, ct);
        }

        using var client = BuildNomadClient(cfg);
        HashSet<string> running, managed;
        try
        {
            running = [.. await client.GetRunningJobNamesAsync(ct: ct)];
            managed = [.. await client.GetRunningJobNamesAsync(managedOnly: true, ct)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not reach Nomad at {Address} — assuming all services are new. ({Message})",
                cfg.NomadAddress, ex.Message);
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

    // ── Apply ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var jobs = Generator.Generate(manifest, config, cfg);
        var results = new List<DeploymentResult>();
        using var nomadClient = BuildNomadClient(cfg);

        foreach (var job in jobs)
        {
            results.Add(await ApplyOneAsync(nomadClient, job, outputDirectory, ct));
        }

        return results;
    }

    private async Task<DeploymentResult> ApplyOneAsync(
        NomadClient nomadClient, ConsulJob job, string outputDirectory, CancellationToken ct)
    {
        var hclPath = Path.Combine(outputDirectory, $"{job.Name}.job.hcl");
        if (!File.Exists(hclPath))
        {
            return new DeploymentResult
            {
                ServiceName = job.Name,
                Success = false,
                Error = $"Artifact not found: {hclPath}. Run 'deployify plan' first."
            };
        }

        try
        {
            // Secrets first: the job's template block reads them when the task starts.
            if (job.SecretEnv.Count > 0)
            {
                await nomadClient.PutVariableAsync(NomadVariables.JobPath(job.Name),
                    job.SecretEnv.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()), ct);
            }

            var evalId = await nomadClient.SubmitJobAsync(hclPath, ct);
            logger.LogInformation(
                "Submitted Consul Connect job '{Name}', evalId={EvalId}", job.Name, evalId);
            return new DeploymentResult
            {
                ServiceName = job.Name,
                DeploymentId = evalId,
                Success = true
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit job '{Name}'.", job.Name);
            return new DeploymentResult
            {
                ServiceName = job.Name,
                Success = false,
                Error = ex.Message
            };
        }
    }

    // ── Rollout (health-gated apply) ──────────────────────────────────────────

    public async Task<ServiceSnapshot> CaptureAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        using var client = BuildNomadClient(cfg);
        return await _rollout.CaptureAsync(client, cfg.NomadNamespace, service, ct);
    }

    public async Task<AppliedRevision> ApplyServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var job = Generator.Generate(manifest, config, cfg).FirstOrDefault(j => j.Name == service);
        if (job is null)
        {
            return new AppliedRevision { ServiceName = service, Success = false, Error = "not a Nomad job in this manifest" };
        }

        // Remembered for the health gate: only services that register a Consul check are gated on it.
        _consulChecked[service] = cfg.RegisterServices && job.Ports.Count > 0 &&
                                  (!job.Ports[0].IsHttp || !cfg.HealthCheckType.Equals("none", StringComparison.OrdinalIgnoreCase));

        using var client = BuildNomadClient(cfg);
        var result = await ApplyOneAsync(client, job, outputDirectory, ct);
        if (!result.Success)
        {
            return new AppliedRevision { ServiceName = service, Success = false, Error = result.Error };
        }

        return new AppliedRevision
        {
            ServiceName = service,
            Success = true,
            Revision = await NomadRollout.CurrentVersionAsync(client, service, ct)
        };
    }

    /// <summary>
    /// Nomad's deployment status first; once Nomad calls the version healthy, a service that
    /// registers a Consul check must also have a passing instance in Consul.
    /// </summary>
    public async Task<HealthObservation> ObserveAsync(
        EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        using var client = BuildNomadClient(cfg);
        var nomad = await NomadRollout.ObserveAsync(client, service, revision, cfg.AutoRevert, ct);
        if (nomad.State != HealthState.Healthy || !_consulChecked.TryGetValue(service, out var check) || !check)
        {
            return nomad;
        }

        using var consul = BuildConsulClient(cfg);
        return await consul.IsServiceHealthyAsync(service, ct)
            ? nomad
            : HealthObservation.Progressing($"{nomad.Detail}; waiting for a passing Consul check");
    }

    public async Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        using var client = BuildNomadClient(cfg);
        return await _rollout.RestoreAsync(client, cfg.NomadNamespace, snapshot, ct);
    }

    public bool HasNativeRollback(EnvironmentConfig config) => GetConfig(config).AutoRevert;

    // ── Publish ───────────────────────────────────────────────────────────────

    // ── Lifecycle (destroy / prune) ───────────────────────────────────────────

    public async Task<IReadOnlyList<ManagedService>> ListManagedAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        using var client = BuildNomadClient(GetConfig(config));
        return await NomadLifecycle.ListManagedAsync(client, ct);
    }

    public async Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default)
    {
        using var client = BuildNomadClient(GetConfig(config));
        await NomadLifecycle.DeleteServiceAsync(client, service, options, logger, ct);
    }

    public IReadOnlyList<string> DescribeKept(EnvironmentConfig config, DeleteOptions options)
        => [.. NomadLifecycle.DescribeKept(GetConfig(config).NomadNamespace, options), "Consul itself (service registrations go away with the jobs; Deployify writes no intentions or config entries)"];

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
        return
        [
            .. Generator.Generate(manifest, config, cfg).Select(job => new ServiceExplanation
            {
                Name = job.Name,
                Image = job.Image,
                ImageTag = job.ImageTag,
                Count = job.Count,
                Env = ResolvedEnvironment.ForDisplay(job.Env, job.SecretEnv),
                Ports = [.. job.Ports.Select(p => $"{p.Label}:{p.ContainerPort}/{p.Protocol}")],
                Dependencies = job.Dependencies,
                AdditionalInfo = new Dictionary<string, string>
                {
                    ["nomadNamespace"] = cfg.NomadNamespace,
                    ["datacenters"] = string.Join(", ", cfg.Datacenters),
                    ["consulAddress"] = cfg.ConsulAddress,
                    ["networkMode"] = "bridge (Consul Connect)",
                    ["upstreams"] = job.Upstreams.Count == 0
                        ? "none"
                        : string.Join(", ", job.Upstreams.Select(u =>
                            $"{u.DestinationName}→localhost:{u.LocalBindPort}")),
                    ["ingressGateway"] = job.ExposeViaIngressGateway
                        ? cfg.IngressGatewayName
                        : "disabled"
                }
            })
        ];
    }

    // ── GetDependencies ───────────────────────────────────────────────────────

    public IReadOnlyList<ServiceDependency> GetDependencies(
        AspireManifest manifest, EnvironmentConfig config)
    {
        var cfg = GetConfig(config);
        return
        [
            .. Generator.Generate(manifest, config, cfg)
                .SelectMany(job => job.Dependencies.Select(dep =>
                    new ServiceDependency { From = dep, To = job.Name }))
        ];
    }

    // ── Diff ──────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
        => await CompareWithClusterAsync(manifest, config, GetConfig(config), outputDirectory, ct);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static NomadConsulTargetConfig GetConfig(EnvironmentConfig config)
    {
        if (config.TargetConfig.TryGetValue("nomadconsul", out var raw) && raw is JsonElement el)
        {
            return el.Deserialize<NomadConsulTargetConfig>(TargetConfigJsonOptions.Default) ?? new NomadConsulTargetConfig();
        }

        return new NomadConsulTargetConfig();
    }

    private NomadClient BuildNomadClient(NomadConsulTargetConfig cfg)
        => new(cfg.NomadAddress, Secrets.Resolve(cfg.NomadToken, "NOMAD_TOKEN"),
            loggerFactory.CreateLogger<NomadClient>(), cfg.NomadNamespace,
            Secrets.Resolve(cfg.RegistryPassword, "DEPLOYIFY_REGISTRY_PASSWORD"));

    private ConsulClient BuildConsulClient(NomadConsulTargetConfig cfg)
        => new(cfg.ConsulAddress, Secrets.Resolve(cfg.ConsulToken, "CONSUL_HTTP_TOKEN"), logger);
}
