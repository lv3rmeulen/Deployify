using System.Text.Json;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Kubernetes;

public sealed class KubernetesDeploymentTarget(
    IPublisher publisher,
    ILogger<KubernetesDeploymentTarget> logger) : IDeploymentTarget
{
    private readonly KubernetesManifestGenerator _generator = new KubernetesManifestGenerator();

    public DeploymentTargetType TargetType => DeploymentTargetType.Kubernetes;
    public string DisplayName => "Kubernetes";

    public async Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        var k8S = GetConfig(config);
        if (await KubectlRunner.IsAvailableAsync())
        {
	        return await new KubectlRunner(k8S, logger).PingAsync();
        }

        using var api = new KubernetesApiClient(k8S, logger);
        return await api.PingAsync(ct)
            ? (true, null)
            : (false, "Cannot reach Kubernetes API. Check your kubeconfig.");
    }

    public async Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var k8S = GetConfig(config);
        var services = _generator.Generate(manifest, config, k8S);
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
        var k8S = GetConfig(config);
        var services = _generator.Generate(manifest, config, k8S);
        var results = new List<ValidationResult>();

        if (await KubectlRunner.IsAvailableAsync())
        {
            var kubectl = new KubectlRunner(k8S, logger);
            foreach (var svc in services)
            {
	            foreach (var file in await _generator.WriteYamlAsync(svc, outputDirectory, ct))
	            {
		            results.Add(new ValidationResult { ArtefactName = file, Errors = await kubectl.ValidateFileAsync(file) });
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
		            results.Add(new ValidationResult { ArtefactName = file, Errors = [] });
	            }
            }
        }

        return results;
    }

    public async Task<PlanResult> PlanAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var k8S = GetConfig(config);
        var services = _generator.Generate(manifest, config, k8S);

        // Generate YAML artefacts to disk — apply will use these
        foreach (var svc in services)
        {
	        await _generator.WriteYamlAsync(svc, outputDirectory, ct);
        }

        var desired = services.Select(s => s.ServiceName).ToHashSet();

        IReadOnlyList<string> existingNs;
        if (await KubectlRunner.IsAvailableAsync())
        {
	        existingNs = await new KubectlRunner(k8S, logger).GetNamespacesWithPrefixAsync(k8S.NamespacePrefix);
        }
        else
        {
            using var api = new KubernetesApiClient(k8S, logger);
            existingNs = await api.GetNamespacesWithPrefixAsync(k8S.NamespacePrefix, ct);
        }

        var running = existingNs
            .Select(ns => ns.StartsWith(k8S.NamespacePrefix + "-") ? ns[(k8S.NamespacePrefix.Length + 1)..] : ns)
            .ToHashSet();

        return new PlanResult
        {
            ToCreate = desired.Except(running).Select(n => (object)n).ToList(),
            ToUpdate = desired.Intersect(running).Select(n => (object)n).ToList(),
            ToDelete = running.Except(desired).ToList(),
            Unchanged = []
        };
    }

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var k8S = GetConfig(config);
        var services = _generator.Generate(manifest, config, k8S);
        var results = new List<DeploymentResult>();
        var useKubectl = await KubectlRunner.IsAvailableAsync();
        var kubectl = useKubectl ? new KubectlRunner(k8S, logger) : null;

        foreach (var svc in services)
        {
            var svcDir = Path.Combine(outputDirectory, svc.ServiceName);
            if (!Directory.Exists(svcDir))
            {
                results.Add(new DeploymentResult
                {
                    ServiceName = svc.ServiceName, Success = false,
                    Error = $"Artefact directory not found: {svcDir}. Run 'deployify plan' first."
                });
                continue;
            }

            // Read files already written by plan — do not regenerate
            var files = Directory.GetFiles(svcDir, "*.yaml").ToList();
            try
            {
                if (kubectl is not null)
                {
                    var (ok, output) = await kubectl.ApplyDirectoryAsync(svcDir);
                    if (ok)
                    {
                        logger.LogInformation("[kubectl] {Name}: {Out}", svc.ServiceName, output.Trim());
                        results.Add(new DeploymentResult { ServiceName = svc.ServiceName, DeploymentId = $"ns/{svc.Namespace}", Success = true });
                        continue;
                    }

                    logger.LogWarning("kubectl failed for {Name}, trying API fallback.", svc.ServiceName);
                }
                
                results.Add(await ApplyViaApiAsync(svc, files, k8S, ct));
            }
            catch (Exception ex)
            {
                results.Add(new DeploymentResult { ServiceName = svc.ServiceName, Success = false, Error = ex.Message });
            }
        }

        return results;
    }

    public async Task PublishAsync(AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default) =>
        await publisher.PublishAsync(manifest, config, appHostDirectory, ct);

    public IReadOnlyList<ServiceExplanation> Explain(AspireManifest manifest, EnvironmentConfig config)
    {
        var k8S = GetConfig(config);
        return _generator.Generate(manifest, config, k8S).Select(svc => new ServiceExplanation
        {
            Name = svc.ServiceName,
            Image = svc.Image,
            ImageTag = svc.ImageTag,
            Count = svc.Replicas,
            Env = svc.Env,
            Ports = svc.Ports.Select(p => $"{p.Name}:{p.ContainerPort}/{p.Protocol}").ToList(),
            Dependencies = svc.Dependencies,
            AdditionalInfo = new Dictionary<string, string>
            {
                ["namespace"] = svc.Namespace,
                ["ingress"] = svc.HasIngress ? svc.IngressHost ?? "yes" : "no",
                ["cpu"] = $"req:{svc.CpuRequest} limit:{svc.CpuLimit}",
                ["memory"] = $"req:{svc.MemoryRequest} limit:{svc.MemoryLimit}",
                ["configmap"] = svc.HasConfigMap ? "yes" : "no",
                ["pvc"] = svc.HasPvc ? svc.PvcStorageSize ?? "1Gi" : "no"
            }
        }).ToList();
    }

    public IReadOnlyList<ServiceDependency> GetDependencies(AspireManifest manifest, EnvironmentConfig config)
    {
        var k8S = GetConfig(config);
        return _generator.Generate(manifest, config, k8S)
            .SelectMany(svc => svc.Dependencies.Select(dep => new ServiceDependency { From = dep, To = svc.ServiceName }))
            .ToList();
    }

    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var k8S = GetConfig(config);
        var services = _generator.Generate(manifest, config, k8S);
        var entries = new List<DiffEntry>();

        if (await KubectlRunner.IsAvailableAsync())
        {
            var kubectl = new KubectlRunner(k8S, logger);
            var existingNs = await kubectl.GetNamespacesWithPrefixAsync(k8S.NamespacePrefix);
            var existingSet = existingNs.ToHashSet();

            foreach (var svc in services)
            {
                if (!existingSet.Contains(svc.Namespace))
                {
                    entries.Add(new DiffEntry { ServiceName = svc.ServiceName, Kind = DiffKind.Added });
                    continue;
                }

                var hasDiff = false;
                foreach (var file in await _generator.WriteYamlAsync(svc, outputDirectory, ct))
                {
                    var diff = await kubectl.DiffFileAsync(file);
                    if (!string.IsNullOrWhiteSpace(diff)) { hasDiff = true; break; }
                }

                entries.Add(new DiffEntry { ServiceName = svc.ServiceName, Kind = hasDiff ? DiffKind.Changed : DiffKind.Unchanged, Field = hasDiff ? "manifest" : null });
            }

            var desiredNs = services.Select(s => s.Namespace).ToHashSet();
            foreach (var ns in existingNs.Where(ns => !desiredNs.Contains(ns)))
            {
	            entries.Add(new DiffEntry
	            {
		            ServiceName = ns.StartsWith(k8S.NamespacePrefix + "-") ? ns[(k8S.NamespacePrefix.Length + 1)..] : ns,
		            Kind = DiffKind.Removed
	            });
            }
        }
        else
        {
            using var api = new KubernetesApiClient(k8S, logger);
            var existingNs = (await api.GetNamespacesWithPrefixAsync(k8S.NamespacePrefix, ct)).ToHashSet();
            foreach (var svc in services)
            {
	            entries.Add(new DiffEntry { ServiceName = svc.ServiceName, Kind = existingNs.Contains(svc.Namespace) ? DiffKind.Unchanged : DiffKind.Added });
            }
        }

        return entries;
    }

    private async Task<DeploymentResult> ApplyViaApiAsync(KubernetesServiceManifest svc, IEnumerable<string> files, KubernetesTargetConfig k8S, CancellationToken ct)
    {
        using var api = new KubernetesApiClient(k8S, logger);
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
