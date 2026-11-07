using System.Collections.Generic;

namespace Deployify.Target.NomadConsul;

/// <summary>
/// Internal model for a single Nomad job running inside the Consul service mesh.
///
/// Key differences from a plain NomadJob:
/// - Network mode is always "bridge" (required for Connect sidecars).
/// - Ports are not exposed on the host; the container port is referenced
///   by name inside the sidecar service.
/// - Dependencies are rendered as upstream stanzas, each bound to a
///   loopback port inside the task group so the service can reach peers
///   via localhost:&lt;upstream-port&gt;.
/// </summary>
public sealed class ConsulJob
{
    /// <summary>Job and service name (matches the Aspire resource name).</summary>
    public string Name { get; set; } = string.Empty;

    public string? Image { get; set; }
    public string? ImageTag { get; set; }
    public int Count { get; set; } = 1;

    /// <summary>Environment variables injected into the task.</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>
    /// Bindings from the Aspire manifest — used to build the Consul service port
    /// and, when <see cref="ExposeViaIngressGateway"/> is true, the ingress listener.
    /// </summary>
    public List<ConsulPort> Ports { get; set; } = [];

    /// <summary>
    /// Names of other services this job depends on. Each entry becomes a
    /// Connect upstream stanza bound to a loopback port inside the group.
    /// </summary>
    public List<string> Dependencies { get; set; } = [];

    /// <summary>
    /// Resolved upstream entries: (service name → local loopback port).
    /// Populated by <see cref="ConsulJobGenerator"/> so that env vars can be
    /// injected and the HCL upstream stanzas can be rendered.
    /// </summary>
    public List<ConsulUpstream> Upstreams { get; set; } = [];

    /// <summary>Whether this service should be exposed via the Ingress Gateway.</summary>
    public bool ExposeViaIngressGateway { get; set; }

    /// <summary>True for project.v0 resources; false for container.v0.</summary>
    public bool IsProjectService { get; set; }

    /// <summary>Container arguments from the manifest (placeholders already resolved).</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>
    /// True for the single synthetic job that runs the Consul ingress gateway in front of
    /// every service with <see cref="ExposeViaIngressGateway"/> set.
    /// </summary>
    public bool IsIngressGateway { get; set; }

    /// <summary>Listeners of the ingress gateway job: one per exposed service.</summary>
    public List<IngressListener> IngressListeners { get; set; } = [];

    /// <summary>Path written by the generator, set after WriteHclAsync.</summary>
    public string? HclPath { get; set; }

    // ── Dapr ──────────────────────────────────────────────────────────────────
    public bool DaprEnabled { get; set; }
    public string? DaprAppId { get; set; }
    public int? DaprAppPort { get; set; }
    public int DaprHttpPort { get; set; } = 3500;
    public int DaprGrpcPort { get; set; } = 50001;
    public string DaprVersion { get; set; } = "latest";
}

/// <summary>A single port binding from the Aspire manifest.</summary>
public sealed class ConsulPort
{
    public string Label { get; set; } = string.Empty;
    public int ContainerPort { get; set; }
    public string Protocol { get; set; } = "tcp";

    /// <summary>The binding carries HTTP traffic (decides the health check type).</summary>
    public bool IsHttp { get; set; }
}

/// <summary>One ingress gateway listener forwarding a host port to a mesh service.</summary>
public sealed class IngressListener
{
    public string ServiceName { get; set; } = string.Empty;
    public int Port { get; set; }
}

/// <summary>
/// A resolved upstream dependency: the remote service name and the local
/// loopback port Nomad will bind for it inside the task group.
/// </summary>
public sealed class ConsulUpstream
{
    /// <summary>The upstream Consul service name.</summary>
    public string DestinationName { get; set; } = string.Empty;

    /// <summary>The loopback port inside the allocation, e.g. 20000.</summary>
    public int LocalBindPort { get; set; }
}
