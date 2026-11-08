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
using Deployify.Core.SecretBackends;
using Deployify.Core.SecretBackends.KeyVault;
using Deployify.Core.SecretBackends.Vault;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Kubernetes;

public sealed class KubernetesDeploymentTarget(
    IPublisher publisher,
    ILogger<KubernetesDeploymentTarget> logger) : IDeploymentTarget, ISecretStoreProvider, IRolloutTarget
{
    private readonly KubernetesManifestGenerator _generator = new();

    /// <summary>
    /// A service's ConfigMap and Secret as they were before the rollout, kept in memory only so
    /// no secret reaches the journal. A rollback in a later process can't restore them.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CapturedConfig> _configBefore = new();

    private sealed record CapturedConfig(
        Dictionary<string, string>? ConfigMap,
        bool SecretCaptured,
        IReadOnlyDictionary<string, SensitiveValue>? Secret);

    /// <summary>Creates Vault clients; replaceable in tests.</summary>
    internal Func<VaultConfig, VaultClient> VaultClientFactory { get; init; } = c => VaultClient.FromConfig(c);

    /// <summary>Creates Key Vault clients; replaceable in tests.</summary>
    internal Func<KeyVaultConfig, IKeyVaultClient> KeyVaultClientFactory { get; init; } = c => new AzureCliKeyVaultClient(c.SubscriptionId);

    /// <summary>Starts kubectl; replaceable in tests.</summary>
    internal IProcessRunner Processes { get; init; } = ProcessRunner.Default;

    private Task<bool> KubectlAvailableAsync(CancellationToken ct) => KubectlRunner.IsAvailableAsync(Processes, ct);

    private KubectlRunner Kubectl(KubernetesTargetConfig cfg, ILogger? log = null) => new(cfg, log ?? logger, Processes);

    // ── Secrets ───────────────────────────────────────────────────────────────

    public IReadOnlyCollection<SecretBackendType> SupportedSecretBackends { get; } =
        [SecretBackendType.KubernetesSecret, SecretBackendType.Vault, SecretBackendType.KeyVault];

    public ISecretStore CreateSecretStore(EnvironmentConfig config, ILogger storeLogger)
    {
        var kubernetesConfig = GetConfig(config);
        return config.Secrets.Backend switch
        {
            SecretBackendType.KubernetesSecret => new KubernetesSecretStore(
                Kubectl(kubernetesConfig, storeLogger), SecretsNamespace(kubernetesConfig)),
            SecretBackendType.Vault => new VaultSecretStore(
                () => VaultClientFactory(config.Secrets.Vault), config.Secrets.Vault.Mount,
                KubernetesManifestGenerator.VaultParametersPath(config.Secrets.Vault, kubernetesConfig)),
            SecretBackendType.KeyVault => new KeyVaultSecretStore(
                KeyVaultClientFactory(config.Secrets.KeyVault), KeyVaultSecrets.VaultName(config.Secrets.KeyVault),
                KubernetesManifestGenerator.KeyVaultPrefix(config.Secrets.KeyVault, kubernetesConfig)),
            _ => throw new NotSupportedException($"Kubernetes has no {config.Secrets.Backend} secret store.")
        };
    }

    /// <summary>Namespace holding the generated-parameters Secret.</summary>
    public static string SecretsNamespace(KubernetesTargetConfig cfg) => $"{cfg.NamespacePrefix}-system";

    /// <summary>
    /// Puts a service's secrets in their backend before its manifests are applied. Returns true
    /// when values changed in a way running pods won't pick up by themselves.
    /// </summary>
    internal async Task<bool> SyncSecretsAsync(
        KubernetesServiceManifest svc, EnvironmentConfig config, IKubernetesSecretClient cluster, CancellationToken ct)
    {
        if (!svc.HasSecret || svc.SecretBackend == SecretBackendType.Inline)
        {
            return false;
        }

        var changed = false;
        if (svc.UsesKeyVaultCsi)
        {
            // The CSI driver creates and updates the {service}-secrets Secret itself; Deployify
            // only puts the values in the vault.
            var keyVault = svc.KeyVault!;
            var client = KeyVaultClientFactory(keyVault);
            var vaultName = KeyVaultSecrets.VaultName(keyVault);
            var tags = new Dictionary<string, string>
            {
                [KeyVaultSecrets.StoreTag] = svc.KeyVaultPrefix!,
                [KeyVaultSecrets.AppTag] = svc.ServiceName
            };
            var data = svc.SecretData;
            foreach (var (id, name) in svc.KeyVaultObjectNames.OrderBy(o => o.Key, StringComparer.Ordinal))
            {
                var (version, written, existed) = await KeyVaultSecrets.SetIfChangedAsync(client, vaultName, name, data[id], tags, ct);
                changed |= written && existed;
                config.Secrets.References[$"{svc.ServiceName}/{id}"] = new SecretReference
                {
                    Backend = SecretBackendType.KeyVault,
                    Location = KeyVaultSecrets.SecretUrl(keyVault, name),
                    Key = id,
                    Version = version
                };
            }

            return changed;
        }

        if (svc.UsesSecretRefs)
        {
            var name = KubernetesManifestGenerator.SecretName(svc);
            var before = await cluster.GetResourceVersionAsync("secret", svc.Namespace, name, ct);
            await cluster.ServerSideApplyAsync(KubernetesSecretManifest.Namespace(svc.Namespace, new Dictionary<string, string>
            {
                ["app.kubernetes.io/managed-by"] = "deployify",
                ["deployify/service"] = svc.ServiceName
            }), ct);
            var after = await cluster.ServerSideApplyAsync(
                KubernetesSecretManifest.Build(svc.Namespace, name, svc.SecretData,
                    new Dictionary<string, string> { ["app"] = svc.ServiceName }), ct);
            changed = before is not null && before != after;
            foreach (var key in svc.SecretData.Keys)
            {
                config.Secrets.References[$"{svc.ServiceName}/{key}"] = new SecretReference
                {
                    Backend = SecretBackendType.KubernetesSecret,
                    Location = $"{svc.Namespace}/{name}",
                    Key = key,
                    Version = after
                };
            }
        }

        if (svc.UsesVaultAgent)
        {
            var vault = config.Secrets.Vault;
            using var client = VaultClientFactory(vault);
            var (version, written) = await client.WriteKvIfChangedAsync(svc.VaultMount, svc.VaultPath!, svc.SecretData, ct);
            // The agent re-renders static secrets on its own schedule; restart so the change is immediate.
            changed |= written && version > 1;
            if (vault.ManageAuth)
            {
                await client.WritePolicyAsync(svc.VaultRole!, VaultPolicy.ReadOnly(svc.VaultKvApiPath!), ct);
                await client.WriteKubernetesRoleAsync(
                    vault.AuthMount, svc.VaultRole!, svc.ServiceName, svc.Namespace, [svc.VaultRole!], ct);
            }

            foreach (var key in svc.SecretData.Keys)
            {
                config.Secrets.References[$"{svc.ServiceName}/{key}"] = new SecretReference
                {
                    Backend = SecretBackendType.Vault,
                    Location = svc.VaultKvApiPath!,
                    Key = key,
                    Version = version.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
            }
        }

        return changed;
    }

    public DeploymentTargetType TargetType => DeploymentTargetType.Kubernetes;
    public string DisplayName => "Kubernetes";

    public async Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        var kubernetesConfig = GetConfig(config);
        if (await KubectlAvailableAsync(ct))
        {
            return await Kubectl(kubernetesConfig).PingAsync(ct);
        }

        using var api = await KubernetesApiClient.CreateAsync(kubernetesConfig, logger, ct);
        return await api.PingAsync(ct)
            ? (true, null)
            : (false, "Cannot reach Kubernetes API. Check your kubeconfig.");
    }

    public async Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var kubernetesConfig = GetConfig(config);
        var services = _generator.Generate(manifest, config, kubernetesConfig);
        var paths = new List<string>();
        foreach (var svc in services)
        {
            paths.AddRange(await _generator.WriteYamlAsync(svc, outputDirectory, ct));
        }

        return paths;
    }

    public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var kubernetesConfig = GetConfig(config);
        var services = _generator.Generate(manifest, config, kubernetesConfig);
        var results = new List<ValidationResult>();

        if (await KubectlAvailableAsync(ct))
        {
            var kubectl = Kubectl(kubernetesConfig);
            foreach (var svc in services)
            {
                foreach (var file in await _generator.WriteYamlAsync(svc, outputDirectory, ct))
                {
                    results.Add(new ValidationResult { ArtifactName = file, Errors = await kubectl.ValidateFileAsync(file, ct) });
                }
            }
        }
        else
        {
            logger.LogWarning("kubectl not found - skipping server-side validation.");
            foreach (var svc in services)
            {
                foreach (var file in await _generator.WriteYamlAsync(svc, outputDirectory, ct))
                {
                    results.Add(new ValidationResult { ArtifactName = file, Errors = [] });
                }
            }
        }

        return results;
    }

    public async Task<PlanResult> PlanAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var kubernetesConfig = GetConfig(config);
        var services = _generator.Generate(manifest, config, kubernetesConfig);

        // Generate YAML artifacts to disk — apply will use these
        foreach (var svc in services)
        {
            await _generator.WriteYamlAsync(svc, outputDirectory, ct);
        }

        return PlanResult.FromDiff(await CompareWithClusterAsync(services, kubernetesConfig, outputDirectory, ct));
    }

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var kubernetesConfig = GetConfig(config);
        var services = _generator.Generate(manifest, config, kubernetesConfig);
        var results = new List<DeploymentResult>();
        var useKubectl = await KubectlAvailableAsync(ct);
        var kubectl = useKubectl ? Kubectl(kubernetesConfig) : null;

        foreach (var svc in services)
        {
            var svcDir = Path.Combine(outputDirectory, svc.ServiceName);
            if (!Directory.Exists(svcDir))
            {
                results.Add(new DeploymentResult
                {
                    ServiceName = svc.ServiceName, Success = false,
                    Error = $"Artifact directory not found: {svcDir}. Run 'deployify plan' first."
                });
                continue;
            }

            // Read files already written by plan — do not regenerate
            var files = Directory.GetFiles(svcDir, "*.yaml").ToList();
            try
            {
                if (kubectl is not null)
                {
                    // Secrets go to their backend first, so the pods find them when they start.
                    var secretsChanged = await SyncSecretsAsync(svc, config, kubectl, ct);
                    var (ok, output) = await kubectl.ApplyDirectoryAsync(svcDir, ct);
                    if (ok)
                    {
                        logger.LogInformation("[kubectl] {Name}: {Out}", svc.ServiceName, output.Trim());
                        if (secretsChanged && !await kubectl.RolloutRestartAsync(svc.ServiceName, svc.Namespace, ct))
                        {
                            logger.LogWarning("Secrets of {Name} changed but its pods could not be restarted.", svc.ServiceName);
                        }

                        results.Add(new DeploymentResult { ServiceName = svc.ServiceName, DeploymentId = $"ns/{svc.Namespace}", Success = true });
                        continue;
                    }

                    logger.LogWarning("kubectl failed for {Name}, trying API fallback.", svc.ServiceName);
                }

                results.Add(await ApplyViaApiAsync(svc, files, kubernetesConfig, config, syncSecrets: kubectl is null, ct));
            }
            catch (Exception ex)
            {
                results.Add(new DeploymentResult { ServiceName = svc.ServiceName, Success = false, Error = ex.Message });
            }
        }

        return results;
    }

    public async Task PublishAsync(AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default)
        => await publisher.PublishAsync(manifest, config, appHostDirectory, ct);

    public IReadOnlyList<ServiceExplanation> Explain(AspireManifest manifest, EnvironmentConfig config)
    {
        var kubernetesConfig = GetConfig(config);
        return
        [
            .. _generator.Generate(manifest, config, kubernetesConfig).Select(svc => new ServiceExplanation
            {
                Name = svc.ServiceName,
                Image = svc.Image,
                ImageTag = svc.ImageTag,
                Count = svc.Replicas,
                Env = ResolvedEnvironment.ForDisplay(svc.Env, svc.SecretEnv),
                Ports = [.. svc.Ports.Select(p => $"{p.Name}:{p.ContainerPort}/{p.Protocol}")],
                Dependencies = svc.Dependencies,
                AdditionalInfo = new Dictionary<string, string>
                {
                    ["namespace"] = svc.Namespace,
                    ["ingress"] = svc.HasIngress ? svc.IngressHost ?? "yes" : "no",
                    ["cpu"] = $"req:{svc.CpuRequest} limit:{svc.CpuLimit}",
                    ["memory"] = $"req:{svc.MemoryRequest} limit:{svc.MemoryLimit}",
                    ["configmap"] = svc.HasConfigMap ? "yes" : "no",
                    ["secret"] = !svc.HasSecret ? "no"
                        : svc.UsesVaultAgent ? $"vault: {svc.VaultKvApiPath}"
                        : svc.UsesKeyVaultCsi ? $"key vault {KeyVaultSecrets.VaultName(svc.KeyVault!)} via {KubernetesManifestGenerator.SecretProviderClassName(svc)}"
                        : svc.UsesSecretRefs ? $"{KubernetesManifestGenerator.SecretName(svc)} (applied by apply, not written to disk)"
                        : KubernetesManifestGenerator.SecretName(svc),
                    ["pvc"] = svc.HasPvc ? svc.PvcStorageSize ?? "1Gi" : "no"
                }
            })
        ];
    }

    public IReadOnlyList<ServiceDependency> GetDependencies(AspireManifest manifest, EnvironmentConfig config)
    {
        var kubernetesConfig = GetConfig(config);
        return
        [
            .. _generator.Generate(manifest, config, kubernetesConfig)
                .SelectMany(svc => svc.Dependencies.Select(dep => new ServiceDependency { From = dep, To = svc.ServiceName }))
        ];
    }

    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var kubernetesConfig = GetConfig(config);
        var services = _generator.Generate(manifest, config, kubernetesConfig);
        foreach (var svc in services)
        {
            await _generator.WriteYamlAsync(svc, outputDirectory, ct);
        }

        return await CompareWithClusterAsync(services, kubernetesConfig, outputDirectory, ct);
    }

    /// <summary>
    /// Compares every service's generated YAML with the cluster; plan and diff both use this,
    /// so they always agree. A service whose namespace is missing is Added, one kubectl diff
    /// finds identical is Unchanged, one it finds different is Changed. When the comparison
    /// itself fails (kubectl diff errors, or only the API fallback is available) the service
    /// is Unknown, with the reason, rather than guessed. An unreachable cluster is treated as
    /// empty, with a warning, as the other targets do.
    /// </summary>
    private async Task<List<DiffEntry>> CompareWithClusterAsync(
        IReadOnlyList<KubernetesServiceManifest> services, KubernetesTargetConfig kubernetesConfig,
        string outputDirectory, CancellationToken ct)
    {
        var kubectl = await KubectlAvailableAsync(ct) ? Kubectl(kubernetesConfig) : null;

        IReadOnlyList<string> existingNs;
        try
        {
            if (kubectl is not null)
            {
                existingNs = await kubectl.GetNamespacesWithPrefixAsync(kubernetesConfig.NamespacePrefix, ct);
            }
            else
            {
                using var api = await KubernetesApiClient.CreateAsync(kubernetesConfig, logger, ct);
                existingNs = await api.GetNamespacesWithPrefixAsync(kubernetesConfig.NamespacePrefix, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not reach the cluster — assuming all services are new. ({Message})", ex.Message);
            existingNs = [];
        }

        var existing = existingNs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = new List<DiffEntry>();
        foreach (var svc in services)
        {
            if (!existing.Contains(svc.Namespace))
            {
                entries.Add(new DiffEntry { ServiceName = svc.ServiceName, Kind = DiffKind.Added });
                continue;
            }

            if (kubectl is null)
            {
                entries.Add(new DiffEntry
                {
                    ServiceName = svc.ServiceName, Kind = DiffKind.Unknown,
                    Reason = "kubectl is not installed, and the API fallback can't diff manifests"
                });
                continue;
            }

            var diff = await kubectl.DiffFileAsync(Path.Combine(outputDirectory, svc.ServiceName), ct);
            entries.Add(diff.Kind switch
            {
                KubectlDiffKind.Same => new DiffEntry { ServiceName = svc.ServiceName, Kind = DiffKind.Unchanged },
                KubectlDiffKind.Different => new DiffEntry
                {
                    ServiceName = svc.ServiceName, Kind = DiffKind.Changed, Field = "manifest", NewValue = diff.Text
                },
                _ => new DiffEntry { ServiceName = svc.ServiceName, Kind = DiffKind.Unknown, Reason = diff.Text }
            });
        }

        var desiredNs = services.Select(s => s.Namespace).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prefix = kubernetesConfig.NamespacePrefix + "-";
        foreach (var ns in existingNs.Where(ns => !desiredNs.Contains(ns)))
        {
            entries.Add(new DiffEntry
            {
                ServiceName = ns.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? ns[prefix.Length..] : ns,
                Kind = DiffKind.Removed
            });
        }

        return entries;
    }

    // ── Rollout (health-gated apply) ──────────────────────────────────────────

    private const string NamespaceKey = "namespace";
    private const string RolloutRevisionKey = "rolloutRevision";
    private const string ReplicasKey = "replicas";

    private async Task<KubectlRunner> RequireKubectlAsync(KubernetesTargetConfig cfg, CancellationToken ct)
        => await KubectlAvailableAsync(ct)
            ? Kubectl(cfg)
            : throw new InvalidOperationException(
                "Health-gated rollouts on Kubernetes need kubectl on PATH. Install it, or deploy with --no-health-gate.");

    private KubernetesServiceManifest FindService(AspireManifest manifest, EnvironmentConfig config, KubernetesTargetConfig cfg, string service)
        => _generator.Generate(manifest, config, cfg).FirstOrDefault(s => s.ServiceName == service)
           ?? throw new InvalidOperationException($"'{service}' is not a Kubernetes service in this manifest.");

    /// <summary>
    /// Records the Deployment's generation, rollout revision and replica count (in the journal),
    /// and its ConfigMap and Secret (in memory only).
    /// </summary>
    public async Task<ServiceSnapshot> CaptureAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var svc = FindService(manifest, config, cfg, service);
        var kubectl = await RequireKubectlAsync(cfg, ct);

        var json = await kubectl.GetDeploymentJsonAsync(service, svc.Namespace, ct);

        var configMap = await kubectl.GetConfigMapDataAsync(svc.Namespace, $"{service}-config", ct);
        IReadOnlyDictionary<string, SensitiveValue>? secret = null;
        var secretCaptured = false;
        if (svc.HasSecret && !svc.UsesKeyVaultCsi)
        {
            try
            {
                secret = await kubectl.GetSecretDataAsync(svc.Namespace, KubernetesManifestGenerator.SecretName(svc), ct);
                secretCaptured = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Could not read the Secret of {Name} ({Message}); a rollback will not restore it.",
                    service, ex.Message);
            }
        }

        _configBefore[$"{svc.Namespace}/{service}"] = new CapturedConfig(configMap, secretCaptured, secret);

        if (json is null)
        {
            return new ServiceSnapshot
            {
                ServiceName = service, Existed = false, Data = new Dictionary<string, string> { [NamespaceKey] = svc.Namespace }
            };
        }

        var status = KubernetesDeploymentStatus.Parse(json);
        var data = new Dictionary<string, string>
        {
            [NamespaceKey] = svc.Namespace,
            [ReplicasKey] = status.SpecReplicas.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (status.RolloutRevision is { } rev)
        {
            data[RolloutRevisionKey] = rev.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await BackUpConfigAsync(kubectl, svc.Namespace, service, status.Generation, configMap, secretCaptured, secret, data, ct);

        return new ServiceSnapshot
        {
            ServiceName = service,
            Existed = true,
            Revision = status.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Data = data
        };
    }

    public async Task<AppliedRevision> ApplyServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var svc = FindService(manifest, config, cfg, service);
        var kubectl = await RequireKubectlAsync(cfg, ct);

        var svcDir = Path.Combine(outputDirectory, service);
        if (!Directory.Exists(svcDir))
        {
            return new AppliedRevision
            {
                ServiceName = service, Success = false,
                Error = $"Artifact directory not found: {svcDir}. Run 'deployify plan' first."
            };
        }

        try
        {
            var secretsChanged = await SyncSecretsAsync(svc, config, kubectl, ct);
            var (ok, output) = await kubectl.ApplyDirectoryAsync(svcDir, ct);
            if (!ok)
            {
                return new AppliedRevision { ServiceName = service, Success = false, Error = output.Trim() };
            }

            if (secretsChanged && !await kubectl.RolloutRestartAsync(service, svc.Namespace, ct))
            {
                logger.LogWarning("Secrets of {Name} changed but its pods could not be restarted.", service);
            }

            // Read back after the restart, which bumps the generation once more.
            var json = await kubectl.GetDeploymentJsonAsync(service, svc.Namespace, ct)
                       ?? throw new InvalidOperationException($"Deployment {svc.Namespace}/{service} is missing after apply.");
            return new AppliedRevision
            {
                ServiceName = service,
                Success = true,
                Revision = KubernetesDeploymentStatus.Parse(json).Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AppliedRevision { ServiceName = service, Success = false, Error = ex.Message };
        }
    }

    public async Task<HealthObservation> ObserveAsync(
        EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var kubectl = await RequireKubectlAsync(cfg, ct);
        var json = await kubectl.GetDeploymentJsonAsync(service, KubernetesManifestGenerator.NamespaceFor(cfg, service), ct);
        return KubernetesRolloutHealth.Evaluate(
            json is null ? null : KubernetesDeploymentStatus.Parse(json),
            long.Parse(revision, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Puts the ConfigMap and Secret back first (so the restored pods read the old values), then
    /// rolls the pod template back with <c>kubectl rollout undo</c> and restores the replica
    /// count. A Deployment this rollout created is scaled to zero.
    /// </summary>
    public async Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var kubectl = await RequireKubectlAsync(cfg, ct);
        var service = snapshot.ServiceName;
        var ns = snapshot.Data.TryGetValue(NamespaceKey, out var n) ? n : KubernetesManifestGenerator.NamespaceFor(cfg, service);

        await RestoreConfigAsync(kubectl, ns, snapshot, ct);

        var json = await kubectl.GetDeploymentJsonAsync(service, ns, ct);
        if (snapshot is not { Existed: true, Revision: { } revision })
        {
            if (json is not null)
            {
                await kubectl.ScaleDeploymentAsync(service, ns, 0, ct);
                logger.LogInformation("Scaled new deployment {Namespace}/{Name} to zero.", ns, service);
            }

            return null;
        }

        if (json is null)
        {
            throw new InvalidOperationException($"Deployment {ns}/{service} no longer exists, so it can't be rolled back.");
        }

        var current = KubernetesDeploymentStatus.Parse(json);
        if (current.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) == revision)
        {
            return revision; // the rollout never changed the deployment
        }

        if (snapshot.Data.TryGetValue(RolloutRevisionKey, out var raw) &&
            long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var toRevision) &&
            current.RolloutRevision != toRevision)
        {
            await kubectl.RolloutUndoAsync(service, ns, toRevision, ct);
        }

        if (snapshot.Data.TryGetValue(ReplicasKey, out var rawReplicas) &&
            int.TryParse(rawReplicas, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var replicas) &&
            replicas != current.SpecReplicas)
        {
            await kubectl.ScaleDeploymentAsync(service, ns, replicas, ct);
        }

        var after = await kubectl.GetDeploymentJsonAsync(service, ns, ct)
                    ?? throw new InvalidOperationException($"Deployment {ns}/{service} disappeared during the rollback.");
        var generation = KubernetesDeploymentStatus.Parse(after).Generation;
        logger.LogInformation("Rolled {Namespace}/{Name} back (generation {Generation}).", ns, service, generation);
        return generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private const string ConfigBackupKey = "configBackup";
    private const string SecretBackupKey = "secretBackup";
    private const string RollbackOfLabel = "deployify/rollback-of";

    /// <summary>Copies kept per service and kind; older ones are deleted when a new one is written.</summary>
    internal const int BackupsKept = 5;

    /// <summary>
    /// Keeps a copy of the ConfigMap and Secret in the service's own namespace
    /// (<c>{service}-config-rollback-g{generation}</c>, <c>{service}-secrets-rollback-g{generation}</c>),
    /// so a rollback in a later process can restore them. The journal records only their names.
    /// A copy that can't be written is skipped with a warning.
    /// </summary>
    private async Task BackUpConfigAsync(
        KubectlRunner kubectl, string ns, string service, long generation,
        Dictionary<string, string>? configMap, bool secretCaptured, IReadOnlyDictionary<string, SensitiveValue>? secret,
        Dictionary<string, string> data, CancellationToken ct)
    {
        var gen = generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var labels = new Dictionary<string, string> { [RollbackOfLabel] = service, ["app.kubernetes.io/managed-by"] = "deployify" };
        try
        {
            if (configMap is not null)
            {
                var name = $"{service}-config-rollback-g{gen}";
                await kubectl.ServerSideApplyAsync(KubernetesRolloutHealth.ConfigMapJson(ns, name, configMap, labels), ct);
                data[ConfigBackupKey] = name;
                await PruneAsync(kubectl, "configmap", ns, service, ct);
            }

            if (secretCaptured && secret is not null)
            {
                var name = $"{service}-secrets-rollback-g{gen}";
                await kubectl.ServerSideApplyAsync(KubernetesSecretManifest.Build(ns, name, secret, labels), ct);
                data[SecretBackupKey] = name;
                await PruneAsync(kubectl, "secret", ns, service, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not keep a copy of the config of {Name} ({Message}); only a rollback in this run " +
                              "will restore it.", service, ex.Message);
        }
    }

    private static async Task PruneAsync(KubectlRunner kubectl, string kind, string ns, string service, CancellationToken ct)
    {
        var names = await kubectl.ListNamesAsync(kind, ns, $"{RollbackOfLabel}={service}", ct);
        foreach (var old in names
                     .OrderByDescending(n => long.TryParse(n[(n.LastIndexOf("-g", StringComparison.Ordinal) + 2)..],
                         System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : -1)
                     .Skip(BackupsKept))
        {
            await kubectl.DeleteAsync(kind, ns, old, ct);
        }
    }

    private async Task RestoreConfigAsync(
        KubectlRunner kubectl, string ns, ServiceSnapshot snapshot, CancellationToken ct)
    {
        var service = snapshot.ServiceName;
        if (!_configBefore.TryGetValue($"{ns}/{service}", out var before))
        {
            // A rollback from a later process: read the copies kept in the cluster.
            Dictionary<string, string>? configMap = null;
            IReadOnlyDictionary<string, SensitiveValue>? secret = null;
            if (snapshot.Data.TryGetValue(ConfigBackupKey, out var configBackup))
            {
                configMap = await kubectl.GetConfigMapDataAsync(ns, configBackup, ct)
                            ?? throw new InvalidOperationException($"The copy {ns}/{configBackup} is gone, so the ConfigMap can't be restored.");
            }

            if (snapshot.Data.TryGetValue(SecretBackupKey, out var secretBackup))
            {
                secret = await kubectl.GetSecretDataAsync(ns, secretBackup, ct)
                         ?? throw new InvalidOperationException($"The copy {ns}/{secretBackup} is gone, so the Secret can't be restored.");
            }

            if (configMap is null && secret is null)
            {
                return; // nothing was captured
            }

            before = new CapturedConfig(configMap, secret is not null, secret);
        }

        var configMapName = $"{service}-config";
        if (before.ConfigMap is { } oldData)
        {
            var now = await kubectl.GetConfigMapDataAsync(ns, configMapName, ct);
            if (now is null || !SameData(oldData, now))
            {
                await kubectl.ServerSideApplyAsync(KubernetesRolloutHealth.ConfigMapJson(ns, configMapName, oldData), ct);
                logger.LogInformation("Restored ConfigMap {Namespace}/{Name}.", ns, configMapName);
            }
        }

        if (before is { SecretCaptured: true, Secret: { } oldSecret })
        {
            var secretName = $"{service}-secrets";
            var now = await kubectl.GetSecretDataAsync(ns, secretName, ct);
            if (now is null || !SameSecret(oldSecret, now))
            {
                await kubectl.ServerSideApplyAsync(KubernetesSecretManifest.Build(ns, secretName, oldSecret,
                    new Dictionary<string, string> { ["app"] = service }), ct);
                logger.LogInformation("Restored Secret {Namespace}/{Name}.", ns, secretName);
            }
        }
    }

    private static bool SameData(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static bool SameSecret(IReadOnlyDictionary<string, SensitiveValue> a, IReadOnlyDictionary<string, SensitiveValue> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v.Reveal() == kv.Value.Reveal());

    public bool HasNativeRollback(EnvironmentConfig config) => false;

    public async Task<string?> GetUnavailableReasonAsync(EnvironmentConfig config, CancellationToken ct = default)
        => await KubectlAvailableAsync(ct) ? null : "kubectl is not installed (the API fallback can't watch rollouts)";

    private async Task<DeploymentResult> ApplyViaApiAsync(
        KubernetesServiceManifest svc, IEnumerable<string> files, KubernetesTargetConfig kubernetesConfig,
        EnvironmentConfig config, bool syncSecrets, CancellationToken ct)
    {
        using var api = await KubernetesApiClient.CreateAsync(kubernetesConfig, logger, ct);
        if (syncSecrets && await SyncSecretsAsync(svc, config, api, ct))
        {
            logger.LogWarning("Secrets of {Name} changed. Restart its pods to pick them up: kubectl rollout restart " +
                              "deployment/{Name} -n {Namespace}", svc.ServiceName, svc.ServiceName, svc.Namespace);
        }

        foreach (var file in files)
        {
            var yaml = await File.ReadAllTextAsync(file, ct);
            var (kind, apiVersion, name, ns) = ExtractMeta(yaml);
            var (ok, error) = await api.ApplyAsync(kind, apiVersion, ns ?? svc.Namespace, name, yaml, ct);
            if (!ok)
            {
                return new DeploymentResult { ServiceName = svc.ServiceName, Success = false, Error = error };
            }
        }

        return new DeploymentResult { ServiceName = svc.ServiceName, DeploymentId = $"ns/{svc.Namespace}", Success = true };
    }

    private static (string Kind, string ApiVersion, string Name, string? Ns) ExtractMeta(string yaml)
    {
        var lines = yaml.Split('\n');
        string Get(string key) => lines.FirstOrDefault(l => l.StartsWith(key + ":"))?.Split(':', 2).Last().Trim() ?? "";
        string GetInMeta(string key) => lines.SkipWhile(l => !l.TrimStart().StartsWith(key + ":")).FirstOrDefault()?.Split(':', 2).Last().Trim() ?? "";
        return (Get("kind"), Get("apiVersion"), GetInMeta("name"), GetInMeta("namespace") is "" or null ? null : GetInMeta("namespace"));
    }

    private static KubernetesTargetConfig GetConfig(EnvironmentConfig config)
    {
        if (config.TargetConfig.TryGetValue("kubernetes", out var raw) && raw is JsonElement el)
        {
            return el.Deserialize<KubernetesTargetConfig>(TargetConfigJsonOptions.Default) ?? new KubernetesTargetConfig();
        }

        return new KubernetesTargetConfig();
    }
}
