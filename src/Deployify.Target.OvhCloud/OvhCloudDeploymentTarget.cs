using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.SecretBackends;
using Deployify.Target.Kubernetes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.OvhCloud;

/// <summary>
/// OVHcloud Managed Kubernetes Service. Services are deployed exactly as on the Kubernetes target
/// (same manifests, secrets, health-gated rollouts and destroy), with the cluster found through
/// the OVHcloud API: its status is checked before anything touches it, and its kubeconfig is
/// fetched from the API, so neither the Control Panel nor a hand-copied kubeconfig is needed.
/// Manifest settings come from targetConfig["kubernetes"], OVHcloud ones from targetConfig["ovhcloud"].
/// </summary>
public sealed class OvhCloudDeploymentTarget : IDeploymentTarget, ISecretStoreProvider, IRolloutTarget, IResourceLifecycle
{
    private readonly ILogger _logger;
    private readonly HttpMessageHandler? _apiHandler;

    /// <summary>Kubeconfig paths already fetched in this process, by project and cluster.</summary>
    private readonly Dictionary<string, string> _connected = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    /// <summary>Does the cluster work once it has been reached.</summary>
    internal KubernetesDeploymentTarget Kubernetes { get; }

    /// <summary>How often a cluster that is not ready yet is checked again.</summary>
    internal TimeSpan ReadyPollInterval { get; init; } = TimeSpan.FromSeconds(15);

    internal TimeProvider Time { get; init; } = TimeProvider.System;

    public OvhCloudDeploymentTarget(IPublisher publisher, ILoggerFactory loggers)
        : this(publisher, loggers, ProcessRunner.Default, apiHandler: null)
    {
    }

    internal OvhCloudDeploymentTarget(
        IPublisher publisher, ILoggerFactory loggers, IProcessRunner processes, HttpMessageHandler? apiHandler)
    {
        _logger = loggers.CreateLogger<OvhCloudDeploymentTarget>();
        _apiHandler = apiHandler;
        Kubernetes = new KubernetesDeploymentTarget(publisher, loggers.CreateLogger<KubernetesDeploymentTarget>())
        {
            Processes = processes
        };
    }

    public DeploymentTargetType TargetType => DeploymentTargetType.OvhCloud;
    public string DisplayName => "OVHcloud Managed Kubernetes";

    // ── Configuration and connection ─────────────────────────────────────────

    internal static OvhCloudTargetConfig GetOvhConfig(EnvironmentConfig config)
        => config.TargetConfig.TryGetValue("ovhcloud", out var raw) && raw is JsonElement el
            ? el.Deserialize<OvhCloudTargetConfig>(TargetConfigJsonOptions.Default) ?? new OvhCloudTargetConfig()
            : raw as OvhCloudTargetConfig ?? new OvhCloudTargetConfig();

    internal static string? ProjectId(OvhCloudTargetConfig cfg) => Secrets.Resolve(cfg.ProjectId, "OVH_CLOUD_PROJECT_SERVICE");

