namespace Deployify.Target.NomadConsul;

/// <summary>
/// Configuration for the Nomad + Consul Connect service mesh target.
/// Stored under targetConfig["nomadconsul"] in deployify-state.json.
///
/// Nomad settings are used to submit jobs; Consul settings are used to
/// verify the Consul agent is reachable and to configure service registration.
/// </summary>
public sealed class NomadConsulTargetConfig
{
    // ── Nomad ─────────────────────────────────────────────────────────────────

    /// <summary>Nomad API address, e.g. http://localhost:4646</summary>
    public string NomadAddress { get; set; } = "http://localhost:4646";

    /// <summary>Optional Nomad ACL token.</summary>
    public string? NomadToken { get; set; }

    /// <summary>Nomad namespace for all jobs.</summary>
    public string NomadNamespace { get; set; } = "default";

    /// <summary>Nomad datacenters.</summary>
    public List<string> Datacenters { get; set; } = ["dc1"];

    // ── Consul ────────────────────────────────────────────────────────────────

    /// <summary>Consul HTTP API address, e.g. http://localhost:8500</summary>
    public string ConsulAddress { get; set; } = "http://localhost:8500";

    /// <summary>Optional Consul ACL token.</summary>
    public string? ConsulToken { get; set; }

    /// <summary>
    /// Consul datacenter. If null, uses the agent's default datacenter.
    /// </summary>
    public string? ConsulDatacenter { get; set; }

    /// <summary>
    /// Consul admin partition / namespace for Connect-aware services.
    /// Leave null for the default namespace in OSS Consul.
    /// </summary>
    public string? ConsulNamespace { get; set; }

    // ── Mesh behaviour ────────────────────────────────────────────────────────

    /// <summary>
    /// Base port for Consul Connect upstream listeners inside the task group.
    /// Each dependency gets an incrementing port starting here.
    /// Default: 20000 (Nomad Connect convention).
    /// </summary>
    public int UpstreamBasePort { get; set; } = 20000;

    /// <summary>
    /// Whether to expose services externally via an Ingress Gateway.
    /// When true, an ingress gateway service block is emitted for every
    /// service that has HTTP bindings.
    /// </summary>
    public bool EnableIngressGateway { get; set; } = false;

    /// <summary>
    /// Name of the Consul Ingress Gateway to register against.
    /// Only used when <see cref="EnableIngressGateway"/> is true.
    /// </summary>
    public string IngressGatewayName { get; set; } = "ingress-gateway";

    // ── Resource defaults ─────────────────────────────────────────────────────

    public int DefaultCpu { get; set; } = 256;
    public int DefaultMemory { get; set; } = 256;

    // ── Registry auth ─────────────────────────────────────────────────────────

    /// <summary>
    /// Docker registry username. When set together with <see cref="RegistryPassword"/>,
    /// an <c>auth { username = ... password = ... }</c> block is emitted inside every
    /// Docker task config so Nomad agents can pull from private registries.
    /// </summary>
    public string? RegistryUsername { get; set; }

    /// <summary>Docker registry password or access token.</summary>
    public string? RegistryPassword { get; set; }

    /// <summary>Docker registry server address override.</summary>
    public string? RegistryServer { get; set; }

    // ── Service registration ──────────────────────────────────────────────────

    /// <summary>When true, emits a service {} block for each task. Defaults to true.</summary>
    public bool RegisterServices { get; set; } = true;

    /// <summary>HTTP path for health checks. Defaults to "/health".</summary>
    public string HealthCheckPath { get; set; } = "/health";

    /// <summary>Health check interval. Defaults to "10s".</summary>
    public string HealthCheckInterval { get; set; } = "10s";

    /// <summary>Health check timeout. Defaults to "2s".</summary>
    public string HealthCheckTimeout { get; set; } = "2s";
}
