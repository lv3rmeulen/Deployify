using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// Other resources this resource depends on. Aspire does not emit this field;
    /// <see cref="AspireManifestExtensions.WithInferredReferences"/> fills it in from the
    /// <c>{resource.…}</c> placeholders used in env values and connection strings.
    /// </summary>
    [JsonPropertyName("references")]
    public List<AspireReference>? References { get; set; }

    /// <summary>Parent resource (e.g. the server a database resource belongs to).</summary>
    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

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
    public bool HasDaprSidecar
        => Annotations is not null &&
            Annotations.TryGetValue("dapr/enabled", out var v) &&
            v.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>App ID for the Dapr sidecar. Falls back to the resource name.</summary>
    public string? DaprAppId
        => Annotations is not null && Annotations.TryGetValue("dapr/app-id", out var id)
            ? id : null;

    /// <summary>App port Dapr's sidecar will use to call back into the service.</summary>
    public int? DaprAppPort
        => Annotations is not null &&
            Annotations.TryGetValue("dapr/app-port", out var p) &&
            int.TryParse(p, out var port)
            ? port : null;

    // ── Type helpers ──────────────────────────────────────────────────────────

    /// <summary>True for resources that become a running workload (projects and containers).</summary>
    [JsonIgnore]
    public bool IsDeployable
        => Type.StartsWith("project.", StringComparison.Ordinal) ||
            Type.StartsWith("container.", StringComparison.Ordinal) ||
            Type.StartsWith("dockerfile.", StringComparison.Ordinal);

    /// <summary>The first HTTP(S) binding, if the resource has one.</summary>
    [JsonIgnore]
    public KeyValuePair<string, AspireBinding>? HttpBinding
        => Bindings?.FirstOrDefault(b => b.Value.IsHttp) is { Value: not null } kv ? kv : null;

    /// <summary>Returns a shallow copy that carries every field, including unknown ones.</summary>
    public AspireResource Copy() => new()
    {
        Type = Type,
        Path = Path,
        Image = Image,
        Env = Env is null ? null : new Dictionary<string, string>(Env),
        Bindings = Bindings,
        ConnectionString = ConnectionString,
        References = References,
        Parent = Parent,
        Annotations = Annotations is null ? null : new Dictionary<string, string>(Annotations),
        Dapr = Dapr,
        DaprComponent = DaprComponent,
        ExtensionData = ExtensionData
    };
}

public sealed class AspireBinding
{
    [JsonPropertyName("scheme")]
    public string? Scheme { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("transport")]
    public string? Transport { get; set; }

    /// <summary>
    /// Port the workload listens on inside its container. Aspire emits this as
    /// <c>targetPort</c>; older manifests and hand-written fixtures use <c>containerPort</c>.
    /// </summary>
    [JsonPropertyName("containerPort")]
    public int? ContainerPort { get; set; }

    /// <summary>
    /// Aspire's name for the container port. Setting it fills <see cref="ContainerPort"/>
    /// unless that was set explicitly, so every target can keep reading ContainerPort.
    /// </summary>
    [JsonPropertyName("targetPort")]
    public int? TargetPort
    {
        get;
        set
        {
            field = value;
            ContainerPort ??= value;
        }
    }

