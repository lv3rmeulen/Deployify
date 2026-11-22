using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Deployify.Core.Models;
using Deployify.Target.Kubernetes;
using Deployify.Target.Kubernetes.Manifests;

namespace Deployify.Target.GitOps;

/// <summary>
/// Canary and blue-green on the GitOps targets, where a controller in the cluster moves the
/// traffic: Argo Rollouts next to Argo CD, Flagger next to Flux. Deployify commits the
/// controller's object next to the service's manifests and adjusts those manifests so the two
/// controllers don't undo each other's changes. Either controller must be installed in the cluster.
/// </summary>
public static class ProgressiveDelivery
{
    public const string ArgoRolloutsFile = "rollout.yaml";
    public const string PreviewServiceFile = "service-preview.yaml";
    public const string FlaggerCanaryFile = "canary.yaml";
    public const string FlaggerHpaFile = "hpa.yaml";

    // ── Argo Rollouts ─────────────────────────────────────────────────────────

    public static string? ArgoRolloutsUnsupportedReason(KubernetesServiceManifest svc, RolloutStrategy strategy)
        => strategy.Kind == RolloutStrategyKind.BlueGreen && !svc.HasService
            ? "blue-green switches the service's Service, and this service has no port."
            : null;

    /// <summary>
    /// The service's files with an Argo Rollouts <c>Rollout</c> that takes over its Deployment through
    /// <c>workloadRef</c> (the Deployment stays in git and is scaled down by Argo Rollouts), and for
    /// blue-green a <c>{service}-preview</c> Service. The Argo CD Application ignores the fields
    /// Argo Rollouts changes (see <see cref="ArgoCdIgnoreDifferences"/>).
    /// </summary>
    public static IReadOnlyList<(string FileName, string Content)> ArgoRollouts(
        KubernetesServiceManifest svc, RolloutStrategy strategy, IReadOnlyList<(string FileName, string Content)> files)
    {
        var result = files.ToList();
        var sb = new StringBuilder();
        sb.Append($"""
            apiVersion: argoproj.io/v1alpha1
            kind: Rollout
            metadata:
              name: {svc.ServiceName}
              namespace: {svc.Namespace}
              labels:
                app: {svc.ServiceName}
                app.kubernetes.io/managed-by: deployify
            spec:
              replicas: {N(svc.Replicas)}
              selector:
                matchLabels:
                  app: {svc.ServiceName}
              workloadRef:
                apiVersion: apps/v1
                kind: Deployment
                name: {svc.ServiceName}
                scaleDown: progressively
              strategy:

            """);
        if (strategy.Kind == RolloutStrategyKind.Canary)
        {
            sb.Append("    canary:\n      steps:\n");
            foreach (var step in strategy.Steps)
            {
                sb.Append($"        - setWeight: {N(step.Weight)}\n");
                if (step.Pause > TimeSpan.Zero)
                {
                    sb.Append($"        - pause:\n            duration: {Seconds(step.Pause)}\n");
                }
            }
        }
        else
        {
            sb.Append($"""
                    blueGreen:
                      activeService: {svc.ServiceName}
                      previewService: {svc.ServiceName}-preview
                      autoPromotionEnabled: true
                      scaleDownDelaySeconds: {N((int)strategy.ScaleDownDelay.TotalSeconds)}

                """);
            if (strategy.PreviewPause > TimeSpan.Zero)
            {
                sb.Append($"      autoPromotionSeconds: {N((int)strategy.PreviewPause.TotalSeconds)}\n");
            }

            var service = result.FirstOrDefault(f => f.FileName == "service.yaml");
            if (service.Content is not null)
            {
                result.Add((PreviewServiceFile, CandidateManifests.Renamed(service.Content, $"{svc.ServiceName}-preview")));
            }
        }

        result.Add((ArgoRolloutsFile, sb.ToString()));
        return result;
    }

    /// <summary>
    /// <c>ignoreDifferences</c> for the Argo CD Application of a service under Argo Rollouts: the
    /// Deployment's replicas (scaled down by Argo Rollouts) and, for blue-green, the Services'
    /// selectors (Argo Rollouts adds the pod template hash), so self-heal doesn't undo them.
    /// </summary>
    public static string ArgoCdIgnoreDifferences(KubernetesServiceManifest svc, RolloutStrategy strategy)
    {
        var sb = new StringBuilder();
        sb.Append($"""
              ignoreDifferences:
                - group: apps
                  kind: Deployment
                  name: {svc.ServiceName}
                  jsonPointers:
                    - /spec/replicas

            """);
        if (strategy.Kind == RolloutStrategyKind.BlueGreen)
        {
            foreach (var name in new[] { svc.ServiceName, $"{svc.ServiceName}-preview" })
            {
                sb.Append($"""
                        - group: ""
                          kind: Service
                          name: {name}
                          jsonPointers:
                            - /spec/selector

                    """);
            }
        }

        return sb.ToString();
    }

    // ── Flagger ───────────────────────────────────────────────────────────────

