using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.SecretBackends;
using Deployify.Core.ServiceLogs;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.CloudRun;

/// <summary>
/// Google Cloud Run: each Aspire HTTP service becomes a Cloud Run service, described by a
/// generated Knative service spec and applied with <c>gcloud run services replace</c>. Every
/// deploy creates a revision; rollbacks, canaries and blue-green move traffic between revisions,
/// which Cloud Run splits by exact percentages. Secret values live in Secret Manager, and each
/// revision pins the secret versions it was deployed with.
///
/// Requires the gcloud CLI, signed in with access to the project.
/// </summary>
public sealed class CloudRunDeploymentTarget(
    IPublisher publisher,
    ILogger<CloudRunDeploymentTarget> logger)
    : IDeploymentTarget, ISecretStoreProvider, IRolloutTarget, ITrafficShiftingTarget, IResourceLifecycle, IServiceLogSource
{
    private readonly CloudRunManifestGenerator _generator = new();
    private readonly ConcurrentDictionary<string, string?> _projectNumbers = new(StringComparer.Ordinal);

    /// <summary>The revision each running candidate shares the traffic with, from its snapshot.</summary>
    private readonly ConcurrentDictionary<string, string> _stableRevision = new(StringComparer.Ordinal);

    /// <summary>Process runner for gcloud; tests swap in a fake.</summary>
    internal IProcessRunner? Runner { get; init; }

    private IProcessRunner Processes => Runner ?? ProcessRunner.Default;

    private GcloudClient Client(CloudRunTargetConfig cfg) => new(cfg, logger, Runner);

    public DeploymentTargetType TargetType => DeploymentTargetType.CloudRun;

    public string DisplayName => "Google Cloud Run";

    // ── Configuration ─────────────────────────────────────────────────────────

    internal static CloudRunTargetConfig GetConfig(EnvironmentConfig config)
    {
        var cfg = config.TargetConfig.TryGetValue("cloudrun", out var raw) && raw is JsonElement el
            ? el.Deserialize<CloudRunTargetConfig>(TargetConfigJsonOptions.Default) ?? new CloudRunTargetConfig()
            : new CloudRunTargetConfig();
        cfg.DeployifyEnvironment = config.Name;
        return cfg;
    }

    internal static string SecretPrefix(EnvironmentConfig config)
        => config.Secrets.GcpSecretManager?.SecretPrefix is { Length: > 0 } prefix
            ? prefix
            : $"deployify-{CloudRunManifestGenerator.LabelValue(config.Name ?? "default")}";

    internal static string SecretProject(EnvironmentConfig config, CloudRunTargetConfig cfg)
        => config.Secrets.GcpSecretManager?.ProjectId is { Length: > 0 } project ? project : cfg.ProjectId;

    /// <summary>The configured project number, else the one gcloud reports (once per project), else null.</summary>
    private async Task<string?> ProjectNumberAsync(CloudRunTargetConfig cfg, CancellationToken ct)
    {
        if (cfg.ProjectNumber is { Length: > 0 } configured)
        {
            return configured;
        }

        if (string.IsNullOrWhiteSpace(cfg.ProjectId))
        {
            return null;
        }

        if (_projectNumbers.TryGetValue(cfg.ProjectId, out var known))
        {
            return known;
        }

        string? number = null;
        try
        {
            if (await GcloudClient.IsAvailableAsync(Processes, ct))
            {
                number = await Client(cfg).GetProjectNumberAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Could not look up the number of project {Project}: {Message}", cfg.ProjectId, ex.Message);
        }

        _projectNumbers[cfg.ProjectId] = number;
        return number;
    }

    private async Task<(CloudRunTargetConfig Cfg, IReadOnlyList<CloudRunService> Services)> GenerateServicesAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct)
    {
        var cfg = GetConfig(config);
        return (cfg, await _generator.GenerateAsync(manifest, config, cfg, await ProjectNumberAsync(cfg, ct), ct));
    }

    private async Task<CloudRunService> FindAsync(AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct)
        => (await GenerateServicesAsync(manifest, config, ct)).Services.FirstOrDefault(s => s.Name == service)
           ?? throw new InvalidOperationException($"'{service}' is not a Cloud Run service in this manifest.");

    // ── Secrets ───────────────────────────────────────────────────────────────

    public IReadOnlyCollection<SecretBackendType> SupportedSecretBackends { get; } = [SecretBackendType.GcpSecretManager];

    public ISecretStore CreateSecretStore(EnvironmentConfig config, ILogger storeLogger)
    {
        var cfg = GetConfig(config);
        return new GcpSecretManagerStore(new GcloudClient(cfg, storeLogger, Runner), SecretProject(config, cfg), SecretPrefix(config));
    }

    /// <summary>
    /// Writes each secret env var of <paramref name="svc"/> to Secret Manager (a new version only
    /// when the value changed) and records where it went. Returns secret name → version, for pinning.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, string>> SyncSecretsAsync(
        CloudRunService svc, EnvironmentConfig config, CloudRunTargetConfig cfg, CancellationToken ct)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (svc.SecretEnv.Count == 0)
        {
            return versions;
        }

        var client = Client(cfg);
        var project = SecretProject(config, cfg);
        var prefix = SecretPrefix(config);
        var labels = new Dictionary<string, string>
        {
            [CloudRunManifestGenerator.ManagedByLabel] = CloudRunManifestGenerator.ManagedByValue,
            [CloudRunManifestGenerator.ServiceLabel] = CloudRunManifestGenerator.LabelValue(svc.Name),
            [CloudRunManifestGenerator.EnvironmentLabel] = CloudRunManifestGenerator.LabelValue(config.Name ?? "default")
        };

        foreach (var (key, value) in svc.SecretEnv.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var name = CloudRunManifestGenerator.SecretName(prefix, svc.Name, key);
            var desired = value.ToString();
            var version = await client.AccessLatestAsync(project, name, ct) == desired
                ? await client.LatestVersionAsync(project, name, ct)
                : await client.WriteAsync(project, name, desired, labels, ct);
            versions[name] = version;
            config.Secrets.References[$"{svc.Name}/{key}"] = new SecretReference
            {
                Backend = SecretBackendType.GcpSecretManager,
                Location = $"projects/{project}/secrets/{name}",
                Version = version
            };
        }

        return versions;
    }

    // ── Ping / generate / validate ────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
        => await GcloudClient.IsAvailableAsync(Processes, ct)
            ? await Client(GetConfig(config)).PingAsync(ct)
            : (false, "gcloud not found on PATH. Install the Google Cloud CLI from https://cloud.google.com/sdk/docs/install");

    public async Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var (cfg, services) = await GenerateServicesAsync(manifest, config, ct);
        var paths = new List<string>();
        foreach (var svc in services)
        {
            paths.Add(await _generator.WriteSpecAsync(svc, config, cfg, SecretPrefix(config), outputDirectory, ct));
        }

        return paths;
    }

    public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var (cfg, services) = await GenerateServicesAsync(manifest, config, ct);
        var results = new List<ValidationResult>();
        foreach (var svc in services)
        {
            var errors = new List<string>();
            if (svc.Image is null)
            {
                errors.Add("no image: set containerRegistry so the project can be published");
            }

            if (string.IsNullOrWhiteSpace(cfg.ProjectId))
            {
                errors.Add("cloudrun.projectId is not set");
            }

            if (svc.Name.Length > 49 || !svc.Name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            {
                errors.Add("Cloud Run service names are lowercase letters, digits and '-', at most 49 characters");
            }

            var path = await _generator.WriteSpecAsync(svc, config, cfg, SecretPrefix(config), outputDirectory, ct);
            results.Add(new ValidationResult { ArtifactName = path, Errors = errors });
        }

        return results;
    }

    // ── Plan / diff ───────────────────────────────────────────────────────────

    public async Task<PlanResult> PlanAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        await GenerateAsync(manifest, config, outputDirectory, ct);
        return PlanResult.FromDiff(await DiffAsync(manifest, config, outputDirectory, ct));
    }

    /// <summary>
    /// Compares each generated spec's hash with the <c>deployify-spec</c> label of the live
    /// service. Only services Deployify labelled for this environment can show as removed.
    /// </summary>
    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var (cfg, services) = await GenerateServicesAsync(manifest, config, ct);
        IReadOnlyList<CloudRunServiceStatus> live;
        try
        {
            live = await Client(cfg).ListManagedAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Could not list Cloud Run services: {Message}", ex.Message);
            return [.. services.Select(s => new DiffEntry { ServiceName = s.Name, Kind = DiffKind.Unknown, Reason = ex.Message })];
        }

        var byName = live.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var entries = new List<DiffEntry>();
        foreach (var svc in services)
        {
            var hash = CloudRunManifestGenerator.BuildSpec(svc, cfg, SecretPrefix(config))["metadata"]!["labels"]![CloudRunManifestGenerator.SpecLabel]!
                .GetValue<string>();
            if (!byName.TryGetValue(svc.Name, out var current))
            {
                entries.Add(new DiffEntry { ServiceName = svc.Name, Kind = DiffKind.Added });
                continue;
            }

            current.Labels.TryGetValue(CloudRunManifestGenerator.SpecLabel, out var liveHash);
            entries.Add(liveHash == hash
                ? new DiffEntry { ServiceName = svc.Name, Kind = DiffKind.Unchanged }
                : new DiffEntry { ServiceName = svc.Name, Kind = DiffKind.Changed, Field = "spec", OldValue = liveHash, NewValue = hash });
        }

        var desired = services.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var env = cfg.DeployifyEnvironment is { Length: > 0 } e ? CloudRunManifestGenerator.LabelValue(e) : null;
        foreach (var gone in live.Where(s => !desired.Contains(s.Name) && OurEnvironment(s, env)))
        {
            entries.Add(new DiffEntry { ServiceName = gone.Name, Kind = DiffKind.Removed });
        }

        return entries;
    }

    private static bool OurEnvironment(CloudRunServiceStatus service, string? env)
        => env is null || !service.Labels.TryGetValue(CloudRunManifestGenerator.EnvironmentLabel, out var tagged) || tagged == env;

    // ── Apply ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var (cfg, services) = await GenerateServicesAsync(manifest, config, ct);
        var results = new List<DeploymentResult>();
        foreach (var svc in services)
        {
            try
            {
                var status = await DeployAsync(svc, config, cfg, outputDirectory, stableRevision: null, wait: true, ct);
                results.Add(new DeploymentResult { ServiceName = svc.Name, Success = true, DeploymentId = status.LatestReadyRevision ?? status.Url });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Failed to deploy Cloud Run service {Name}: {Message}", svc.Name, ex.Message);
                results.Add(new DeploymentResult { ServiceName = svc.Name, Success = false, Error = ex.Message });
            }
        }

        return results;
    }

    /// <summary>
    /// Secrets to Secret Manager, then the spec plan wrote (secret versions pinned, and for a
    /// candidate the traffic left on <paramref name="stableRevision"/>) through gcloud.
    /// </summary>
    private async Task<CloudRunServiceStatus> DeployAsync(
        CloudRunService svc, EnvironmentConfig config, CloudRunTargetConfig cfg, string outputDirectory,
        string? stableRevision, bool wait, CancellationToken ct)
    {
        var specPath = Path.Combine(outputDirectory, CloudRunManifestGenerator.SpecFileName(svc.Name));
        if (!File.Exists(specPath))
        {
            throw new InvalidOperationException($"Service spec not found: {specPath}. Run 'deployify plan' first.");
        }

        var versions = await SyncSecretsAsync(svc, config, cfg, ct);
        var spec = JsonNode.Parse(await File.ReadAllTextAsync(specPath, ct))!.AsObject();
        var deploy = CloudRunManifestGenerator.ForDeploy(spec, versions, stableRevision);

        var temp = Path.Combine(Path.GetTempPath(), $"deployify-{svc.Name}-{Guid.NewGuid():N}.service.json");
        await File.WriteAllTextAsync(temp, deploy.ToJsonString(), new UTF8Encoding(false), ct);
        CloudRunServiceStatus status;
        try
        {
            status = await Client(cfg).ReplaceAsync(svc.Name, temp, wait, ct);
        }
        finally
        {
            File.Delete(temp);
        }

        if (cfg.AllowUnauthenticated)
        {
            await Client(cfg).AllowUnauthenticatedAsync(svc.Name, ct);
        }

        logger.LogInformation("Deployed Cloud Run service {Name} (generation {Generation}).", svc.Name, status.Generation);
        return status;
    }

    public Task PublishAsync(AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default)
        => publisher.PublishAsync(manifest, config, appHostDirectory, ct);

    // ── Explain / graph ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
    {
        var (cfg, services) = await GenerateServicesAsync(manifest, config, ct);
        var number = await ProjectNumberAsync(cfg, ct);
        return
        [
            .. services.Select(svc => new ServiceExplanation
            {
                Name = svc.Name,
                Image = svc.Image,
                ImageTag = svc.ImageTag,
                Count = svc.MaxInstances,
                Env = ResolvedEnvironment.ForDisplay(svc.Env, svc.SecretEnv),
                Ports = [$"{svc.Port}/http"],
                Dependencies = svc.Dependencies,
                AdditionalInfo = new Dictionary<string, string>
                {
                    ["project"] = cfg.ProjectId,
                    ["region"] = cfg.Region,
                    ["url"] = number is null ? "(set cloudrun.projectNumber)" : $"https://{CloudRunManifestGenerator.Host(svc.Name, number, cfg.Region)}",
                    ["ingress"] = svc.External ? "all" : cfg.InternalIngress,
                    ["minInstances"] = svc.MinInstances.ToString(CultureInfo.InvariantCulture),
                    ["maxInstances"] = svc.MaxInstances.ToString(CultureInfo.InvariantCulture),
                    ["cpu"] = svc.Cpu,
                    ["memory"] = svc.Memory,
                    ["rollout"] = svc.Rollout.Describe(),
                    ["type"] = svc.IsProject ? "project" : "container"
                }
            })
        ];
    }

    public async Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        =>
        [
            .. (await GenerateServicesAsync(manifest, config, ct)).Services
                .SelectMany(s => s.Dependencies.Select(d => new ServiceDependency { From = d, To = s.Name }))
        ];

    // ── Rollout (health-gated apply) ──────────────────────────────────────────

    /// <summary>Records the revision that has all the traffic now (references only, nothing secret).</summary>
    public async Task<ServiceSnapshot> CaptureAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
    {
        var status = await Client(GetConfig(config)).DescribeAsync(service, ct);
        if (status is null)
        {
            return new ServiceSnapshot { ServiceName = service, Existed = false };
        }

        var revision = status.Serving ?? status.LatestReadyRevision;
        return new ServiceSnapshot { ServiceName = service, Existed = revision is not null, Revision = revision };
    }

    /// <summary>Deploys without waiting; the revision to watch is the service's new generation.</summary>
    public async Task<AppliedRevision> ApplyServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service, CancellationToken ct = default)
    {
        try
        {
            var svc = await FindAsync(manifest, config, service, ct);
            var status = await DeployAsync(svc, config, GetConfig(config), outputDirectory, stableRevision: null, wait: false, ct);
            return new AppliedRevision { ServiceName = service, Success = true, Revision = Generation(status) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AppliedRevision { ServiceName = service, Success = false, Error = ex.Message };
        }
    }

    public async Task<HealthObservation> ObserveAsync(
        EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
    {
        var client = Client(GetConfig(config));
        var status = await client.DescribeAsync(service, ct);
        if (CloudRunHealth.IsGeneration(revision, out var generation))
        {
            return CloudRunHealth.Evaluate(status, generation);
        }

        return CloudRunHealth.EvaluateRevision(status, status is null ? null : await client.DescribeRevisionAsync(revision, ct), revision);
    }

    /// <summary>
    /// Sends all traffic back to the snapshot's revision (Cloud Run keeps old revisions, with the
    /// secret versions they were deployed with) and drops a candidate's tag. A service this
    /// rollout created is deleted.
    /// </summary>
    public async Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
    {
        var client = Client(GetConfig(config));
        var service = snapshot.ServiceName;
        var status = await client.DescribeAsync(service, ct);

        if (snapshot is not { Existed: true, Revision: { } previous })
        {
            if (status is not null)
            {
                await client.DeleteServiceAsync(service, ct);
                logger.LogInformation("Deleted new Cloud Run service {Name}.", service);
            }

            return null;
        }

        if (status is null)
        {
            throw new InvalidOperationException($"Cloud Run service '{service}' no longer exists, so it can't be rolled back.");
        }

        await client.SetTrafficAsync(service, new Dictionary<string, int> { [previous] = 100 }, ct);
        if (status.Traffic.Any(t => t.Tag == CloudRunManifestGenerator.CandidateTag))
        {
            await client.RemoveTagAsync(service, CloudRunManifestGenerator.CandidateTag, ct);
        }

        logger.LogInformation("Sent all traffic of {Name} back to revision {Previous}.", service, previous);
        return previous;
    }

    public Task<bool> HasNativeRollbackAsync(EnvironmentConfig config, CancellationToken ct = default) => Task.FromResult(false);

    public async Task<string?> GetUnavailableReasonAsync(EnvironmentConfig config, CancellationToken ct = default)
        => await GcloudClient.IsAvailableAsync(Processes, ct) ? null : "gcloud is not installed";

    private static string Generation(CloudRunServiceStatus status) => status.Generation.ToString(CultureInfo.InvariantCulture);

    // ── Canary and blue-green: revision traffic splits ────────────────────────

    public RolloutStrategyMode StrategyMode => RolloutStrategyMode.Driven;

    public TrafficPrecision Precision => TrafficPrecision.Exact;

    public Task<string?> GetStrategyUnsupportedReasonAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, RolloutStrategy strategy, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Deploys the new spec with all traffic left on the running revision and the new one tagged
    /// <c>candidate</c>, which makes it reachable at <c>https://candidate---{service host}</c>.
    /// The candidate is identified by the service's generation until Cloud Run names its revision.
    /// </summary>
    public async Task<AppliedRevision> StartCandidateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service, RolloutStrategy strategy,
        ServiceSnapshot before, CancellationToken ct = default)
    {
        try
        {
            var cfg = GetConfig(config);
            var stable = before.Revision ?? throw new InvalidOperationException("the running revision is unknown");
            var svc = await FindAsync(manifest, config, service, ct);
            var status = await DeployAsync(svc, config, cfg, outputDirectory, stable, wait: false, ct);
            _stableRevision[service] = stable;
            return new AppliedRevision
            {
                ServiceName = service, Success = true, Revision = Generation(status), PreviewUrl = TagUrl(status.Url)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AppliedRevision { ServiceName = service, Success = false, Error = ex.Message };
        }
    }

    /// <summary><c>https://api-123.us-central1.run.app</c> → <c>https://candidate---api-123.us-central1.run.app</c>.</summary>
    internal static string? TagUrl(string? serviceUrl)
        => serviceUrl is { } url && url.StartsWith("https://", StringComparison.Ordinal)
            ? $"https://{CloudRunManifestGenerator.CandidateTag}---{url["https://".Length..]}"
            : null;

    public async Task<HealthObservation> ObserveCandidateAsync(
        EnvironmentConfig config, string service, string candidate, CancellationToken ct = default)
    {
        var client = Client(GetConfig(config));
        var status = await client.DescribeAsync(service, ct);
        _stableRevision.TryGetValue(service, out var stable);
        var generation = long.Parse(candidate, CultureInfo.InvariantCulture);
        var revision = status is { LatestCreatedRevision: { } name } && status.ObservedGeneration >= generation && name != stable
            ? await client.DescribeRevisionAsync(name, ct)
            : null;
        return CloudRunHealth.EvaluateCandidate(status, revision, generation, stable);
    }

    public async Task<int> ShiftTrafficAsync(
        EnvironmentConfig config, string service, string candidate, int percent, CancellationToken ct = default)
    {
        var client = Client(GetConfig(config));
        var status = await client.DescribeAsync(service, ct)
                     ?? throw new InvalidOperationException($"Cloud Run service '{service}' disappeared during the rollout.");
        if (status.ObservedGeneration < long.Parse(candidate, CultureInfo.InvariantCulture) || status.LatestCreatedRevision is not { } name)
        {
            throw new InvalidOperationException("Cloud Run hasn't created the candidate revision yet.");
        }

        if (!_stableRevision.TryGetValue(service, out var stable) || stable == name)
        {
            return percent; // no new revision: nothing to split
        }

        var split = percent >= 100
            ? new Dictionary<string, int> { [name] = 100 }
            : new Dictionary<string, int> { [stable] = 100 - percent, [name] = percent };
        await client.SetTrafficAsync(service, split, ct);
        return Math.Min(percent, 100);
    }

    /// <summary>Applies the generated spec, whose traffic all goes to the latest revision (the candidate) without a tag.</summary>
    public async Task<string?> PromoteAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service, string candidate,
        CancellationToken ct = default)
    {
        var svc = await FindAsync(manifest, config, service, ct);
        var status = await DeployAsync(svc, config, GetConfig(config), outputDirectory, stableRevision: null, wait: false, ct);
        return Generation(status);
    }

    /// <summary>Nothing to remove: the old revision keeps no traffic and stays available for a rollback, as Cloud Run keeps revisions.</summary>
    public Task FinishAsync(EnvironmentConfig config, string service, string candidate, CancellationToken ct = default)
    {
        _stableRevision.TryRemove(service, out _);
        return Task.CompletedTask;
    }

    // ── Logs ──────────────────────────────────────────────────────────────────

    /// <summary>How often <c>--follow</c> asks Cloud Logging for new lines; tests shorten it.</summary>
    internal TimeSpan LogPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public async IAsyncEnumerable<ServiceLogLine> ReadLogsAsync(
        EnvironmentConfig config, ServiceLogQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!await GcloudClient.IsAvailableAsync(Processes, ct))
        {
            throw new ServiceLogsException("Reading logs on Cloud Run needs the Google Cloud CLI (gcloud) on PATH.");
        }

        var cfg = GetConfig(config);
        await foreach (var line in CloudRunLogs.ReadAsync(Client(cfg), cfg, query, LogPollInterval, ct))
        {
            yield return line;
        }
    }

    // ── Lifecycle (destroy / prune) ───────────────────────────────────────────

    public async Task<IReadOnlyList<ManagedService>> ListManagedAsync(EnvironmentConfig config, CancellationToken ct = default)
        =>
        [
            .. (await Client(GetConfig(config)).ListManagedAsync(ct)).Select(s => new ManagedService(
                s.Name, s.Url, s.Labels.TryGetValue(CloudRunManifestGenerator.EnvironmentLabel, out var env) ? env : null))
        ];

    /// <summary>Deletes the service and, unless asked to keep them, its Secret Manager secrets.</summary>
    public async Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var client = Client(cfg);
        await client.DeleteServiceAsync(service, ct);
        logger.LogInformation("Deleted Cloud Run service {Name}.", service);
        if (options.KeepSecrets)
        {
            return;
        }

        var project = SecretProject(config, cfg);
        var filter = $"{CloudRunManifestGenerator.ServiceLabel}={CloudRunManifestGenerator.LabelValue(service)} AND " +
                     $"labels.{CloudRunManifestGenerator.EnvironmentLabel}={CloudRunManifestGenerator.LabelValue(config.Name ?? "default")}";
        foreach (var secret in await client.ListSecretsAsync(project, filter, ct))
        {
            await client.DeleteSecretAsync(project, secret, ct);
            logger.LogInformation("Deleted secret {Secret}.", secret);
        }
    }

    public Task<IReadOnlyList<string>> DescribeKeptAsync(EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        return Task.FromResult<IReadOnlyList<string>>(
        [
            $"the project '{cfg.ProjectId}' and its service accounts, VPC connectors and IAM bindings",
            $"the generated-values secret {SecretPrefix(config)}-generated",
            "images in the registry"
        ]);
    }
}
