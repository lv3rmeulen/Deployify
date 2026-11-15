using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Deployify.Core.ResourceSources;

namespace Deployify.Target.Kubernetes;

/// <summary>Where a dashboard resource lives in the cluster, for its logs and commands.</summary>
internal sealed record KubernetesResourceLocation(
    string Namespace,
    string Service,
    string? Pod,
    int DesiredReplicas,
    int? StoppedReplicas);

/// <summary>
/// Turns <c>kubectl get … -o json</c> output into dashboard resources: a "Deployment" per
/// Deployify service, with its pods nested under it. Pure, so it is tested without a cluster.
/// </summary>
internal static class KubernetesResourceMapper
{
    /// <summary>Annotation that remembers a Deployment's replica count while it is stopped (scaled to 0).</summary>
    public const string StoppedReplicasAnnotation = "deployify/stopped-replicas";

    public const string DeploymentType = "Deployment";
    public const string PodType = "Pod";

    private static readonly HashSet<string> s_failingReasons = new(StringComparer.Ordinal)
    {
        "CrashLoopBackOff", "ImagePullBackOff", "ErrImagePull", "InvalidImageName",
        "CreateContainerConfigError", "CreateContainerError", "RunContainerError", "OOMKilled", "Error"
    };

    public static (IReadOnlyList<ResourceSnapshot> Resources, IReadOnlyDictionary<string, KubernetesResourceLocation> Locations) Map(
        string managedObjectsJson, string podsJson, KubernetesTargetConfig config, ResourceSourceOptions options)
    {
        using var managedDoc = JsonDocument.Parse(managedObjectsJson);
        using var podsDoc = JsonDocument.Parse(podsJson);
        var objects = Items(managedDoc.RootElement).ToList();
        var prefix = config.NamespacePrefix + "-";

        // Only namespaces Deployify manages under this environment's prefix; the -system
        // namespace holds the generated-parameters Secret, not a service.
        var namespaces = objects
            .Where(o => Kind(o) == "Namespace")
            .Select(o => Str(o, "metadata", "name") ?? "")
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        !n.Equals(KubernetesDeploymentTarget.SecretsNamespace(config), StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.Ordinal);

        var inScope = objects.Where(o => namespaces.Contains(Str(o, "metadata", "namespace") ?? "")).ToList();
        var configMaps = inScope.Where(o => Kind(o) == "ConfigMap")
            .ToDictionary(o => (Str(o, "metadata", "namespace")!, Str(o, "metadata", "name")!), o => o);
        var pods = Items(podsDoc.RootElement)
            .Where(p => namespaces.Contains(Str(p, "metadata", "namespace") ?? ""))
            .ToList();

        var resources = new List<ResourceSnapshot>();
        var locations = new Dictionary<string, KubernetesResourceLocation>(StringComparer.Ordinal);
        foreach (var deployment in inScope.Where(o => Kind(o) == "Deployment").OrderBy(o => Str(o, "metadata", "name")))
        {
            var ns = Str(deployment, "metadata", "namespace")!;
            var service = Str(deployment, "metadata", "name")!;
            var ownPods = pods
                .Where(p => Str(p, "metadata", "namespace") == ns && Str(p, "metadata", "labels", "app") == service)
                .OrderBy(p => Str(p, "metadata", "name"), StringComparer.Ordinal)
                .ToList();

            var desired = Int(deployment, "spec", "replicas") ?? 1;
            var stopped = int.TryParse(Str(deployment, "metadata", "annotations", StoppedReplicasAnnotation),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : (int?)null;

            // The dashboard keys resources by name across everything it shows: the service name
            // is unique within the environment, pod names are unique within their namespace.
            var name = service;
            locations[name] = new KubernetesResourceLocation(ns, service, null, desired, stopped);
            resources.Add(MapDeployment(name, ns, service, deployment, ownPods, inScope, configMaps, options));

            foreach (var pod in ownPods)
            {
                var podName = Str(pod, "metadata", "name")!;
                locations[podName] = new KubernetesResourceLocation(ns, service, podName, desired, stopped);
                resources.Add(MapPod(podName, name, ns, pod));
            }
        }

        return (resources, locations);
    }

    private static ResourceSnapshot MapDeployment(
        string name, string ns, string service, JsonElement deployment, IReadOnlyList<JsonElement> pods,
        IReadOnlyList<JsonElement> objects, IReadOnlyDictionary<(string, string), JsonElement> configMaps,
        ResourceSourceOptions options)
    {
        var desired = Int(deployment, "spec", "replicas") ?? 1;
        var ready = Int(deployment, "status", "readyReplicas") ?? 0;
        var updated = Int(deployment, "status", "updatedReplicas") ?? 0;
        var conditions = Conditions(deployment);
        var waitingReasons = pods.SelectMany(WaitingReasons).ToList();

        string state;
        ResourceStateStyle? style;
        if (desired == 0)
        {
            (state, style) = pods.Count > 0 ? (ResourceStates.Stopping, ResourceStateStyle.Info) : (ResourceStates.Exited, (ResourceStateStyle?)null);
        }
        else if (waitingReasons.Any(s_failingReasons.Contains))
        {
            (state, style) = ready == 0
                ? (ResourceStates.FailedToStart, ResourceStateStyle.Error)
                : (ResourceStates.RuntimeUnhealthy, ResourceStateStyle.Warning);
        }
        else if (conditions.TryGetValue("Progressing", out var progressing) && progressing.Reason == "ProgressDeadlineExceeded")
        {
            (state, style) = (ResourceStates.FailedToStart, ResourceStateStyle.Error);
        }
        else if (ready >= desired && updated >= desired)
        {
            (state, style) = (ResourceStates.Running, ResourceStateStyle.Success);
        }
        else
        {
            (state, style) = (ResourceStates.Starting, ResourceStateStyle.Info);
        }

        var health = new List<ResourceHealthReport>();
        if (desired > 0)
        {
            var available = conditions.TryGetValue("Available", out var a) && a.Status == "True";
            var status = !available ? ResourceHealthStatus.Unhealthy
                : ready < desired ? ResourceHealthStatus.Degraded
                : ResourceHealthStatus.Healthy;
            health.Add(new ResourceHealthReport("replicas", status,
                $"{ready}/{desired} replicas ready" + (a?.Message is { Length: > 0 } m && !available ? $": {m}" : "")));
        }

        var container = MainContainer(deployment, service);
        var (env, secretSources) = EnvironmentOf(container, ns, configMaps, options);
        var properties = new List<ResourceProperty>
        {
            new(ResourcePropertyNames.ContainerImage, container is { } c ? Str(c, "image") : null, "Image"),
            new("kubernetes.namespace", ns, "Namespace"),
            new("kubernetes.replicas", $"{ready}/{desired}", "Ready replicas"),
            new("kubernetes.revision", Str(deployment, "metadata", "annotations", "deployment.kubernetes.io/revision"), "Revision")
        };
        if (secretSources.Count > 0)
        {
            properties.Add(new ResourceProperty("kubernetes.secrets", string.Join(", ", secretSources), "Secrets"));
        }

        return new ResourceSnapshot
        {
            Name = name,
            DisplayName = service,
            ResourceType = DeploymentType,
            State = state,
            StateStyle = style,
            CreatedAt = Time(deployment, "metadata", "creationTimestamp"),
            Urls = Urls(ns, service, objects),
            Environment = env,
            Properties = properties,
            HealthReports = health,
            Commands =
            [
                ResourceCommandNames.StartCommand(enabled: desired == 0),
                ResourceCommandNames.StopCommand(enabled: desired > 0,
                    confirmation: $"Scale {service} to 0 replicas? The next 'deployify apply' scales it back up."),
                ResourceCommandNames.RestartCommand(enabled: desired > 0,
                    description: "Rolling restart (kubectl rollout restart)")
            ]
        };
    }

    private static ResourceSnapshot MapPod(string name, string parent, string ns, JsonElement pod)
    {
        var phase = Str(pod, "status", "phase") ?? "Unknown";
        var terminating = Str(pod, "metadata", "deletionTimestamp") is not null;
        var waiting = WaitingReasons(pod).ToList();
        var conditions = Conditions(pod);
        var ready = conditions.TryGetValue("Ready", out var r) && r.Status == "True";
        var restarts = ContainerStatuses(pod).Sum(cs => Int(cs, "restartCount") ?? 0);

        string state;
        ResourceStateStyle? style;
        if (terminating)
        {
            (state, style) = (ResourceStates.Stopping, ResourceStateStyle.Info);
        }
        else if (waiting.FirstOrDefault(s_failingReasons.Contains) is { } failing)
        {
            // The reason itself ("CrashLoopBackOff") says more than any generic state.
            (state, style) = (failing, ResourceStateStyle.Error);
        }
        else
        {
            (state, style) = phase switch
            {
                "Running" when ready => (ResourceStates.Running, ResourceStateStyle.Success),
                "Running" => (ResourceStates.Running, ResourceStateStyle.Warning),
                "Pending" when conditions.TryGetValue("PodScheduled", out var sched) && sched.Status == "False"
                    => (ResourceStates.Waiting, ResourceStateStyle.Warning),
                "Pending" => (ResourceStates.Starting, ResourceStateStyle.Info),
                "Succeeded" => (ResourceStates.Finished, (ResourceStateStyle?)null),
                "Failed" => (ResourceStates.Exited, ResourceStateStyle.Error),
                _ => (phase, (ResourceStateStyle?)null)
            };
        }

        var health = new List<ResourceHealthReport>();
        if (phase == "Running" && !terminating)
        {
            health.Add(new ResourceHealthReport("ready",
                ready ? ResourceHealthStatus.Healthy : ResourceHealthStatus.Unhealthy,
                ready ? "Ready" : r?.Message ?? "Not ready"));
        }

        var terminatedAt = ContainerStatuses(pod)
            .Select(cs => Time(cs, "state", "terminated", "finishedAt"))
            .Where(t => t is not null)
            .Max();

        return new ResourceSnapshot
        {
            Name = name,
            DisplayName = name,
            ResourceType = PodType,
            Parent = parent,
            State = state,
            StateStyle = style,
            CreatedAt = Time(pod, "metadata", "creationTimestamp"),
            StartedAt = Time(pod, "status", "startTime"),
            StoppedAt = phase is "Succeeded" or "Failed" ? terminatedAt : null,
            Properties =
            [
                new(ResourcePropertyNames.ContainerImage,
                    string.Join(", ", Containers(pod).Select(c => Str(c, "image")).Where(i => i is not null)), "Image"),
                new("kubernetes.namespace", ns, "Namespace"),
                new("kubernetes.node", Str(pod, "spec", "nodeName"), "Node"),
                new("kubernetes.podIP", Str(pod, "status", "podIP"), "Pod IP"),
                new("kubernetes.restarts", restarts.ToString(CultureInfo.InvariantCulture), "Restarts")
            ],
            HealthReports = health,
            Commands =
            [
                ResourceCommandNames.RestartCommand(enabled: !terminating,
                    description: "Delete the pod; its Deployment starts a replacement") with
                {
                    ConfirmationMessage = $"Delete pod {name}? Its Deployment starts a replacement."
                }
            ]
        };
    }

    // ── URLs and environment ──────────────────────────────────────────────────

    private static IReadOnlyList<ResourceUrl> Urls(string ns, string service, IReadOnlyList<JsonElement> objects)
    {
        var urls = new List<ResourceUrl>();
        foreach (var ingress in objects.Where(o => Kind(o) == "Ingress" && Str(o, "metadata", "namespace") == ns))
        {
            var tlsHosts = Arr(ingress, "spec", "tls")
                .SelectMany(t => Arr(t, "hosts").Select(h => h.GetString()))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in Arr(ingress, "spec", "rules"))
            {
                if (Str(rule, "host") is not { } host)
                {
                    continue;
                }

                foreach (var path in Arr(rule, "http", "paths"))
                {
                    if (Str(path, "backend", "service", "name") != service)
                    {
                        continue;
                    }

                    var p = Str(path, "path") is { } value && value != "/" ? value : "";
                    var scheme = tlsHosts.Contains(host) ? "https" : "http";
                    urls.Add(new ResourceUrl(null, $"{scheme}://{host}{p}"));
                }
            }
        }

        foreach (var svc in objects.Where(o => Kind(o) == "Service" && Str(o, "metadata", "namespace") == ns))
        {
            var svcName = Str(svc, "metadata", "name");
            foreach (var port in Arr(svc, "spec", "ports"))
            {
                var portName = Str(port, "name");
                var number = Int(port, "port");
                if (number is null)
                {
                    continue;
                }

                var scheme = SchemeFor(portName, Str(port, "appProtocol"), number.Value);
                urls.Add(new ResourceUrl(portName, $"{scheme}://{svcName}.{ns}.svc.cluster.local:{number}", IsInternal: true));
            }
        }

        return urls;
    }

