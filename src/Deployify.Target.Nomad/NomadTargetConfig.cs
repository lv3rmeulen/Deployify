using System.Collections.Generic;

namespace Deployify.Target.Nomad;

/// <summary>
/// Nomad-specific settings stored under targetConfig["nomad"] in deployify-state.json.
/// </summary>
public sealed class NomadTargetConfig
{
    public string Address { get; set; } = "http://localhost:4646";

    /// <summary>
    /// Nomad ACL token. Prefer <c>"env:VAR_NAME"</c>; when unset, NOMAD_TOKEN is used.
    /// </summary>
    public string? Token { get; set; }

    public string Namespace { get; set; } = "default";
    public List<string> Datacenters { get; set; } = ["dc1"];
    public NomadDeploymentMode DeploymentMode { get; set; } = NomadDeploymentMode.Docker;

    /// <summary>
    /// Docker registry username. When set together with <see cref="RegistryPassword"/>,
    /// an <c>auth { username = ... password = ... }</c> block is emitted inside every
    /// Docker task config so Nomad agents can pull from private registries without
    /// requiring a prior <c>docker login</c> on the host.
    /// </summary>
    public string? RegistryUsername { get; set; }

    /// <summary>
    /// Docker registry password or access token. Prefer <c>"env:VAR_NAME"</c> so the secret
    /// stays out of deployify-state.json; when unset, DEPLOYIFY_REGISTRY_PASSWORD is used.
    /// </summary>
    public string? RegistryPassword { get; set; }

    /// <summary>
    /// Docker registry server address for the auth block.
    /// Deployify derives this automatically from containerRegistry or the image name.
    /// Override explicitly for private registries, e.g. "https://myregistry.azurecr.io".
    /// Leave null for Docker Hub.
    /// </summary>
    public string? RegistryServer { get; set; }

    /// <summary>
    /// Service registration provider. Defaults to "nomad" (built-in service catalog).
    /// Set to "consul" to register with Consul instead.
    /// </summary>
    public string ServiceProvider { get; set; } = "nomad";

    /// <summary>
    /// When true, emits a service {} block for each task with bindings.
    /// Defaults to true.
    /// </summary>
    public bool RegisterServices { get; set; } = true;

    /// <summary>
    /// Health check type: "tcp" (default) checks the service's port is open, "http" calls
    /// <see cref="HealthCheckPath"/>, "none" registers the service without a check.
    /// TCP is the default because Aspire's ServiceDefaults only map /health in the
    /// Development environment, so an HTTP check fails in a Production container.
    /// Non-HTTP services (Redis, Postgres, …) always get a TCP check.
    /// </summary>
    public string HealthCheckType { get; set; } = "tcp";

    /// <summary>
    /// Emits <c>auto_revert = true</c> in every job's update block, so Nomad goes back to the
    /// last stable job version by itself when a deployment fails. Deployify's health-gated rollout
    /// notices that and doesn't revert a second time. Defaults to true.
    /// </summary>
    public bool AutoRevert { get; set; } = true;

    /// <summary>
    /// HTTP path used when <see cref="HealthCheckType"/> is "http". Defaults to "/health".
    /// </summary>
    public string HealthCheckPath { get; set; } = "/health";

    /// <summary>
    /// Health check interval. Defaults to "10s".
    /// </summary>
    public string HealthCheckInterval { get; set; } = "10s";

    /// <summary>
    /// Health check timeout. Defaults to "2s".
    /// </summary>
    public string HealthCheckTimeout { get; set; } = "2s";

    /// <summary>
    /// Base port for automatic port assignment when Aspire bindings have no containerPort.
    /// Each service binding gets an incrementing port starting from this value.
    /// Defaults to 8080.
    /// </summary>
    public int BasePort { get; set; } = 8080;

    /// <summary>CPU (MHz) reserved for each task. Defaults to 256.</summary>
    public int DefaultCpu { get; set; } = 256;

    /// <summary>Memory (MB) reserved for each task. Defaults to 256.</summary>
    public int DefaultMemory { get; set; } = 256;

    /// <summary>Artifact store configuration for raw_exec deployments.</summary>
    public ArtifactStoreConfig? ArtifactStore { get; set; }
}

public enum NomadDeploymentMode
{
    Docker,
    RawExec
}
