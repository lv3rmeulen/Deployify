namespace Deployify.Target.Kubernetes;

/// <summary>
/// Kubernetes-specific settings stored under targetConfig["kubernetes"]
/// in deployify-state.json.
/// </summary>
public sealed class KubernetesTargetConfig
{
    /// <summary>Path to kubeconfig file. Null = use default (~/.kube/config).</summary>
    public string? KubeConfigPath { get; set; }

    /// <summary>Kubernetes context to use. Null = current context.</summary>
    public string? Context { get; set; }

    /// <summary>
    /// Namespace prefix. Each service gets its own namespace: {NamespacePrefix}-{serviceName}.
    /// E.g. prefix "myapp" → namespace "myapp-api", "myapp-worker".
    /// </summary>
    public string NamespacePrefix { get; set; } = "deployify";

    /// <summary>Whether to generate an Ingress resource for services with HTTP bindings.</summary>
    public bool GenerateIngress { get; set; } = true;

    /// <summary>Ingress class name (e.g. nginx, traefik).</summary>
    public string IngressClassName { get; set; } = "nginx";

    /// <summary>Base domain for auto-generated Ingress hosts. E.g. "example.com" → host "api.example.com".</summary>
    public string? IngressBaseDomain { get; set; }

    /// <summary>Default CPU request for all containers.</summary>
    public string CpuRequest { get; set; } = "100m";

    /// <summary>Default CPU limit for all containers.</summary>
    public string CpuLimit { get; set; } = "500m";

    /// <summary>Default memory request for all containers.</summary>
    public string MemoryRequest { get; set; } = "128Mi";

    /// <summary>Default memory limit for all containers.</summary>
    public string MemoryLimit { get; set; } = "512Mi";

    /// <summary>Default number of replicas per service.</summary>
    public int DefaultReplicas { get; set; } = 1;

    /// <summary>
    /// Liveness/readiness probe type: "tcp" (default) checks the port is open, "http" calls
    /// <see cref="HealthCheckPath"/>, "none" adds no probes. TCP is the default because
    /// Aspire's ServiceDefaults only map /health in the Development environment, so an
    /// HTTP probe would restart a Production pod in a loop.
    /// </summary>
    public string HealthCheckType { get; set; } = "tcp";

    /// <summary>HTTP path used when <see cref="HealthCheckType"/> is "http".</summary>
    public string HealthCheckPath { get; set; } = "/health";

    /// <summary>Base port for services whose Aspire bindings carry no port.</summary>
    public int BasePort { get; set; } = 8080;
}
