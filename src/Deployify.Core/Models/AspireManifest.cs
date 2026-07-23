using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deployify.Core.Models;

/// <summary>
/// Minimal representation of the manifest.json produced by
/// <c>dotnet run --publisher manifest --output-path manifest.json</c>
/// in an Aspire AppHost project.
/// </summary>
public sealed class AspireManifest
{
    [JsonPropertyName("resources")]
    public Dictionary<string, AspireResource> Resources { get; set; } = [];

    /// <summary>Absorbs unknown top-level keys (e.g. $schema, version) without throwing.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class AspireResource
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>For project resources: relative path to .csproj</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>For container resources: Docker image name</summary>
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }

    [JsonPropertyName("bindings")]
    public Dictionary<string, AspireBinding>? Bindings { get; set; }

    [JsonPropertyName("connectionString")]
    public string? ConnectionString { get; set; }

    /// <summary>References to other resources this resource depends on</summary>
    [JsonPropertyName("references")]
    public List<AspireReference>? References { get; set; }

    /// <summary>
    /// Resource annotations — used by Aspire integrations such as Dapr.
    /// The Aspire Dapr integration emits dapr annotations here.
    /// </summary>
    [JsonPropertyName("annotations")]
    public Dictionary<string, string>? Annotations { get; set; }

    /// <summary>
    /// For dapr.v0 resources: the Dapr sidecar configuration attached to a project.
    /// </summary>
    [JsonPropertyName("dapr")]
    public AspireDaprConfig? Dapr { get; set; }

    /// <summary>
    /// For dapr.component.v0 resources: the Dapr component definition.
    /// </summary>
    [JsonPropertyName("daprComponent")]
    public AspireDaprComponentConfig? DaprComponent { get; set; }

    /// <summary>Absorbs unknown resource properties (inputs, connections, etc.) without throwing.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    // ── Dapr helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the resource has a Dapr sidecar annotation,
    /// either from the manifest (Aspire Dapr integration) or injected by
    /// <see cref="EnvironmentConfig.DeployDapr"/>.
    /// </summary>
    public bool HasDaprSidecar =>
        Annotations is not null &&
        Annotations.TryGetValue("dapr/enabled", out var v) &&
        v.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>App ID for the Dapr sidecar. Falls back to the resource name.</summary>
    public string? DaprAppId =>
        Annotations is not null && Annotations.TryGetValue("dapr/app-id", out var id)
            ? id : null;

    /// <summary>App port Dapr's sidecar will use to call back into the service.</summary>
    public int? DaprAppPort =>
        Annotations is not null &&
        Annotations.TryGetValue("dapr/app-port", out var p) &&
        int.TryParse(p, out var port)
            ? port : null;
}

public sealed class AspireBinding
{
    [JsonPropertyName("scheme")]
    public string? Scheme { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("transport")]
    public string? Transport { get; set; }

    [JsonPropertyName("containerPort")]
    public int? ContainerPort { get; set; }

