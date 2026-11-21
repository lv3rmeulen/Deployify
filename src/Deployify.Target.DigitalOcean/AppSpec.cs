using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Deployify.Target.DigitalOcean;

// The subset of the App Platform app spec Deployify writes. Field names follow the spec
// (snake_case); see https://docs.digitalocean.com/products/app-platform/reference/app-spec/.

public sealed class AppSpec
{
    public required string Name { get; set; }
    public string? Region { get; set; }
    public List<ServiceSpec>? Services { get; set; }
    public List<WorkerSpec>? Workers { get; set; }
    public List<DatabaseSpec>? Databases { get; set; }
    public IngressSpec? Ingress { get; set; }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public JsonObject ToJson() => JsonSerializer.SerializeToNode(this, JsonOptions)!.AsObject();
}

public sealed class ImageSourceSpec
{
    /// <summary><c>DOCR</c>, <c>DOCKER_HUB</c> or <c>GHCR</c>.</summary>
    public required string RegistryType { get; set; }

    /// <summary>Empty for DOCR; the namespace for Docker Hub (<c>library</c> for official images) and GHCR.</summary>
    public string? Registry { get; set; }

    public required string Repository { get; set; }

    /// <summary>Set either <see cref="Tag"/> or <see cref="Digest"/>, never both.</summary>
    public string? Tag { get; set; }

    public string? Digest { get; set; }

    /// <summary><c>username:token</c> for a private Docker Hub or GHCR image.</summary>
    public string? RegistryCredentials { get; set; }
}

public sealed class EnvSpec
{
    public required string Key { get; set; }
    public string? Value { get; set; }

    /// <summary><c>RUN_TIME</c>: images are built before they reach App Platform.</summary>
    public string Scope { get; set; } = "RUN_TIME";

    /// <summary><c>GENERAL</c> or <c>SECRET</c>.</summary>
    public string Type { get; set; } = "GENERAL";
}

public sealed class HealthCheckSpec
{
    public string? HttpPath { get; set; }
    public int? Port { get; set; }
}

public abstract class ComponentSpec
{
    public required string Name { get; set; }
    public required ImageSourceSpec Image { get; set; }
    public string? RunCommand { get; set; }
    public string? InstanceSizeSlug { get; set; }
    public int? InstanceCount { get; set; }
    public List<EnvSpec>? Envs { get; set; }
}

public sealed class ServiceSpec : ComponentSpec
{
    // Ordered after the shared fields, so every component starts with its name.

    /// <summary>The public HTTP port. Null for an internal-only service.</summary>
    [JsonPropertyOrder(1)]
    public int? HttpPort { get; set; }

    /// <summary>Ports only other components of the app can reach.</summary>
    [JsonPropertyOrder(2)]
    public List<int>? InternalPorts { get; set; }

    [JsonPropertyOrder(3)]
    public HealthCheckSpec? HealthCheck { get; set; }
}

public sealed class WorkerSpec : ComponentSpec;

public sealed class DatabaseSpec
{
    public required string Name { get; set; }

    /// <summary><c>PG</c>, <c>VALKEY</c>, <c>REDIS</c>, …</summary>
    public required string Engine { get; set; }

    public string? Version { get; set; }

    /// <summary>True for a Managed Database cluster attached by <see cref="ClusterName"/>; false for a dev database.</summary>
    public bool? Production { get; set; }

    public string? ClusterName { get; set; }

    /// <summary>MySQL and Postgres only.</summary>
    public string? DbName { get; set; }

    public string? DbUser { get; set; }
}

public sealed class IngressSpec
{
    public List<IngressRuleSpec> Rules { get; set; } = [];
}

public sealed class IngressRuleSpec
{
    public required IngressMatchSpec Match { get; set; }
    public required IngressComponentSpec Component { get; set; }
}

public sealed class IngressMatchSpec
{
    public required IngressPathSpec Path { get; set; }
}

public sealed class IngressPathSpec
{
    public required string Prefix { get; set; }
}

public sealed class IngressComponentSpec
{
    public required string Name { get; set; }

    /// <summary>False strips the prefix before forwarding, as App Platform does by default.</summary>
    public bool? PreservePathPrefix { get; set; }
}
