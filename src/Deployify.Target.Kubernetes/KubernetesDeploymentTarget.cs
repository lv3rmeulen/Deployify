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

namespace Deployify.Target.Kubernetes;

public sealed class KubernetesDeploymentTarget(
    IPublisher publisher,
    ILogger<KubernetesDeploymentTarget> logger) : IDeploymentTarget
{
    private readonly KubernetesManifestGenerator _generator = new();

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
                    var (ok, output) = await kubectl.ApplyDirectoryAsync(svcDir, ct);
                    if (ok)
                    {
                        logger.LogInformation("[kubectl] {Name}: {Out}", svc.ServiceName, output.Trim());
                        results.Add(new DeploymentResult { ServiceName = svc.ServiceName, DeploymentId = $"ns/{svc.Namespace}", Success = true });
                        continue;
                    }

                    logger.LogWarning("kubectl failed for {Name}, trying API fallback.", svc.ServiceName);
                }

                results.Add(await ApplyViaApiAsync(svc, files, kubernetesConfig, ct));
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
                    ["secret"] = svc.HasSecret ? KubernetesManifestGenerator.SecretName(svc) : "no",
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

    private async Task<DeploymentResult> ApplyViaApiAsync(KubernetesServiceManifest svc, IEnumerable<string> files, KubernetesTargetConfig kubernetesConfig, CancellationToken ct)
    {
        using var api = await KubernetesApiClient.CreateAsync(kubernetesConfig, logger, ct);
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