    private static string SchemeFor(string? portName, string? appProtocol, int port)
    {
        var hint = (appProtocol ?? portName ?? "").ToLowerInvariant();
        if (hint.StartsWith("https", StringComparison.Ordinal) || port == 443)
        {
            return "https";
        }

        return hint.StartsWith("http", StringComparison.Ordinal) || port is 80 or 8080 ? "http" : "tcp";
    }

    private static (IReadOnlyList<ResourceEnvironmentVariable> Env, IReadOnlyList<string> SecretSources) EnvironmentOf(
        JsonElement? container, string ns, IReadOnlyDictionary<(string, string), JsonElement> configMaps, ResourceSourceOptions options)
    {
        var env = new List<ResourceEnvironmentVariable>();
        var secrets = new List<string>();
        if (container is not { } c)
        {
            return (env, secrets);
        }

        foreach (var from in Arr(c, "envFrom"))
        {
            if (Str(from, "configMapRef", "name") is { } cmName &&
                configMaps.TryGetValue((ns, cmName), out var cm) &&
                cm.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in data.EnumerateObject())
                {
                    env.Add(new ResourceEnvironmentVariable(entry.Name, options.ShowEnvironmentValues ? entry.Value.GetString() : null));
                }
            }
            else if (Str(from, "secretRef", "name") is { } secretName)
            {
                // Its keys would mean reading the Secret: name it, don't open it.
                secrets.Add(secretName);
            }
        }