    /// <summary>Host / public port, when Aspire specifies one.</summary>
    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("external")]
    public bool IsExternal { get; set; }

    /// <summary>
    /// True when the binding carries HTTP traffic. Aspire always sets <c>protocol</c> to
    /// <c>tcp</c> and puts http/https in <c>scheme</c> (and <c>transport</c>).
    /// </summary>
    [JsonIgnore]
    public bool IsHttp
        => Scheme is "http" or "https" ||
            Transport is "http" or "http2" ||
            Protocol is "http" or "https";

    /// <summary>The URL scheme to use when building addresses for this binding.</summary>
    [JsonIgnore]
    public string EffectiveScheme => Scheme ?? (IsHttp ? "http" : "tcp");

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
        var browserToken = Secrets.Resolve(db.BrowserToken);
        if (browserToken is not null)
        {
            env["DASHBOARD__FRONTEND__AUTHMODE"] = "BrowserToken";
            env["DASHBOARD__FRONTEND__BROWSERTOKEN"] = browserToken;
        }

        var injected = new Dictionary<string, AspireResource>(manifest.Resources)
        {
            [AspireDashboard.ResourceName] = new()
            {
                Type = AspireDashboard.ResourceType,
                Image = $"{AspireDashboard.Image}:{db.ImageTag}",
                Env = env,
                Bindings = new Dictionary<string, AspireBinding>
                {
                    ["http"] = new() { Scheme = "http", Protocol = "tcp", ContainerPort = db.Port, IsExternal = true },
                    ["otlp"] = new() { Scheme = "grpc", Protocol = "tcp", ContainerPort = db.OtlpPort, IsExternal = true }
                }
            }
        };

        return new AspireManifest { Resources = injected, ExtensionData = manifest.ExtensionData };
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
            .Where(kv => kv.Value.Type == "dapr.v0" && kv.Value.Dapr?.Application is not null)
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

                var folded = resource.Copy();
                folded.Annotations = annotations;
                withoutDaprResources[appName] = folded;
            }
        }

        var working = new AspireManifest { Resources = withoutDaprResources, ExtensionData = manifest.ExtensionData };

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
            if (!resource.IsDeployable)
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

            var annotated = resource.Copy();
            annotated.Annotations = annotations;
            injected[name] = annotated;

            index++;
        }

        return new AspireManifest { Resources = injected, ExtensionData = manifest.ExtensionData };
    }

    // ── Dependency inference ──────────────────────────────────────────────────

    private static readonly Regex s_placeholderRef =
        new(@"\{(?<res>[A-Za-z0-9_-]+)\.", RegexOptions.Compiled);

    /// <summary>
    /// Aspire's manifest has no <c>references</c> field: a dependency shows up as a
    /// <c>{other.bindings.http.url}</c> or <c>{db.connectionString}</c> placeholder.
    /// This fills <see cref="AspireResource.References"/> from those placeholders for every
    /// resource that doesn't already list references. Placeholders that point at
    /// non-workload resources (parameters, values, databases) are followed through to the
    /// workload they ultimately resolve to, so a service depending on
    /// <c>{appdb.connectionString}</c> ends up depending on the postgres container.
    /// </summary>
    public static AspireManifest WithInferredReferences(this AspireManifest manifest)
    {
        var resources = manifest.Resources;
        var result = new Dictionary<string, AspireResource>(resources.Count);

        foreach (var (name, resource) in resources)
        {
            if (resource.References is { Count: > 0 } || !resource.IsDeployable)
            {
                result[name] = resource;
                continue;
            }

            var deps = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var text in PlaceholderSources(resource))
            {
                CollectWorkloads(text, name, resources, deps, depth: 0);
            }

            if (deps.Count == 0)
            {
                result[name] = resource;
                continue;
            }

            var copy = resource.Copy();
            copy.References = [.. deps.Select(d => new AspireReference { Name = d })];
            result[name] = copy;
        }

        return new AspireManifest { Resources = result, ExtensionData = manifest.ExtensionData };
    }

    private static IEnumerable<string> PlaceholderSources(AspireResource resource)
    {
        if (resource.Env is not null)
        {
            foreach (var v in resource.Env.Values)
            {
                yield return v;
            }
        }

        if (resource.ConnectionString is not null)
        {
            yield return resource.ConnectionString;
        }

        if (resource.ExtensionData is not null &&
            resource.ExtensionData.TryGetValue("args", out var args) &&
            args.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in args.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.String)
                {
                    yield return a.GetString()!;
                }
            }
        }
    }

    private static void CollectWorkloads(
        string text, string self, IReadOnlyDictionary<string, AspireResource> resources,
        ISet<string> deps, int depth)
    {
        if (depth > 8)
        {
            return; // guard against cycles between value resources
        }

        foreach (Match m in s_placeholderRef.Matches(text))
        {
            var target = m.Groups["res"].Value;
            if (target == self || !resources.TryGetValue(target, out var res))
            {
                continue;
            }

            if (res.IsDeployable)
            {
                deps.Add(target);
                continue;
            }

            // Follow non-workload resources (value.v0, *.database.v0, …) to what they point at.
            if (res.Parent is not null && resources.TryGetValue(res.Parent, out var parent) && parent.IsDeployable && res.Parent != self)
            {
                deps.Add(res.Parent);
            }

            if (res.ConnectionString is not null)
            {
                CollectWorkloads(res.ConnectionString, self, resources, deps, depth + 1);
            }

            if (res.ExtensionData is not null &&
                res.ExtensionData.TryGetValue("value", out var v) &&
                v.ValueKind == JsonValueKind.String)
            {
                CollectWorkloads(v.GetString()!, self, resources, deps, depth + 1);
            }
        }
    }
}
