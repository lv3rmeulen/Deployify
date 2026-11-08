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
using Deployify.Core.SecretBackends;
using Deployify.Core.SecretBackends.KeyVault;
using Deployify.Core.SecretBackends.Vault;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Kubernetes;

public sealed class KubernetesDeploymentTarget(
    IPublisher publisher,
    ILogger<KubernetesDeploymentTarget> logger) : IDeploymentTarget, ISecretStoreProvider
{
    private readonly KubernetesManifestGenerator _generator = new();

    /// <summary>Creates Vault clients; replaceable in tests.</summary>
    internal Func<VaultConfig, VaultClient> VaultClientFactory { get; init; } = c => VaultClient.FromConfig(c);

    /// <summary>Creates Key Vault clients; replaceable in tests.</summary>
    internal Func<KeyVaultConfig, IKeyVaultClient> KeyVaultClientFactory { get; init; } = c => new AzureCliKeyVaultClient(c.SubscriptionId);

    // ── Secrets ───────────────────────────────────────────────────────────────

    public IReadOnlyCollection<SecretBackendType> SupportedSecretBackends { get; } =
        [SecretBackendType.KubernetesSecret, SecretBackendType.Vault, SecretBackendType.KeyVault];

    public ISecretStore CreateSecretStore(EnvironmentConfig config, ILogger storeLogger)
    {
        var kubernetesConfig = GetConfig(config);
        return config.Secrets.Backend switch
        {
            SecretBackendType.KubernetesSecret => new KubernetesSecretStore(
                new KubectlRunner(kubernetesConfig, storeLogger), SecretsNamespace(kubernetesConfig)),
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
        if (await KubectlRunner.IsAvailableAsync(ct))
        {
            return await new KubectlRunner(kubernetesConfig, logger).PingAsync(ct);
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

        if (await KubectlRunner.IsAvailableAsync(ct))
        {
            var kubectl = new KubectlRunner(kubernetesConfig, logger);
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

        var useKubectl = await KubectlRunner.IsAvailableAsync(ct);
        var kubectl = useKubectl ? new KubectlRunner(kubernetesConfig, logger) : null;

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
        catch (Exception ex)
        {
            logger.LogWarning("Could not reach the cluster — assuming all services are new. ({Message})", ex.Message);
            existingNs = [];
        }

        var existing = existingNs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new PlanResult();
        foreach (var svc in services)
        {
            if (!existing.Contains(svc.Namespace))
            {
                result.ToCreate.Add(svc.ServiceName);
                continue;
            }

            // kubectl diff tells us whether the live objects already match.
            var diff = kubectl is null ? null
                : await kubectl.DiffFileAsync(Path.Combine(outputDirectory, svc.ServiceName), ct);
            if (diff is "")
            {
                result.Unchanged.Add(svc.ServiceName);
            }
            else
            {
                result.ToUpdate.Add(svc.ServiceName);
            }
        }

        var desiredNs = services.Select(s => s.Namespace).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var ns in existing.Where(ns => !desiredNs.Contains(ns)))
        {
            result.ToDelete.Add(ns[(kubernetesConfig.NamespacePrefix.Length + 1)..]);
        }

        return result;
    }

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var kubernetesConfig = GetConfig(config);
        var services = _generator.Generate(manifest, config, kubernetesConfig);
        var results = new List<DeploymentResult>();
        var useKubectl = await KubectlRunner.IsAvailableAsync(ct);
        var kubectl = useKubectl ? new KubectlRunner(kubernetesConfig, logger) : null;

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
        var entries = new List<DiffEntry>();

        if (await KubectlRunner.IsAvailableAsync(ct))
        {
            var kubectl = new KubectlRunner(kubernetesConfig, logger);
            var existingNs = await kubectl.GetNamespacesWithPrefixAsync(kubernetesConfig.NamespacePrefix, ct);
            var existingSet = existingNs.ToHashSet();

            foreach (var svc in services)
            {
                if (!existingSet.Contains(svc.Namespace))
                {
                    entries.Add(new DiffEntry { ServiceName = svc.ServiceName, Kind = DiffKind.Added });
                    continue;
                }

                await _generator.WriteYamlAsync(svc, outputDirectory, ct);
                var diff = await kubectl.DiffFileAsync(Path.Combine(outputDirectory, svc.ServiceName), ct);
                var hasDiff = diff is null || diff.Length > 0;
                entries.Add(new DiffEntry { ServiceName = svc.ServiceName, Kind = hasDiff ? DiffKind.Changed : DiffKind.Unchanged, Field = hasDiff ? "manifest" : null });
            }

            var desiredNs = services.Select(s => s.Namespace).ToHashSet();
            foreach (var ns in existingNs.Where(ns => !desiredNs.Contains(ns)))
            {
                entries.Add(new DiffEntry
                {
                    ServiceName = ns.StartsWith(kubernetesConfig.NamespacePrefix + "-") ? ns[(kubernetesConfig.NamespacePrefix.Length + 1)..] : ns,
                    Kind = DiffKind.Removed
                });
            }
        }
        else
        {
            using var api = await KubernetesApiClient.CreateAsync(kubernetesConfig, logger, ct);
            var existingNs = (await api.GetNamespacesWithPrefixAsync(kubernetesConfig.NamespacePrefix, ct)).ToHashSet();
            foreach (var svc in services)
            {
                entries.Add(new DiffEntry { ServiceName = svc.ServiceName, Kind = existingNs.Contains(svc.Namespace) ? DiffKind.Unchanged : DiffKind.Added });
            }
        }

        return entries;
    }

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
