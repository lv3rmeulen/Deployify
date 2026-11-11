using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.SecretBackends;
using Deployify.Target.Kubernetes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Helm;

/// <summary>
/// Kubernetes, delivered as Helm releases. Every service becomes a chart (the Kubernetes target's
/// manifests, templated) installed as release <c>{service}</c> in namespace
/// <c>{namespacePrefix}-{service}</c>. Cluster and manifest settings come from
/// targetConfig["kubernetes"]; secrets are synced exactly as the Kubernetes target does.
/// </summary>
public sealed class HelmDeploymentTarget : IDeploymentTarget, ISecretStoreProvider, IRolloutTarget, IResourceLifecycle
{
    private readonly IPublisher _publisher;
    private readonly ILogger _logger;
    private readonly IProcessRunner _processes;
    private readonly KubernetesManifestGenerator _generator = new();
    private readonly HelmChartWriter _charts = new();

    /// <summary>
    /// The Kubernetes target does the parts that are the same with or without Helm: secret
    /// backends, namespaces, health and the destroy bookkeeping.
    /// </summary>
    internal KubernetesDeploymentTarget Kubernetes { get; }

    /// <summary>A service's out-of-band Secret before the rollout; in memory only, never journaled.</summary>
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, SensitiveValue>> _secretBefore = new();

    public HelmDeploymentTarget(IPublisher publisher, ILoggerFactory loggers)
        : this(publisher, loggers, ProcessRunner.Default)
    {
    }

    internal HelmDeploymentTarget(IPublisher publisher, ILoggerFactory loggers, IProcessRunner processes)
    {
        _publisher = publisher;
        _logger = loggers.CreateLogger<HelmDeploymentTarget>();
        _processes = processes;
        Kubernetes = new KubernetesDeploymentTarget(publisher, loggers.CreateLogger<KubernetesDeploymentTarget>())
        {
            Processes = processes
        };
    }

    public DeploymentTargetType TargetType => DeploymentTargetType.Helm;
    public string DisplayName => "Helm (Kubernetes)";

    // ── Configuration and tools ──────────────────────────────────────────────

    internal static HelmTargetConfig GetHelmConfig(EnvironmentConfig config)
        => config.TargetConfig.TryGetValue("helm", out var raw) && raw is JsonElement el
            ? el.Deserialize<HelmTargetConfig>(TargetConfigJsonOptions.Default) ?? new HelmTargetConfig()
            : new HelmTargetConfig();

    private HelmRunner Helm(EnvironmentConfig config)
        => new(KubernetesDeploymentTarget.GetConfig(config), GetHelmConfig(config), _logger, _processes);

    private KubectlRunner Kubectl(EnvironmentConfig config)
        => new(KubernetesDeploymentTarget.GetConfig(config), _logger, _processes);

    private Task<bool> HelmAvailableAsync(CancellationToken ct) => HelmRunner.IsAvailableAsync(_processes, ct);

    private Task<bool> KubectlAvailableAsync(CancellationToken ct) => KubectlRunner.IsAvailableAsync(_processes, ct);

    /// <summary>Why helm and kubectl can't be used right now, or null when both are on PATH.</summary>
    private async Task<string?> MissingToolsAsync(CancellationToken ct)
    {
        var missing = new List<string>();
        if (!await HelmAvailableAsync(ct))
        {
            missing.Add("helm");
        }

        if (!await KubectlAvailableAsync(ct))
        {
            missing.Add("kubectl");
        }

        return missing.Count == 0
            ? null
            : $"The Helm target needs {string.Join(" and ", missing)} on PATH.";
    }

    private async Task RequireToolsAsync(CancellationToken ct)
    {
        if (await MissingToolsAsync(ct) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }
    }

