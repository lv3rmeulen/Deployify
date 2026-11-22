using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Target.Kubernetes.Manifests;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Kubernetes;

/// <summary>
/// Canary and blue-green rollouts on Kubernetes without a service mesh, shared by the Kubernetes,
/// OVHcloud and Helm targets. The service's own Deployment is left alone until promotion:
/// <list type="bullet">
/// <item>Canary: a second Deployment <c>{service}-canary</c> with the same <c>app</c> label, so the
/// service's Service spreads requests over both. Its share follows from the number of pods
/// (<see cref="TrafficPrecision.Instances"/>): N stable pods and c canary pods give it c / (N + c).</item>
/// <item>Blue-green: a full-size Deployment <c>{service}-preview</c> with its own <c>app</c> label and a
/// <c>{service}-preview</c> Service to try it at, then the switch: the service's Service selector
/// is pointed at the preview pods.</item>
/// </list>
/// Promotion applies the generated manifests, so the service ends up exactly as a plain apply
/// leaves it; the candidate's objects are then deleted. The candidate reads a copy of the new
/// ConfigMap, so the running pods keep their values until promotion.
/// </summary>
internal sealed class KubernetesTrafficShifter(ILogger logger)
{
    /// <summary>Services whose secrets changed when the candidate started; their pods restart at promotion.</summary>
    private readonly ConcurrentDictionary<string, bool> _restartOnPromote = new();

    public static string CanaryName(string service) => $"{service}-canary";

    public static string PreviewName(string service) => $"{service}-preview";

    public static string CandidateConfigName(string service) => $"{service}-config-candidate";

    public static bool IsPreview(string service, string candidate) => candidate == PreviewName(service);

    /// <summary>
    /// Starts the candidate. <paramref name="syncSecrets"/> puts the service's secrets in their
    /// backend (as a plain apply would) and reports whether running pods need a restart to see them.
    /// </summary>
    public async Task<AppliedRevision> StartAsync(
        KubectlRunner kubectl, KubernetesServiceManifest svc, RolloutStrategy strategy,
        Func<CancellationToken, Task<bool>> syncSecrets, CancellationToken ct)
    {
        var service = svc.ServiceName;
        var ns = svc.Namespace;
        var live = await kubectl.GetDeploymentJsonAsync(service, ns, ct)
                   ?? throw new InvalidOperationException($"Deployment {ns}/{service} doesn't exist, so there is nothing to run a candidate next to.");
        var stable = Math.Max(1, KubernetesDeploymentStatus.Parse(live).SpecReplicas);
        var preview = strategy.Kind == RolloutStrategyKind.BlueGreen;
        var name = preview ? PreviewName(service) : CanaryName(service);

        // Leftovers of an interrupted rollout would keep their old pods and selector.
        await RemoveCandidatesAsync(kubectl, ns, service, ct);

        if (await syncSecrets(ct))
        {
            _restartOnPromote[$"{ns}/{service}"] = true;
        }

        var files = KubernetesManifestBuilder.Build(svc);
        string? configName = null;
        if (files.FirstOrDefault(f => f.FileName == "configmap.yaml").Model is { } configMap)
        {
            configName = CandidateConfigName(service);
            await kubectl.ApplyTextAsync(
                CandidateManifests.ConfigMap(KubernetesManifestBuilder.ToText(configMap), service, configName), ct);
        }

        var deployment = files.First(f => f.FileName == "deployment.yaml").Model;
        await kubectl.ApplyTextAsync(CandidateManifests.Deployment(
            KubernetesManifestBuilder.ToText(deployment), service, name, preview,
            preview ? stable : 1, configName, $"{service}-config"), ct);

        string? url = null;
        if (preview && files.FirstOrDefault(f => f.FileName == "service.yaml").Model is { } serviceModel)
        {
            await kubectl.ApplyTextAsync(
                CandidateManifests.PreviewService(KubernetesManifestBuilder.ToText(serviceModel), service, name), ct);
            var port = svc.Ports.FirstOrDefault(p => p.IsHttp) ?? svc.Ports.FirstOrDefault();
            url = $"http://{name}.{ns}.svc.cluster.local" + (port is null ? "" : $":{port.ContainerPort}");
        }

        logger.LogInformation("Started {Kind} {Namespace}/{Name} next to {Service}.",
            preview ? "preview" : "canary", ns, name, service);
        return new AppliedRevision { ServiceName = service, Success = true, Revision = name, PreviewUrl = url };
    }