    [JsonPropertyName("external")]
    public bool IsExternal { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class AspireReference
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Dapr sidecar config from a <c>dapr.v0</c> resource in the Aspire manifest.
/// </summary>
public sealed class AspireDaprConfig
{
    /// <summary>The project resource this sidecar is attached to (e.g. "weatherapi").</summary>
    [JsonPropertyName("application")]
    public string? Application { get; set; }

    [JsonPropertyName("appId")]
    public string? AppId { get; set; }

    [JsonPropertyName("appPort")]
    public int? AppPort { get; set; }

    [JsonPropertyName("appProtocol")]
    public string? AppProtocol { get; set; }

    [JsonPropertyName("appChannelAddress")]
    public string? AppChannelAddress { get; set; }

    [JsonPropertyName("daprHttpPort")]
    public int? DaprHttpPort { get; set; }

    [JsonPropertyName("daprGrpcPort")]
    public int? DaprGrpcPort { get; set; }

    [JsonPropertyName("metricsPort")]
    public int? MetricsPort { get; set; }

    [JsonPropertyName("resourcesPath")]
    public List<string>? ResourcesPath { get; set; }

    [JsonPropertyName("schedulerHostAddress")]
    public string? SchedulerHostAddress { get; set; }

    [JsonPropertyName("placementHostAddress")]
    public string? PlacementHostAddress { get; set; }

    [JsonPropertyName("appHealthProbeTimeout")]
    public int? AppHealthProbeTimeout { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Dapr component config from a <c>dapr.component.v0</c> resource in the Aspire manifest.
/// </summary>
public sealed class AspireDaprComponentConfig
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Extension methods for <see cref="AspireManifest"/>.
/// </summary>
public static class AspireManifestExtensions
{
    /// <summary>
    /// Returns the manifest with an Aspire Dashboard container resource injected when
    /// <see cref="EnvironmentConfig.DeployDashboard"/> is true.
    /// If the dashboard is already present in the manifest it is not added again.
    /// </summary>
    public static AspireManifest WithDashboardIfEnabled(
        this AspireManifest manifest, EnvironmentConfig config)
    {
        if (!config.DeployDashboard)
        {
	        return manifest;
        }

        if (manifest.Resources.ContainsKey(AspireDashboard.ResourceName))
        {
	        return manifest;
        }

        var db = config.Dashboard;
        var env = new Dictionary<string, string>
        {
            ["DASHBOARD__OTLP__ENDPOINT_URL"] = $"http://0.0.0.0:{db.OtlpPort}",
            ["DASHBOARD__RESOURCESERVICE__ENDPOINT_URL"] = "http://0.0.0.0:18300",
            ["DASHBOARD__FRONTEND__ENDPOINT_URL"] = $"http://0.0.0.0:{db.Port}"
        };
        if (db.BrowserToken is not null)
        {
            env["DASHBOARD__FRONTEND__AUTHMODE"] = "BrowserToken";
            env["DASHBOARD__FRONTEND__BROWSERTOKEN"] = db.BrowserToken;
        }

        var injected = new Dictionary<string, AspireResource>(manifest.Resources)
        {
            [AspireDashboard.ResourceName] = new AspireResource
            {
                Type = AspireDashboard.ResourceType,
                Image = $"{AspireDashboard.Image}:{db.ImageTag}",
                Env = env,
                Bindings = new Dictionary<string, AspireBinding>
                {
                    ["http"] = new AspireBinding { Scheme = "http", Protocol = "tcp", ContainerPort = db.Port, IsExternal = true },
                    ["otlp"] = new AspireBinding { Scheme = "grpc", Protocol = "tcp", ContainerPort = db.OtlpPort, IsExternal = true }
                }
            }
        };

        return new AspireManifest { Resources = injected };
    }

    /// <summary>
    /// Returns the manifest with Dapr sidecar annotations merged in from <c>dapr.v0</c>
    /// resources (the Aspire Dapr integration format) and optionally injected on all
    /// remaining project/container resources when <see cref="EnvironmentConfig.DeployDapr"/>
    /// is true.
    ///
    /// <c>dapr.v0</c> and <c>dapr.component.v0</c> resources are removed from the returned
    /// manifest — their configuration is folded into annotations on the linked project resource.
    /// </summary>
    public static AspireManifest WithDaprIfEnabled(
        this AspireManifest manifest, EnvironmentConfig config)
    {
        // ── Pass 1: fold dapr.v0 sidecar configs into their linked project resources ──
        var daprSidecars = manifest.Resources
            .Where(kv => kv.Value is { Type: "dapr.v0", Dapr.Application: not null })
            .ToDictionary(kv => kv.Value.Dapr!.Application!, kv => kv.Value.Dapr!);

        var withoutDaprResources = manifest.Resources
            .Where(kv => kv.Value.Type is not ("dapr.v0" or "dapr.component.v0"))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (daprSidecars.Count > 0)
        {
            foreach (var (appName, daprCfg) in daprSidecars)
            {
                if (!withoutDaprResources.TryGetValue(appName, out var resource))
                {
	                continue;
                }

                var annotations = new Dictionary<string, string>(resource.Annotations ?? [])
                {
                    [DaprConstants.EnabledAnnotation] = "true",
                    [DaprConstants.AppIdAnnotation] = daprCfg.AppId ?? appName
                };
                if (daprCfg.AppPort.HasValue)
                {
	                annotations[DaprConstants.AppPortAnnotation] = daprCfg.AppPort.Value.ToString();
                }

                if (daprCfg.DaprHttpPort.HasValue)
                {
	                annotations[DaprConstants.HttpPortAnnotation] = daprCfg.DaprHttpPort.Value.ToString();
                }

                if (daprCfg.DaprGrpcPort.HasValue)
                {
	                annotations[DaprConstants.GrpcPortAnnotation] = daprCfg.DaprGrpcPort.Value.ToString();
                }

                if (daprCfg.ResourcesPath is { Count: > 0 })
                {
	                annotations["dapr/resources-path"] = string.Join(",", daprCfg.ResourcesPath);
                }

                withoutDaprResources[appName] = new AspireResource
                {
                    Type = resource.Type,
                    Path = resource.Path,
                    Image = resource.Image,
                    Env = resource.Env,
                    Bindings = resource.Bindings,
                    ConnectionString = resource.ConnectionString,
                    References = resource.References,
                    Annotations = annotations
                };
            }
        }

        var working = new AspireManifest { Resources = withoutDaprResources };

        // ── Pass 2: optionally inject annotations on remaining unannotated resources ──
        if (!config.DeployDapr)
        {
	        return working;
        }

        var dapr = config.Dapr;
        var index = 0;
        var injected = new Dictionary<string, AspireResource>();

        foreach (var (name, resource) in working.Resources)
        {
            if (resource.Type is "value.v0" or "parameter.v0")
            {
                injected[name] = resource;
                continue;
            }

            if (resource.HasDaprSidecar)
            {
                injected[name] = resource;
                index++;
                continue;
            }

            var annotations = new Dictionary<string, string>(resource.Annotations ?? [])
            {
                [DaprConstants.EnabledAnnotation] = "true",
                [DaprConstants.AppIdAnnotation] = name,
                [DaprConstants.HttpPortAnnotation] = (dapr.DaprHttpPort + index).ToString(),
                [DaprConstants.GrpcPortAnnotation] = (dapr.DaprGrpcPort + index).ToString()
            };

            var httpPort = resource.Bindings?.Values
                .FirstOrDefault(b => b.Scheme?.StartsWith("http",
                    StringComparison.OrdinalIgnoreCase) == true)?.ContainerPort;
            if (httpPort.HasValue)
            {
	            annotations[DaprConstants.AppPortAnnotation] = httpPort.Value.ToString();
            }

            injected[name] = new AspireResource
            {
                Type = resource.Type,
                Path = resource.Path,
                Image = resource.Image,
                Env = resource.Env,
                Bindings = resource.Bindings,
                ConnectionString = resource.ConnectionString,
                References = resource.References,
                Annotations = annotations
            };

            index++;
        }

        return new AspireManifest { Resources = injected };
    }
}

