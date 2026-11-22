using System.Text.Json.Serialization;

namespace Deployify.Target.CloudRun;

/// <summary>Google Cloud Run settings, stored under targetConfig["cloudrun"] in deployify-state.json.</summary>
public sealed class CloudRunTargetConfig
{
    /// <summary>Google Cloud project ID. Required.</summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// The project's number, which is part of every service's <c>*.run.app</c> address
    /// (<c>https://{service}-{projectNumber}.{region}.run.app</c>). Looked up with gcloud when not set.
    /// </summary>
    public string? ProjectNumber { get; set; }

    /// <summary>Region the services run in.</summary>
    public string Region { get; set; } = "us-central1";

    /// <summary>Service account the services run as. Null uses the project's default compute service account.</summary>
    public string? ServiceAccount { get; set; }

    /// <summary>Default minimum number of instances. 0 = scale to zero.</summary>
    public int MinInstances { get; set; }

    /// <summary>Default maximum number of instances; a service's <c>count</c> override replaces it.</summary>
    public int MaxInstances { get; set; } = 3;

    /// <summary>CPU limit per instance, e.g. "1", "2" or "1000m".</summary>
    public string Cpu { get; set; } = "1";

    /// <summary>Memory limit per instance, e.g. "512Mi" or "1Gi".</summary>
    public string Memory { get; set; } = "512Mi";

    /// <summary>Requests one instance handles at once. Null keeps Cloud Run's default (80).</summary>
    public int? Concurrency { get; set; }

    /// <summary>
    /// Who can reach services whose Aspire binding isn't external. <c>internal</c> (the default)
    /// lets in only traffic from the project's VPC networks, so services that call them need VPC
    /// egress (<see cref="VpcConnector"/> or <see cref="Network"/>). <c>all</c> makes them public too.
    /// Services with an external binding always get <c>all</c>.
    /// </summary>
    public string InternalIngress { get; set; } = "internal";

    /// <summary>
    /// Grant <c>allUsers</c> the Cloud Run Invoker role, so callers need no Google identity token.
    /// Aspire apps call each other with plain HTTP requests, so this is on by default; ingress
    /// still limits who can reach each service.
    /// </summary>
    public bool AllowUnauthenticated { get; set; } = true;

    /// <summary>Serverless VPC Access connector for outbound traffic, e.g. <c>projects/p/locations/r/connectors/c</c>.</summary>
    public string? VpcConnector { get; set; }

    /// <summary>VPC network for Direct VPC egress (instead of a connector).</summary>
    public string? Network { get; set; }

    /// <summary>Subnet for Direct VPC egress.</summary>
    public string? Subnet { get; set; }

    /// <summary>
    /// What goes out through the VPC: <c>private-ranges-only</c> (the default) or <c>all-traffic</c>.
    /// Calls to <c>internal</c> services through their run.app address need <c>all-traffic</c>.
    /// </summary>
    public string VpcEgress { get; set; } = "private-ranges-only";

    /// <summary>Base port for services whose Aspire bindings carry no port.</summary>
    public int BasePort { get; set; } = 8080;

    /// <summary>
    /// The Deployify environment this config belongs to. Not part of the config file: filled in
    /// from the environment's name, and written to every service as the deployify-env label.
    /// </summary>
    [JsonIgnore]
    public string? DeployifyEnvironment { get; set; }
}
