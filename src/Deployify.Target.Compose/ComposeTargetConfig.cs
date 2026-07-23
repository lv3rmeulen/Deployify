namespace Deployify.Target.Compose;

/// <summary>
/// Docker Compose-specific settings stored under targetConfig["compose"]
/// in deployify-state.json.
/// </summary>
public sealed class ComposeTargetConfig
{
    /// <summary>
    /// Project name — used as the Compose project name and as the shared network name.
    /// Defaults to "deployify".
    /// </summary>
    public string ProjectName { get; set; } = "deployify";

    /// <summary>
    /// Working directory where compose files are written and docker compose is run from.
    /// Relative to the AppHost directory.
    /// </summary>
    public string OutputPath { get; set; } = "compose-output";

    /// <summary>
    /// Whether to generate a merged compose.yml that includes all services.
    /// Always written alongside the per-service files.
    /// </summary>
    public bool GenerateMergedFile { get; set; } = true;

    /// <summary>
    /// Default restart policy for all services.
    /// </summary>
    public string RestartPolicy { get; set; } = "unless-stopped";

    /// <summary>
    /// Generate health checks for services that have HTTP bindings.
    /// </summary>
    public bool GenerateHealthChecks { get; set; } = true;

    /// <summary>
    /// Generate an .env file alongside the compose files.
    /// </summary>
    public bool GenerateEnvFile { get; set; } = true;

    /// <summary>
    /// Generate Compose profiles: "dev" and "prod".
    /// Project services get "prod", infrastructure (containers) get both.
    /// </summary>
    public bool GenerateProfiles { get; set; } = true;

    /// <summary>
    /// Named volumes to declare at the top level.
    /// Keyed by volume name, value is the driver (null = default local).
    /// </summary>
    public Dictionary<string, string?> Volumes { get; set; } = [];

    /// <summary>
    /// Per-service bind mount overrides. Key = service name, value = list of "host:container" paths.
    /// </summary>
    public Dictionary<string, List<string>> BindMounts { get; set; } = [];
}
