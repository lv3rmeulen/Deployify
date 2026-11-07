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
    ILoggerFactory loggerFactory) : IDeploymentTarget
{
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
        var jobs = Generator.Generate(manifest, config, cfg);

        // Write HCL artifacts first — apply reads these
        var paths = new Dictionary<string, string>();
        foreach (var job in jobs)
        {
            paths[job.Name] = await Generator.WriteHclAsync(job, config, cfg, outputDirectory, ct);
        }

        using var nomadClient = BuildNomadClient(cfg);
        HashSet<string> running, managed;
        try
        {
            running = [.. await nomadClient.GetRunningJobNamesAsync(ct: ct)];
            managed = [.. await nomadClient.GetRunningJobNamesAsync(managedOnly: true, ct)];
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Could not reach Nomad at {Address} — assuming all services are new. ({Msg})",
                cfg.NomadAddress, ex.Message);
            running = [];
            managed = [];
        }

        var result = new PlanResult();
        foreach (var job in jobs)
        {
            if (!running.Contains(job.Name))
            {
                result.ToCreate.Add(job.Name);
                continue;
            }

            if (await TryPlanAsync(nomadClient, paths[job.Name], ct) is "None")
            {
                result.Unchanged.Add(job.Name);
            }
            else
            {
                result.ToUpdate.Add(job.Name);
            }
        }

        foreach (var name in managed.Except(jobs.Select(j => j.Name)))
        {
            result.ToDelete.Add(name);
        }

        return result;
    }

    private async Task<string> TryPlanAsync(NomadClient client, string hclPath, CancellationToken ct)
    {
        try
        {
            return await client.PlanJobAsync(hclPath, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not plan {Path}: {Message}", Path.GetFileName(hclPath), ex.Message);
            return "Edited";
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
            var hclPath = Path.Combine(outputDirectory, $"{job.Name}.job.hcl");
            if (!File.Exists(hclPath))
            {
                results.Add(new DeploymentResult
                {
                    ServiceName = job.Name,
                    Success = false,
                    Error = $"Artifact not found: {hclPath}. Run 'deployify plan' first."
                });
                continue;
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
                results.Add(new DeploymentResult
                {
                    ServiceName = job.Name,
                    DeploymentId = evalId,
                    Success = true
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to submit job '{Name}'.", job.Name);
                results.Add(new DeploymentResult
                {
                    ServiceName = job.Name,
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
    {
        var cfg = GetConfig(config);
        var jobs = Generator.Generate(manifest, config, cfg).ToDictionary(j => j.Name);
        var entries = new List<DiffEntry>();

        using var nomadClient = BuildNomadClient(cfg);
        HashSet<string> running, managed;
        try
        {
            running = [.. await nomadClient.GetRunningJobNamesAsync(ct: ct)];
            managed = [.. await nomadClient.GetRunningJobNamesAsync(managedOnly: true, ct)];
        }
        catch
        {
            // Nomad unreachable — treat every desired service as Added
            return
            [
                .. jobs.Keys
                    .Select(n => new DiffEntry { ServiceName = n, Kind = DiffKind.Added })
            ];
        }

        foreach (var (name, job) in jobs)
        {
            if (!running.Contains(name))
            {
                entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Added });
                continue;
            }

            var localPath = await Generator.WriteHclAsync(job, config, cfg, outputDirectory, ct);
            var kind = await TryPlanAsync(nomadClient, localPath, ct) is "None"
                ? DiffKind.Unchanged : DiffKind.Changed;

            entries.Add(new DiffEntry
            {
                ServiceName = name,
                Kind = kind,
                Field = kind == DiffKind.Changed ? "job spec" : null
            });
        }

        foreach (var name in managed.Except(jobs.Keys))
        {
            entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Removed });
        }

        return entries;
    }

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
