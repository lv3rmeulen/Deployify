using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deployify.Core.Models;

namespace Deployify.Target.DigitalOcean;

/// <summary>
/// DigitalOcean App Platform settings, stored under <c>TargetConfig["digitalocean"]</c> in
/// deployify-state.json. The API token is never stored here: it is read from the environment
/// variable named by <see cref="TokenVariable"/>.
/// </summary>
public sealed class DoTargetConfig
{
    public const string Key = "digitalocean";

    /// <summary>
    /// The App Platform app the environment deploys to: 2–32 lowercase letters, digits and '-',
    /// starting with a letter. Defaults to <c>deployify-{environment}</c>.
    /// </summary>
    public string? AppName { get; set; }

    /// <summary>
    /// App Platform region slug: <c>nyc</c>, <c>ams</c>, <c>sfo</c>, <c>sgp</c>, <c>lon</c>,
    /// <c>fra</c>, <c>tor</c>, <c>blr</c>, <c>syd</c> or <c>atl</c>. Null lets App Platform pick
    /// the nearest one, but creating a database cluster needs it.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>The DigitalOcean project new apps are created in. Null = the account's default project.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Environment variable holding the API token. Falls back to DIGITALOCEAN_TOKEN when unset.</summary>
    public string TokenVariable { get; set; } = "DIGITALOCEAN_ACCESS_TOKEN";

    /// <summary>Instance size of every service and worker without an entry in <see cref="Services"/>.</summary>
    public string DefaultInstanceSize { get; set; } = "apps-s-1vcpu-0.5gb";

    /// <summary>Instance count of every service and worker without an override.</summary>
    public int DefaultInstanceCount { get; set; } = 1;

    /// <summary>Per-service settings, keyed by Aspire resource name.</summary>
    public Dictionary<string, DoServiceConfig> Services { get; set; } = [];

    /// <summary>
    /// How each Postgres or Redis container is deployed, keyed by Aspire resource name. A
    /// resource without an entry gets the default for its engine: a dev database for
    /// Postgres, a Valkey cluster Deployify creates for Redis.
    /// </summary>
    public Dictionary<string, DoDatabaseConfig> Databases { get; set; } = [];

    /// <summary>
    /// Environment variable holding <c>username:token</c> for pulling images Deployify built
    /// from a private Docker Hub or GitHub Container Registry repository. Not needed for DOCR.
    /// </summary>
    public string? RegistryCredentialsVariable { get; set; }

    /// <summary>How long apply waits for the deployment to go live, in minutes.</summary>
    public int DeployTimeoutMinutes { get; set; } = 20;

    /// <summary>How long apply waits for a new database cluster to come online, in minutes.</summary>
    public int DatabaseTimeoutMinutes { get; set; } = 20;

    /// <summary>The Deployify environment this config belongs to. Filled in from the environment's name.</summary>
    [JsonIgnore]
    public string? DeployifyEnvironment { get; set; }

    /// <summary>The app name to use: <see cref="AppName"/>, or one made from the environment's name.</summary>
    public string EffectiveAppName
        => !string.IsNullOrWhiteSpace(AppName)
            ? AppName
            : Names.Component($"deployify-{DeployifyEnvironment ?? "default"}");

    public static DoTargetConfig From(EnvironmentConfig config)
    {
        var cfg = config.TargetConfig.TryGetValue(Key, out var raw) && raw is JsonElement el
            ? el.Deserialize<DoTargetConfig>(TargetConfigJsonOptions.Default) ?? new DoTargetConfig()
            : new DoTargetConfig();
        cfg.DeployifyEnvironment = config.Name;
        return cfg;
    }

    /// <summary>The API token, or null when the variable isn't set.</summary>
    public string? ReadToken(Func<string, string?>? getVariable = null)
    {
        getVariable ??= Environment.GetEnvironmentVariable;
        return NonEmpty(getVariable(TokenVariable)) ?? NonEmpty(getVariable("DIGITALOCEAN_TOKEN"));

        static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// The datacenter for a new database cluster: App Platform apps can only reach databases in
    /// the datacenter their region maps to.
    /// </summary>
    public static string? DatacenterFor(string? region) => region?.ToLowerInvariant() switch
    {
        "nyc" => "nyc3",
        "ams" => "ams3",
        "sfo" => "sfo3",
        "sgp" => "sgp1",
        "lon" => "lon1",
        "fra" => "fra1",
        "tor" => "tor1",
        "blr" => "blr1",
        "syd" => "syd1",
        "atl" => "atl1",
        _ => null
    };
}

public sealed class DoServiceConfig
{
    /// <summary>Instance size slug, e.g. <c>apps-s-1vcpu-1gb</c>.</summary>
    public string? InstanceSize { get; set; }

    /// <summary>Instance count. A serviceOverrides Count wins over this.</summary>
    public int? InstanceCount { get; set; }

    /// <summary>
    /// The public path prefix of a service with an external HTTP binding. Defaults to <c>/</c>
    /// for the only (or first) external service and <c>/{name}</c> for the others.
    /// </summary>
    public string? Route { get; set; }

    /// <summary>HTTP path App Platform probes for readiness, e.g. <c>/health</c>.</summary>
    public string? HealthCheckPath { get; set; }
}

/// <summary>Where a Postgres or Redis resource runs.</summary>
public enum DoDatabaseMode
{
    /// <summary>An App Platform dev database: Postgres only, cheap, not for production data.</summary>
    Dev,

    /// <summary>A Managed Database cluster, attached to the app by name.</summary>
    Cluster,

    /// <summary>Keep the container as an App Platform component. It has no persistent storage.</summary>
    Container
}

public sealed class DoDatabaseConfig
{
    public DoDatabaseMode? Mode { get; set; }

    /// <summary>The Managed Database cluster to attach (Cluster mode). Defaults to <c>{app}-{resource}</c>.</summary>
    public string? ClusterName { get; set; }

    /// <summary>Create the cluster on apply when it doesn't exist (Cluster mode). Default true.</summary>
    public bool Create { get; set; } = true;

    /// <summary>Size slug of a cluster Deployify creates.</summary>
    public string Size { get; set; } = "db-s-1vcpu-1gb";

    /// <summary>Node count of a cluster Deployify creates.</summary>
    public int Nodes { get; set; } = 1;

    /// <summary>Engine version. Defaults to the container image's major version, or the platform default.</summary>
    public string? Version { get; set; }

    /// <summary>
    /// App Platform engine: <c>PG</c>, <c>VALKEY</c> or <c>REDIS</c>. Defaults from the
    /// container image; Redis images map to <c>VALKEY</c>, DigitalOcean's Redis-compatible engine.
    /// </summary>
    public string? Engine { get; set; }
}
