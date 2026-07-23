namespace Deployify.Target.Nomad;

/// <summary>
/// Nomad-specific settings stored under targetConfig["nomad"] in deployify-state.json.
/// </summary>
public sealed class NomadTargetConfig
{
    public string Address { get; set; } = "http://localhost:4646";
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
    /// Docker registry password or access token.
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
    /// HTTP path used for health checks. Defaults to "/health".
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

    /// <summary>Artifact store configuration for raw_exec deployments.</summary>
    public ArtifactStoreConfig? ArtifactStore { get; set; }
}

public enum NomadDeploymentMode { Docker, RawExec }