    public static string? FlaggerUnsupportedReason(KubernetesServiceManifest svc, RolloutStrategy strategy, FlaggerConfig flagger)
    {
        if (!svc.HasService || svc.Ports.Count == 0)
        {
            return "Flagger routes traffic through the service's Service, and this service has no port.";
        }

        if (strategy.Kind == RolloutStrategyKind.BlueGreen)
        {
            return Durations.TryParse(flagger.Interval, out var interval) && interval > TimeSpan.Zero
                ? null
                : $"flux.flagger.interval is '{flagger.Interval}': use a duration such as 1m.";
        }

        if (string.Equals(flagger.Provider, "kubernetes", StringComparison.OrdinalIgnoreCase))
        {
            return "Flagger's kubernetes provider can only do blue-green. For canary weights set flux.flagger.provider to the " +
                   "mesh or ingress controller that routes the traffic (nginx, istio, linkerd, gatewayapi:v1, …).";
        }

        var pauses = strategy.Steps.Select(s => s.Pause).Distinct().ToList();
        return pauses.Count != 1 || pauses[0] <= TimeSpan.Zero
            ? "Flagger waits the same interval after every step: give all canary steps the same pause, longer than 0."
            : null;
    }

    /// <summary>
    /// The service's files for Flagger: its Service is left out (Flagger creates <c>{service}</c>,
    /// <c>{service}-primary</c> and <c>{service}-canary</c> itself), the Deployment loses its replica
    /// count to a HorizontalPodAutoscaler pinned at that count (which Flagger copies for the primary),
    /// and a Flagger <c>Canary</c> is added.
    /// </summary>
    public static IReadOnlyList<(string FileName, string Content)> Flagger(
        KubernetesServiceManifest svc, RolloutStrategy strategy, FlaggerConfig flagger,
        IReadOnlyList<(string FileName, string Content)> files)
    {
        var result = files
            .Where(f => f.FileName != "service.yaml")
            .Select(f => f.FileName == "deployment.yaml" ? (f.FileName, CandidateManifests.WithoutReplicas(f.Content)) : f)
            .ToList();

        var port = svc.Ports.FirstOrDefault(p => p.IsHttp) ?? svc.Ports[0];
        var interval = strategy.Kind == RolloutStrategyKind.Canary
            ? strategy.Steps[0].Pause
            : Durations.TryParse(flagger.Interval, out var i) ? i : TimeSpan.FromMinutes(1);

        result.Add((FlaggerHpaFile, $"""
            apiVersion: autoscaling/v2
            kind: HorizontalPodAutoscaler
            metadata:
              name: {svc.ServiceName}
              namespace: {svc.Namespace}
              labels:
                app.kubernetes.io/managed-by: deployify
            spec:
              scaleTargetRef:
                apiVersion: apps/v1
                kind: Deployment
                name: {svc.ServiceName}
              minReplicas: {N(svc.Replicas)}
              maxReplicas: {N(svc.Replicas)}

            """));

        var sb = new StringBuilder();
        sb.Append($"""
            apiVersion: flagger.app/v1beta1
            kind: Canary
            metadata:
              name: {svc.ServiceName}
              namespace: {svc.Namespace}
              labels:
                app.kubernetes.io/managed-by: deployify
            spec:
              provider: {KubernetesManifestGenerator.QuoteYaml(flagger.Provider)}
              targetRef:
                apiVersion: apps/v1
                kind: Deployment
                name: {svc.ServiceName}
              autoscalerRef:
                apiVersion: autoscaling/v2
                kind: HorizontalPodAutoscaler
                name: {svc.ServiceName}
              progressDeadlineSeconds: 600
              service:
                port: {N(port.ContainerPort)}
                targetPort: {N(port.ContainerPort)}
                portName: {port.Name}
              analysis:
                interval: {Seconds(interval)}
                threshold: {N(flagger.Threshold)}

            """);
        if (strategy.Kind == RolloutStrategyKind.Canary)
        {
            sb.Append($"    stepWeights: [{string.Join(", ", strategy.Steps.Select(s => N(s.Weight)))}]\n");
        }
        else
        {
            var iterations = Math.Max(1, (int)Math.Ceiling(strategy.PreviewPause.TotalSeconds / interval.TotalSeconds));
            sb.Append($"    iterations: {N(iterations)}\n");
        }

        result.Add((FlaggerCanaryFile, sb.ToString()));
        return result;
    }

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan t) => $"{N((int)t.TotalSeconds)}s";
}

/// <summary>Flagger settings for the Flux target, under <c>targetConfig.flux.flagger</c>.</summary>
public sealed class FlaggerConfig
{
    /// <summary>
    /// Flagger's mesh or ingress provider: <c>kubernetes</c> (the default: blue-green only),
    /// <c>nginx</c>, <c>istio</c>, <c>linkerd</c>, <c>gatewayapi:v1</c>, … Canary weights need one
    /// that routes traffic.
    /// </summary>
    public string Provider { get; set; } = "kubernetes";

    /// <summary>For blue-green: how often Flagger checks the new version before switching. Defaults to 1m.</summary>
    public string Interval { get; set; } = "1m";

    /// <summary>Failed checks before Flagger rolls back.</summary>
    public int Threshold { get; set; } = 2;
}