    private Task<IReadOnlyList<KubernetesServiceManifest>> GenerateServicesAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct)
        => _generator.GenerateAsync(manifest, config, KubernetesDeploymentTarget.GetConfig(config), ct);

    private async Task<KubernetesServiceManifest> FindServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct)
        => (await GenerateServicesAsync(manifest, config, ct)).FirstOrDefault(s => s.ServiceName == service)
           ?? throw new InvalidOperationException($"'{service}' is not a Kubernetes service in this manifest.");

    private async Task<List<string>> WriteChartsAsync(
        IReadOnlyList<KubernetesServiceManifest> services, EnvironmentConfig config, string outputDirectory, CancellationToken ct)
    {
        var chartVersion = GetHelmConfig(config).ChartVersion;
        var paths = new List<string>();
        foreach (var svc in services)
        {
            paths.AddRange(await _charts.WriteChartAsync(svc, outputDirectory, chartVersion, ct));
        }

        return paths;
    }

    // ── Secrets ───────────────────────────────────────────────────────────────

    public IReadOnlyCollection<SecretBackendType> SupportedSecretBackends => Kubernetes.SupportedSecretBackends;

    public ISecretStore CreateSecretStore(EnvironmentConfig config, ILogger storeLogger)
        => Kubernetes.CreateSecretStore(config, storeLogger);

    // ── IDeploymentTarget ─────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        if (await MissingToolsAsync(ct) is { } reason)
        {
            return (false, reason);
        }

        return await Kubernetes.PingAsync(config, ct);
    }

    /// <summary>Writes one chart per service to <c>{output}/helm/{service}</c>. Contacts nothing.</summary>
    public async Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => await WriteChartsAsync(await GenerateServicesAsync(manifest, config, ct), config, outputDirectory, ct);

    /// <summary>Runs <c>helm lint</c> on every chart; without helm, only writes them.</summary>
    public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var services = await GenerateServicesAsync(manifest, config, ct);
        await WriteChartsAsync(services, config, outputDirectory, ct);

        var lint = await HelmAvailableAsync(ct);
        if (!lint)
        {
            _logger.LogWarning("helm not found - skipping helm lint.");
        }

        var helm = Helm(config);
        var results = new List<ValidationResult>();
        foreach (var svc in services)
        {
            var chart = HelmChartWriter.ChartDirectory(outputDirectory, svc.ServiceName);
            results.Add(new ValidationResult
            {
                ArtifactName = chart,
                Errors = lint ? await helm.LintAsync(chart, ct) : []
            });
        }

        return results;
    }

    public async Task<PlanResult> PlanAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => PlanResult.FromDiff(await DiffAsync(manifest, config, outputDirectory, ct));

    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var services = await GenerateServicesAsync(manifest, config, ct);
        await WriteChartsAsync(services, config, outputDirectory, ct);
        return await CompareWithClusterAsync(services, config, outputDirectory, ct);
    }

    /// <summary>
    /// A service without a release is Added; one with a release is compared with helm-diff
    /// (Unchanged or Changed), or Unknown when that isn't possible. Releases of Deployify's
    /// layout (release {service} in {prefix}-{service}) that the manifest no longer has are
    /// Removed. An unreachable cluster is treated as empty, with a warning, as the other targets do.
    /// </summary>
    private async Task<List<DiffEntry>> CompareWithClusterAsync(
        IReadOnlyList<KubernetesServiceManifest> services, EnvironmentConfig config, string outputDirectory, CancellationToken ct)
    {
        var entries = new List<DiffEntry>();
        if (!await HelmAvailableAsync(ct))
        {
            entries.AddRange(services.Select(s => new DiffEntry
            {
                ServiceName = s.ServiceName, Kind = DiffKind.Unknown, Reason = "helm is not installed"
            }));
            return entries;
        }

        var helm = Helm(config);
        IReadOnlyList<HelmRelease> releases;
        try
        {
            releases = await helm.ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not reach the cluster — assuming all services are new. ({Message})", ex.Message);
            releases = [];
        }

        var installed = releases.Select(r => (r.Namespace, r.Name)).ToHashSet();
        foreach (var svc in services)
        {
            if (!installed.Contains((svc.Namespace, svc.ServiceName)))
            {
                entries.Add(new DiffEntry { ServiceName = svc.ServiceName, Kind = DiffKind.Added });
                continue;
            }

            var diff = await helm.DiffAsync(svc.ServiceName, HelmChartWriter.ChartDirectory(outputDirectory, svc.ServiceName),
                svc.Namespace, ct);
            entries.Add(diff.Kind switch
            {
                KubectlDiffKind.Same => new DiffEntry { ServiceName = svc.ServiceName, Kind = DiffKind.Unchanged },
                KubectlDiffKind.Different => new DiffEntry
                {
                    ServiceName = svc.ServiceName, Kind = DiffKind.Changed, Field = "release", NewValue = diff.Text
                },
                _ => new DiffEntry { ServiceName = svc.ServiceName, Kind = DiffKind.Unknown, Reason = diff.Text }
            });
        }

        var prefix = KubernetesDeploymentTarget.GetConfig(config).NamespacePrefix + "-";
        var desired = services.Select(s => s.ServiceName).ToHashSet(StringComparer.Ordinal);
        foreach (var release in releases)
        {
            if (release.Namespace == prefix + release.Name && !desired.Contains(release.Name))
            {
                entries.Add(new DiffEntry { ServiceName = release.Name, Kind = DiffKind.Removed });
            }
        }

        return entries;
    }

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var services = await GenerateServicesAsync(manifest, config, ct);
        if (await MissingToolsAsync(ct) is { } reason)
        {
            return [.. services.Select(s => new DeploymentResult { ServiceName = s.ServiceName, Success = false, Error = reason })];
        }

        var results = new List<DeploymentResult>();
        foreach (var svc in services)
        {
            var error = await InstallAsync(svc, config, outputDirectory, ct);
            results.Add(error is null
                ? new DeploymentResult { ServiceName = svc.ServiceName, DeploymentId = $"release/{svc.Namespace}/{svc.ServiceName}", Success = true }
                : new DeploymentResult { ServiceName = svc.ServiceName, Success = false, Error = error });
        }

        return results;
    }

    /// <summary>
    /// Deploys one service from the chart plan wrote: namespace first (Helm keeps its release
    /// record there), then secrets to their backend, then <c>helm upgrade --install</c>. Pods are
    /// restarted when only out-of-band secret values changed, which Helm can't see. Returns the
    /// error, or null on success.
    /// </summary>
    private async Task<string?> InstallAsync(
        KubernetesServiceManifest svc, EnvironmentConfig config, string outputDirectory, CancellationToken ct)
    {
        var chart = HelmChartWriter.ChartDirectory(outputDirectory, svc.ServiceName);
        if (!File.Exists(Path.Combine(chart, "Chart.yaml")))
        {
            return $"Chart not found: {chart}. Run 'deployify plan' first.";
        }

        try
        {
            var kubectl = Kubectl(config);
            await kubectl.ServerSideApplyAsync(KubernetesSecretManifest.Namespace(svc.Namespace, new Dictionary<string, string>
            {
                ["app.kubernetes.io/managed-by"] = "deployify",
                ["deployify/service"] = svc.ServiceName
            }), ct);

            var secretsChanged = await Kubernetes.SyncSecretsAsync(svc, config, kubectl, ct);
            var result = await Helm(config).UpgradeInstallAsync(svc.ServiceName, chart, svc.Namespace, ct);
            if (!result.Ok)
            {
                return result.ErrorOrOutput.Trim();
            }

            _logger.LogInformation("[helm] {Name}: release {Namespace}/{Name} upgraded.", svc.ServiceName, svc.Namespace, svc.ServiceName);
            if (secretsChanged && !await kubectl.RolloutRestartAsync(svc.ServiceName, svc.Namespace, ct))
            {
                _logger.LogWarning("Secrets of {Name} changed but its pods could not be restarted.", svc.ServiceName);
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    public Task PublishAsync(AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default)
        => _publisher.PublishAsync(manifest, config, appHostDirectory, ct);

    public async Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
    {
        var explanations = await Kubernetes.ExplainAsync(manifest, config, ct);
        foreach (var e in explanations)
        {
            e.AdditionalInfo["release"] = $"{e.AdditionalInfo.GetValueOrDefault("namespace", "?")}/{e.Name}";
            e.AdditionalInfo["chart"] = $"{HelmChartWriter.ChartsFolder}/{e.Name}";
        }

        return explanations;
    }

    public Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => Kubernetes.GetDependenciesAsync(manifest, config, ct);

    // ── Rollout (health-gated apply) ──────────────────────────────────────────
    //
    // The health gate watches the Deployment's generation, as on the Kubernetes target; the Helm
    // revision the service ran before is kept in the snapshot, and a rollback is helm rollback.

    private const string NamespaceKey = "namespace";
    private const string ReleaseStatusKey = "releaseStatus";

    public async Task<string?> GetUnavailableReasonAsync(EnvironmentConfig config, CancellationToken ct = default)
        => await MissingToolsAsync(ct);

    public Task<bool> HasNativeRollbackAsync(EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>
    /// Records the release's current revision (in the journal) and, for services whose Secret
    /// lives outside the chart, that Secret's values (in memory only).
    /// </summary>
    public async Task<ServiceSnapshot> CaptureAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
    {
        var svc = await FindServiceAsync(manifest, config, service, ct);
        await RequireToolsAsync(ct);

        if (svc.UsesSecretRefs && !svc.UsesKeyVaultCsi)
        {
            try
            {
                if (await Kubectl(config).GetSecretDataAsync(svc.Namespace, $"{service}-secrets", ct) is { } secret)
                {
                    _secretBefore[$"{svc.Namespace}/{service}"] = secret;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Could not read the Secret of {Name} ({Message}); a rollback will not restore it.",
                    service, ex.Message);
            }
        }

        var release = await Helm(config).StatusAsync(service, svc.Namespace, ct);
        var data = new Dictionary<string, string> { [NamespaceKey] = svc.Namespace };
        if (release is null)
        {
            return new ServiceSnapshot { ServiceName = service, Existed = false, Data = data };
        }

        data[ReleaseStatusKey] = release.Status;
        return new ServiceSnapshot
        {
            ServiceName = service,
            Existed = true,
            Revision = release.Revision.ToString(CultureInfo.InvariantCulture),
            Data = data
        };
    }

    public async Task<AppliedRevision> ApplyServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service, CancellationToken ct = default)
    {
        var svc = await FindServiceAsync(manifest, config, service, ct);
        await RequireToolsAsync(ct);

        if (await InstallAsync(svc, config, outputDirectory, ct) is { } error)
        {
            return new AppliedRevision { ServiceName = service, Success = false, Error = error };
        }

        try
        {
            return new AppliedRevision
            {
                ServiceName = service, Success = true, Revision = await GenerationAsync(config, svc.Namespace, service, ct)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AppliedRevision { ServiceName = service, Success = false, Error = ex.Message };
        }
    }

    public Task<HealthObservation> ObserveAsync(EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
        => Kubernetes.ObserveAsync(config, service, revision, ct);

    /// <summary>
    /// Puts the out-of-band Secret back, then <c>helm rollback</c> to the recorded revision
    /// (which also restores the ConfigMap and any inline Secret, both part of the chart). A
    /// release this rollout created is uninstalled. Returns the Deployment generation to watch.
    /// </summary>
    public async Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
    {
        await RequireToolsAsync(ct);
        var service = snapshot.ServiceName;
        var ns = snapshot.Data.TryGetValue(NamespaceKey, out var n)
            ? n
            : KubernetesManifestGenerator.NamespaceFor(KubernetesDeploymentTarget.GetConfig(config), service);
        var helm = Helm(config);

        if (_secretBefore.TryGetValue($"{ns}/{service}", out var secret))
        {
            var kubectl = Kubectl(config);
            await kubectl.ServerSideApplyAsync(KubernetesSecretManifest.Build(ns, $"{service}-secrets", secret,
                new Dictionary<string, string> { ["app"] = service }), ct);
            _logger.LogInformation("Restored Secret {Namespace}/{Name}-secrets.", ns, service);
        }

        if (snapshot is not { Existed: true, Revision: { } raw } ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision))
        {
            await helm.UninstallAsync(service, ns, ct);
            _logger.LogInformation("Uninstalled new release {Namespace}/{Name}.", ns, service);
            return null;
        }

        var current = await helm.StatusAsync(service, ns, ct)
                      ?? throw new InvalidOperationException($"Release {ns}/{service} no longer exists, so it can't be rolled back.");
        if (current.Revision != revision)
        {
            await helm.RollbackAsync(service, ns, revision, ct);
            _logger.LogInformation("Rolled release {Namespace}/{Name} back to revision {Revision}.", ns, service, revision);
        }

        return await GenerationAsync(config, ns, service, ct);
    }

    private async Task<string> GenerationAsync(EnvironmentConfig config, string ns, string service, CancellationToken ct)
    {
        var json = await Kubectl(config).GetDeploymentJsonAsync(service, ns, ct)
                   ?? throw new InvalidOperationException($"Deployment {ns}/{service} is missing after the Helm release.");
        return KubernetesDeploymentStatus.Parse(json).Generation.ToString(CultureInfo.InvariantCulture);
    }

    // ── Lifecycle (destroy / prune) ───────────────────────────────────────────

    /// <summary>The same as the Kubernetes target: the labelled namespaces under the prefix.</summary>
    public Task<IReadOnlyList<ManagedService>> ListManagedAsync(EnvironmentConfig config, CancellationToken ct = default)
        => Kubernetes.ListManagedAsync(config, ct);

    /// <summary>
    /// Uninstalls the release, then deletes the namespace (with what the chart didn't own, such
    /// as an out-of-band Secret). Only a namespace carrying Deployify's label is touched.
    /// </summary>
    public async Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default)
    {
        await RequireToolsAsync(ct);
        var cfg = KubernetesDeploymentTarget.GetConfig(config);
        var ns = KubernetesManifestGenerator.NamespaceFor(cfg, service);
        var managed = await Kubectl(config).GetNamespacesWithPrefixAsync(cfg.NamespacePrefix, ct);
        if (!managed.Contains(ns, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Namespace {Namespace} is not there (or not Deployify's); nothing to delete.", ns);
            return;
        }

        await Helm(config).UninstallAsync(service, ns, ct);
        _logger.LogInformation("Uninstalled release {Namespace}/{Name}.", ns, service);
        await Kubernetes.DeleteServiceAsync(config, service, options, ct);
    }

    public Task<IReadOnlyList<string>> DescribeKeptAsync(EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
        => Kubernetes.DescribeKeptAsync(config, options, ct);
}
