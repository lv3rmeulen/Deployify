using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.ResourceSources;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Kubernetes;

/// <summary>
/// The resources Deployify deployed to a Kubernetes cluster, for the Aspire dashboard: every
/// service's Deployment with its pods, read through kubectl (with the environment's kubeconfig
/// and context). Logs come from <c>kubectl logs</c>; start/stop scale the Deployment, restart
/// rolls it (or deletes one pod).
/// </summary>
public sealed class KubernetesResourceSource : PollingResourceSource
{
    private readonly KubernetesTargetConfig _config;
    private readonly KubectlRunner _kubectl;
    private IReadOnlyDictionary<string, KubernetesResourceLocation> _locations =
        new Dictionary<string, KubernetesResourceLocation>(StringComparer.Ordinal);

    internal KubernetesResourceSource(
        KubernetesTargetConfig config, KubectlRunner kubectl, ResourceSourceOptions options, ILogger logger)
        : base(options, logger)
    {
        _config = config;
        _kubectl = kubectl;
    }

    public override string DisplayName => $"Kubernetes ({_config.Context ?? "current context"}, {_config.NamespacePrefix}-*)";

    protected override async Task<IReadOnlyList<ResourceSnapshot>> GetResourcesAsync(CancellationToken ct)
    {
        var managed = await _kubectl.GetManagedObjectsJsonAsync(ct);
        var pods = await _kubectl.GetAppPodsJsonAsync(ct);
        var (resources, locations) = KubernetesResourceMapper.Map(managed, pods, _config, Options);
        _locations = locations;
        return resources;
    }

    public override async IAsyncEnumerable<ResourceLogLine> WatchLogsAsync(
        string resourceName, bool follow, [EnumeratorCancellation] CancellationToken ct)
    {
        var location = Locate(resourceName);
        await foreach (var line in _kubectl.StreamLogsAsync(
                           location.Namespace, location.Pod, location.Pod is null ? location.Service : null,
                           follow, Options.LogTailLines, ct))
        {
            yield return new ResourceLogLine(line.Text, line.IsStdErr);
        }
    }

    public override async Task<ResourceCommandResult> ExecuteCommandAsync(string resourceName, string commandName, CancellationToken ct)
    {
        var location = Locate(resourceName);
        try
        {
            if (location.Pod is { } pod)
            {
                if (commandName != ResourceCommandNames.Restart)
                {
                    return ResourceCommandResult.Failed($"Pods have no '{commandName}' command.");
                }

                await _kubectl.DeleteAsync("pod", location.Namespace, pod, ct);
                return ResourceCommandResult.Succeeded($"Deleted pod {pod}.");
            }

            var ns = location.Namespace;
            var service = location.Service;
            switch (commandName)
            {
                case ResourceCommandNames.Restart:
                    if (!await _kubectl.RolloutRestartAsync(service, ns, ct))
                    {
                        return ResourceCommandResult.Failed($"kubectl rollout restart of {ns}/{service} failed.");
                    }

                    return ResourceCommandResult.Succeeded($"Restarting {service}.");

                case ResourceCommandNames.Stop:
                    if (location.DesiredReplicas == 0)
                    {
                        return ResourceCommandResult.Succeeded($"{service} is already stopped.");
                    }

                    // Remember the count first, so start can put it back even if scaling fails halfway.
                    await _kubectl.AnnotateAsync("deployment", service, ns, KubernetesResourceMapper.StoppedReplicasAnnotation,
                        location.DesiredReplicas.ToString(CultureInfo.InvariantCulture), ct);
                    await _kubectl.ScaleDeploymentAsync(service, ns, 0, ct);
                    return ResourceCommandResult.Succeeded($"Scaled {service} to 0 (was {location.DesiredReplicas}).");

                case ResourceCommandNames.Start:
                    var replicas = location.StoppedReplicas is > 0 ? location.StoppedReplicas.Value : Math.Max(1, _config.DefaultReplicas);
                    await _kubectl.ScaleDeploymentAsync(service, ns, replicas, ct);
                    await _kubectl.AnnotateAsync("deployment", service, ns, KubernetesResourceMapper.StoppedReplicasAnnotation, null, ct);
                    return ResourceCommandResult.Succeeded($"Scaled {service} to {replicas}.");

                default:
                    return ResourceCommandResult.Failed($"Unknown command '{commandName}'.");
            }
        }
        catch (InvalidOperationException ex)
        {
            return ResourceCommandResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Where a resource is. Before the first poll (or for a name it hasn't seen) the name is
    /// taken to be a service, which lives in its own namespace.
    /// </summary>
    private KubernetesResourceLocation Locate(string resourceName)
        => _locations.TryGetValue(resourceName, out var location)
            ? location
            : new KubernetesResourceLocation(
                KubernetesManifestGenerator.NamespaceFor(_config, resourceName), resourceName, null,
                _config.DefaultReplicas, null);
}