    /// <summary>The candidate Deployment's rollout, judged at its latest generation (scaling it bumps the generation).</summary>
    public static async Task<HealthObservation> ObserveAsync(KubectlRunner kubectl, string ns, string candidate, CancellationToken ct)
    {
        var json = await kubectl.GetDeploymentJsonAsync(candidate, ns, ct);
        if (json is null)
        {
            return HealthObservation.Failed($"the candidate deployment {ns}/{candidate} is gone");
        }

        var status = KubernetesDeploymentStatus.Parse(json);
        return KubernetesRolloutHealth.Evaluate(status, status.Generation);
    }

    /// <summary>
    /// Canary: scales the canary so it has about <paramref name="percent"/> of the pods. Blue-green at
    /// 100%: switches the Service to the preview's pods. Returns the share the candidate gets.
    /// </summary>
    public async Task<int> ShiftAsync(
        KubectlRunner kubectl, string ns, string service, string candidate, int percent, CancellationToken ct)
    {
        if (IsPreview(service, candidate))
        {
            if (percent < 100)
            {
                return 0; // a preview gets no traffic before the switch
            }

            if (await kubectl.GetJsonAsync("service", ns, service, ct) is not null)
            {
                await kubectl.PatchAsync("service", ns, service, SelectorPatch(candidate), ct);
                logger.LogInformation("Switched Service {Namespace}/{Name} to {Candidate}.", ns, service, candidate);
            }

            return 100;
        }

        var live = await kubectl.GetDeploymentJsonAsync(service, ns, ct)
                   ?? throw new InvalidOperationException($"Deployment {ns}/{service} disappeared during the canary.");
        var stable = Math.Max(1, KubernetesDeploymentStatus.Parse(live).SpecReplicas);
        var (canary, share) = CanaryReplicas(stable, percent);
        await kubectl.ScaleDeploymentAsync(candidate, ns, canary, ct);
        return share;
    }

    /// <summary>
    /// Canary pods for <paramref name="percent"/> of the traffic next to <paramref name="stable"/>
    /// pods, and the share that gives: c / (stable + c), with c at least 1.
    /// </summary>
    internal static (int Replicas, int Share) CanaryReplicas(int stable, int percent)
    {
        var canary = percent >= 100
            ? stable
            : Math.Max(1, (int)Math.Ceiling(stable * percent / (double)(100 - percent) - 1e-9));
        return (canary, (int)Math.Round(100.0 * canary / (stable + canary), MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// After promotion: points the Service back at the service's own pods (now on the new
    /// version) and deletes the candidate's objects.
    /// </summary>
    public async Task FinishAsync(KubectlRunner kubectl, string ns, string service, CancellationToken ct)
    {
        await RemoveCandidatesAsync(kubectl, ns, service, ct);
        logger.LogInformation("Removed the candidate of {Namespace}/{Name}.", ns, service);
    }

    /// <summary>True (once) when the service's pods must restart at promotion to read changed secrets.</summary>
    public bool TakeRestart(string ns, string service) => _restartOnPromote.TryRemove($"{ns}/{service}", out _);

    /// <summary>
    /// Sends all traffic back to the service's own pods and deletes any candidate. Safe to call
    /// when there is none; used before a restore and before starting a new candidate.
    /// </summary>
    public static async Task RemoveCandidatesAsync(KubectlRunner kubectl, string ns, string service, CancellationToken ct)
    {
        if (await kubectl.GetJsonAsync("service", ns, service, ct) is { } json && SelectorApp(json) is { } app && app != service)
        {
            await kubectl.PatchAsync("service", ns, service, SelectorPatch(service), ct);
        }

        await kubectl.DeleteAsync("deployment", ns, CanaryName(service), ct);
        await kubectl.DeleteAsync("deployment", ns, PreviewName(service), ct);
        await kubectl.DeleteAsync("service", ns, PreviewName(service), ct);
        await kubectl.DeleteAsync("configmap", ns, CandidateConfigName(service), ct);
    }

    internal static string SelectorPatch(string app)
        => JsonSerializer.Serialize(new { spec = new { selector = new { app } } });

    internal static string? SelectorApp(string serviceJson)
    {
        using var doc = JsonDocument.Parse(serviceJson);
        return doc.RootElement.TryGetProperty("spec", out var spec) &&
               spec.TryGetProperty("selector", out var selector) && selector.ValueKind == JsonValueKind.Object &&
               selector.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.String
            ? app.GetString()
            : null;
    }
}