    /// <summary>Where a cluster's kubeconfig is kept unless ovhcloud.kubeconfigPath says otherwise.</summary>
    internal static string DefaultKubeconfigPath(string projectId, string clusterId)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, "deployify", "ovhcloud", $"{Sanitize(projectId)}-{Sanitize(clusterId)}.kubeconfig");

        static string Sanitize(string s)
        {
            var chars = s.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (!char.IsAsciiLetterOrDigit(chars[i]) && chars[i] is not '-' and not '_')
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }
    }

    private OvhApiClient Api(OvhCloudTargetConfig cfg)
        => new(cfg.Endpoint, OvhCredentials.From(cfg), _apiHandler, Time);

    /// <summary>
    /// The environment as the Kubernetes target should see it: its kubeconfig is the one fetched
    /// from the OVHcloud API for ovhcloud.clusterId, once per process, after the cluster is READY.
    /// Without a project and cluster, kubernetes.kubeConfigPath (a kubeconfig downloaded from the
    /// Control Panel) is used as it is.
    /// </summary>
    internal async Task<EnvironmentConfig> ConnectAsync(EnvironmentConfig config, CancellationToken ct)
    {
        var ovh = GetOvhConfig(config);
        var kubernetes = KubernetesDeploymentTarget.GetConfig(config);
        var projectId = ProjectId(ovh);
        if (projectId is null || string.IsNullOrWhiteSpace(ovh.ClusterId))
        {
            if (kubernetes.KubeConfigPath is not null)
            {
                return config;
            }

            throw new InvalidOperationException(
                "The OVHcloud target needs targetConfig.ovhcloud.projectId (or OVH_CLOUD_PROJECT_SERVICE) and " +
                "targetConfig.ovhcloud.clusterId, or targetConfig.kubernetes.kubeConfigPath set to a kubeconfig " +
                "downloaded from the OVHcloud Control Panel.");
        }

        var path = ovh.KubeconfigPath is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : DefaultKubeconfigPath(projectId, ovh.ClusterId);

        await _connectLock.WaitAsync(ct);
        try
        {
            var key = $"{projectId}/{ovh.ClusterId}";
            if (!_connected.ContainsKey(key))
            {
                using var api = Api(ovh);
                var cluster = new OvhKubernetesCluster(api, projectId, ovh.ClusterId);
                var info = await cluster.WaitUntilReadyAsync(
                    ovh.WaitForReady, TimeSpan.FromSeconds(Math.Max(0, ovh.ReadyTimeoutSeconds)), ReadyPollInterval,
                    _logger, Time, ct);
                await cluster.WriteKubeconfigAsync(path, ct);
                _logger.LogInformation("Connected to OVHcloud cluster {Name} ({Region}, Kubernetes {Version}).",
                    info.Name, info.Region, info.Version ?? "?");
                _connected[key] = path;
            }
        }
        finally
        {
            _connectLock.Release();
        }

        // An explicit kubeconfig would point at another cluster: the API's is the one for clusterId.
        // The context stays as configured; null uses the kubeconfig's current context.
        kubernetes.KubeConfigPath = path;
        return config.WithTargetConfig("kubernetes",
            JsonSerializer.SerializeToElement(kubernetes, TargetConfigJsonOptions.Default));
    }

    // ── Secrets ───────────────────────────────────────────────────────────────

    public IReadOnlyCollection<SecretBackendType> SupportedSecretBackends => Kubernetes.SupportedSecretBackends;

    public ISecretStore CreateSecretStore(EnvironmentConfig config, ILogger storeLogger)
    {
        // Only the Kubernetes Secret store talks to the cluster. The interface is synchronous, and
        // the CLI has no synchronization context, so blocking on the (cached) connection is safe.
        var effective = config.Secrets.Backend == SecretBackendType.KubernetesSecret
            ? ConnectAsync(config, CancellationToken.None).GetAwaiter().GetResult()
            : config;
        return Kubernetes.CreateSecretStore(effective, storeLogger);
    }

    // ── IDeploymentTarget ─────────────────────────────────────────────────────

    /// <summary>Checks the cluster's status through the OVHcloud API, then the Kubernetes API itself.</summary>
    public async Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        var ovh = GetOvhConfig(config);
        try
        {
            if (ProjectId(ovh) is { } projectId && !string.IsNullOrWhiteSpace(ovh.ClusterId))
            {
                using var api = Api(ovh);
                var cluster = await new OvhKubernetesCluster(api, projectId, ovh.ClusterId).GetAsync(ct);
                if (!cluster.IsReady)
                {
                    return (false, $"OVHcloud cluster '{cluster.Name}' ({cluster.Region}) is {cluster.Status}, not READY.");
                }
            }

            return await Kubernetes.PingAsync(await ConnectAsync(config, ct), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }

    // Generating manifests and describing them needs no cluster, and so no OVHcloud credentials.

    public Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Kubernetes.GenerateAsync(manifest, config, outputDirectory, ct);

    public Task PublishAsync(AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default)
        => Kubernetes.PublishAsync(manifest, config, appHostDirectory, ct);

    public async Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
    {
        var explanations = await Kubernetes.ExplainAsync(manifest, config, ct);
        var ovh = GetOvhConfig(config);
        if (!string.IsNullOrWhiteSpace(ovh.ClusterId))
        {
            foreach (var explanation in explanations)
            {
                explanation.AdditionalInfo["ovhcloud cluster"] = ovh.ClusterId;
            }
        }

        return explanations;
    }

    public Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => Kubernetes.GetDependenciesAsync(manifest, config, ct);

    // Validate, plan, diff and apply report an unreachable cluster the way the Kubernetes target
    // does (a warning, services that could not be checked, failed services) instead of throwing:
    // the commands print those, and still write the artifacts.

    public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var (connected, error) = await TryConnectAsync(config, ct);
        if (connected is not null)
        {
            return await Kubernetes.ValidateAsync(manifest, connected, outputDirectory, ct);
        }

        _logger.LogWarning("Skipping server-side validation: {Error}", error);
        var files = await Kubernetes.GenerateAsync(manifest, config, outputDirectory, ct);
        return [.. files.Select(f => new ValidationResult { ArtifactName = f, Errors = [] })];
    }

    public async Task<PlanResult> PlanAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => PlanResult.FromDiff(await DiffAsync(manifest, config, outputDirectory, ct));

    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var (connected, error) = await TryConnectAsync(config, ct);
        if (connected is not null)
        {
            return await Kubernetes.DiffAsync(manifest, connected, outputDirectory, ct);
        }

        await Kubernetes.GenerateAsync(manifest, config, outputDirectory, ct);
        return
        [
            .. (await Kubernetes.ExplainAsync(manifest, config, ct)).Select(s => new DiffEntry
            {
                ServiceName = s.Name, Kind = DiffKind.Unknown, Reason = error
            })
        ];
    }

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var (connected, error) = await TryConnectAsync(config, ct);
        if (connected is not null)
        {
            return await Kubernetes.ApplyAsync(manifest, connected, outputDirectory, ct);
        }

        return
        [
            .. (await Kubernetes.ExplainAsync(manifest, config, ct)).Select(s => new DeploymentResult
            {
                ServiceName = s.Name, Success = false, Error = error
            })
        ];
    }

    private async Task<(EnvironmentConfig? Config, string? Error)> TryConnectAsync(EnvironmentConfig config, CancellationToken ct)
    {
        try
        {
            return (await ConnectAsync(config, ct), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    // ── Rollout (health-gated apply) ──────────────────────────────────────────

    public async Task<ServiceSnapshot> CaptureAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
        => await Kubernetes.CaptureAsync(manifest, await ConnectAsync(config, ct), service, ct);

    public async Task<AppliedRevision> ApplyServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service, CancellationToken ct = default)
        => await Kubernetes.ApplyServiceAsync(manifest, await ConnectAsync(config, ct), outputDirectory, service, ct);

    public async Task<HealthObservation> ObserveAsync(
        EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
        => await Kubernetes.ObserveAsync(await ConnectAsync(config, ct), service, revision, ct);

    public async Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
        => await Kubernetes.RestoreAsync(await ConnectAsync(config, ct), snapshot, ct);

    public Task<bool> HasNativeRollbackAsync(EnvironmentConfig config, CancellationToken ct = default)
        => Kubernetes.HasNativeRollbackAsync(config, ct);

    /// <summary>
    /// Also unavailable when the cluster can't be reached: the plain apply then reports that per
    /// service, rather than the rollout failing on its first snapshot.
    /// </summary>
    public async Task<string?> GetUnavailableReasonAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        if (await Kubernetes.GetUnavailableReasonAsync(config, ct) is { } reason)
        {
            return reason;
        }

        var (_, error) = await TryConnectAsync(config, ct);
        return error;
    }

    public async Task CompleteAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => await ((IRolloutTarget)Kubernetes).CompleteAsync(manifest, await ConnectAsync(config, ct), outputDirectory, ct);

    // ── Lifecycle (destroy / prune) ───────────────────────────────────────────

    public async Task<IReadOnlyList<ManagedService>> ListManagedAsync(EnvironmentConfig config, CancellationToken ct = default)
        => await Kubernetes.ListManagedAsync(await ConnectAsync(config, ct), ct);

    public async Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default)
        => await Kubernetes.DeleteServiceAsync(await ConnectAsync(config, ct), service, options, ct);

    public async Task<IReadOnlyList<string>> DestroyEnvironmentAsync(
        EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
        => await ((IResourceLifecycle)Kubernetes).DestroyEnvironmentAsync(await ConnectAsync(config, ct), options, ct);

    /// <summary>What the Kubernetes target keeps, plus the cluster itself, which Deployify never touches.</summary>
    public async Task<IReadOnlyList<string>> DescribeKeptAsync(
        EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
    {
        var kept = new List<string>(await Kubernetes.DescribeKeptAsync(config, options, ct));
        var ovh = GetOvhConfig(config);
        kept.Add(string.IsNullOrWhiteSpace(ovh.ClusterId)
            ? "the OVHcloud cluster and its node pools"
            : $"the OVHcloud cluster {ovh.ClusterId} and its node pools");
        return kept;
    }
}