        foreach (var variable in Arr(c, "env"))
        {
            var name = Str(variable, "name");
            if (name is null)
            {
                continue;
            }

            string? value = null;
            if (variable.TryGetProperty("value", out var v))
            {
                value = options.ShowEnvironmentValues ? v.GetString() : null;
            }
            else if (Str(variable, "valueFrom", "configMapKeyRef", "name") is { } cmName &&
                     Str(variable, "valueFrom", "configMapKeyRef", "key") is { } key &&
                     configMaps.TryGetValue((ns, cmName), out var cm) &&
                     options.ShowEnvironmentValues)
            {
                value = Str(cm, "data", key);
            }
            else if (Str(variable, "valueFrom", "secretKeyRef", "name") is { } secretName)
            {
                if (!secrets.Contains(secretName))
                {
                    secrets.Add(secretName);
                }
            }

            // A later entry for the same name wins, as it does in the container.
            env.RemoveAll(e => e.Name == name);
            env.Add(new ResourceEnvironmentVariable(name, value));
        }

        return ([.. env.OrderBy(e => e.Name, StringComparer.Ordinal)], secrets);
    }

    private static JsonElement? MainContainer(JsonElement deployment, string service)
    {
        var containers = Arr(deployment, "spec", "template", "spec", "containers").ToList();
        return containers.FirstOrDefault(c => Str(c, "name") == service) is { ValueKind: JsonValueKind.Object } named
            ? named
            : containers.Count > 0 ? containers[0] : null;
    }

    // ── Pod status helpers ────────────────────────────────────────────────────

    private static IEnumerable<JsonElement> Containers(JsonElement pod) => Arr(pod, "spec", "containers");

    private static IEnumerable<JsonElement> ContainerStatuses(JsonElement pod)
        => Arr(pod, "status", "initContainerStatuses").Concat(Arr(pod, "status", "containerStatuses"));

    private static IEnumerable<string> WaitingReasons(JsonElement pod)
        => ContainerStatuses(pod)
            .SelectMany(cs => new[] { Str(cs, "state", "waiting", "reason"), Str(cs, "state", "terminated", "reason") })
            .Where(r => r is not null)
            .Select(r => r!);

    private sealed record Condition(string? Status, string? Reason, string? Message);

    private static Dictionary<string, Condition> Conditions(JsonElement obj)
    {
        var result = new Dictionary<string, Condition>(StringComparer.Ordinal);
        foreach (var c in Arr(obj, "status", "conditions"))
        {
            if (Str(c, "type") is { } type)
            {
                result[type] = new Condition(Str(c, "status"), Str(c, "reason"), Str(c, "message"));
            }
        }

        return result;
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static IEnumerable<JsonElement> Items(JsonElement root)
        => root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
            : [];

    private static string? Kind(JsonElement obj) => Str(obj, "kind");

    private static bool TryGet(JsonElement element, string[] path, out JsonElement value)
    {
        value = element;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    internal static string? Str(JsonElement element, params string[] path)
        => TryGet(element, path, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement element, params string[] path)
        => TryGet(element, path, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static IEnumerable<JsonElement> Arr(JsonElement element, params string[] path)
        => TryGet(element, path, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static DateTimeOffset? Time(JsonElement element, params string[] path)
        => Str(element, path) is { } s &&
           DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;
}
