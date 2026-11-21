using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;

namespace Deployify.Target.DigitalOcean;

/// <summary>How a manifest resource ends up in the app spec.</summary>
public enum ComponentKind
{
    /// <summary>A service with a public HTTP route.</summary>
    PublicService,

    /// <summary>A service only the app's other components can reach, on its internal ports.</summary>
    InternalService,

    /// <summary>A worker: no ports, nothing reaches it.</summary>
    Worker,

    /// <summary>A database component (dev database or attached cluster).</summary>
    Database
}

/// <summary>One manifest resource and the component it became.</summary>
public sealed class GeneratedComponent
{
    public required string Resource { get; init; }
    public required string Component { get; init; }
    public required ComponentKind Kind { get; init; }
    public string? Image { get; init; }
    public string? Route { get; init; }
    public int InstanceCount { get; init; } = 1;
    public string? InstanceSize { get; init; }
    public List<string> Ports { get; init; } = [];
    public Dictionary<string, string> DisplayEnv { get; init; } = [];
    public List<string> Dependencies { get; init; } = [];
}

/// <summary>The result of one generation run.</summary>
public sealed class GeneratedApp
{
    /// <summary>The spec to send to App Platform, secrets included.</summary>
    public required AppSpec Spec { get; init; }

    /// <summary>The same spec with secret values masked, for files and output.</summary>
    public required AppSpec DisplaySpec { get; init; }

    public required IReadOnlyList<GeneratedComponent> Components { get; init; }

    public required IReadOnlyList<ManagedDatabase> Databases { get; init; }

    /// <summary>Things that make the spec wrong or undeployable. Validate reports them; plan and apply refuse to go on.</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>Things that deploy but behave differently from the AppHost (lost args, no persistent volumes).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public required IReadOnlyList<string> Unresolved { get; init; }

    public string FileName => $"{Spec.Name}.app.json";
}

/// <summary>
/// Builds one App Platform app spec from the Aspire manifest. The spec is a JSON document
/// that also works with <c>doctl apps create --spec</c>.
/// </summary>
public sealed class AppSpecGenerator
{
    private const int BasePort = 8080;

    public async Task<GeneratedApp> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, DoTargetConfig cfg,
        Func<string, string?>? getVariable = null, CancellationToken ct = default)
    {
        getVariable ??= Environment.GetEnvironmentVariable;
        manifest = manifest.WithInferredReferences();
        var problems = new List<string>();
        var warnings = new List<string>();

        var appName = cfg.EffectiveAppName;
        if (!Names.IsValid(appName))
        {
            problems.Add($"App name '{appName}' isn't valid: use 2–32 lowercase letters, digits and '-', starting with a letter " +
                         "and not ending with '-'. Set TargetConfig.digitalocean.AppName.");
        }

        var databases = ManagedDatabases.Find(manifest, cfg, problems);
        var managed = databases.Select(d => d.Resource).ToHashSet(StringComparer.Ordinal);

        var ports = ServicePorts.Assign(manifest, BasePort);
        var resolver = new ExpressionResolver(manifest, ports, new EndpointStrategy
        {
            Host = Names.Component,
            // Components reach each other over the app's private network at http://<component>:<port>.
            Url = (res, _, scheme, port) => scheme is "http" or "https" ? $"http://{Names.Component(res)}:{port}" : null
        }, ParameterResolution.For(config), ManagedDatabases.Handlers(databases));

        var credentials = cfg.RegistryCredentialsVariable is { Length: > 0 } variable ? getVariable(variable) : null;
        var workloads = manifest.Resources
            .Where(kv => kv.Value.IsDeployable && !managed.Contains(kv.Key))
            .ToList();
        var routes = AssignRoutes(workloads, cfg);

        var components = new List<GeneratedComponent>();
        var services = new List<ServiceSpec>();
        var displayServices = new List<ServiceSpec>();
        var workers = new List<WorkerSpec>();
        var displayWorkers = new List<WorkerSpec>();
        var componentNames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, resource) in workloads)
        {
            var component = Names.Component(name);
            if (componentNames.TryGetValue(component, out var other))
            {
                problems.Add($"{name} and {other} both become component '{component}'; rename one of them.");
                continue;
            }

            componentNames[component] = name;
            if (component != name)
            {
                warnings.Add($"{name} is deployed as component '{component}': App Platform names are lowercase letters, digits and '-'.");
            }

            config.ServiceOverrides.TryGetValue(name, out var overrides);
            cfg.Services.TryGetValue(name, out var serviceCfg);

            // Image
            var image = ServiceImage.For(name, resource, config);
            ImageSourceSpec? source = null;
            if (image is null)
            {
                problems.Add($"{name}: has no image.");
            }
            else
            {
                source = DoImage.ToSource(image, ServiceImage.Tag(name, config), out var imageError);
                if (imageError is not null)
                {
                    problems.Add($"{name}: {imageError}");
                }
                else if (resource.IsBuiltImage && source!.RegistryType != "DOCR" && credentials is not null)
                {
                    source.RegistryCredentials = credentials;
                }
            }

            source ??= new ImageSourceSpec { RegistryType = "DOCKER_HUB", Registry = "library", Repository = "invalid" };

            // Environment
            var env = await ResolveEnvironmentAsync(resolver, resource.Env, overrides?.Env, ct);
            var overridden = overrides?.Env.Keys.ToHashSet(StringComparer.Ordinal) ?? [];
            var manifestPlain = env.Plain.Where(kv => !overridden.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            foreach (var key in ManagedDatabases.RewriteConnectionProperties(manifestPlain, databases))
            {
                env.SetPlain(key, manifestPlain[key]);
            }
            var envs = new List<EnvSpec>();
            var displayEnvs = new List<EnvSpec>();
            foreach (var (key, value) in env.Plain.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                envs.Add(new EnvSpec { Key = key, Value = value });
                displayEnvs.Add(new EnvSpec { Key = key, Value = value });
            }

            foreach (var (key, value) in env.Secrets.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                envs.Add(new EnvSpec { Key = key, Value = value.ToString(), Type = "SECRET" });
                displayEnvs.Add(new EnvSpec { Key = key, Value = value.ToDisplayString(), Type = "SECRET" });
            }

            foreach (var key in envs.Select(e => e.Key).Where(k => !EnvKey.IsValid(k)))
            {
                problems.Add($"{name}: environment variable '{key}' isn't a valid App Platform name (letters, digits and '_', not starting with a digit).");
            }

            // Ports
            var (httpBinding, httpPort, otherPorts) = Ports(name, resource, ports);
            var isPublic = httpBinding is { IsExternal: true };
            var kind = isPublic ? ComponentKind.PublicService
                : otherPorts.Count > 0 || httpPort is not null ? ComponentKind.InternalService
                : ComponentKind.Worker;

            var instanceCount = overrides?.Count ?? serviceCfg?.InstanceCount ?? cfg.DefaultInstanceCount;
            var instanceSize = serviceCfg?.InstanceSize ?? cfg.DefaultInstanceSize;

            WarnAboutDifferences(name, resource, warnings);

            if (kind == ComponentKind.Worker)
            {
                workers.Add(Fill(new WorkerSpec { Name = component, Image = source }, instanceSize, instanceCount, envs));
                displayWorkers.Add(Fill(new WorkerSpec { Name = component, Image = Masked(source) }, instanceSize, instanceCount, displayEnvs));
            }
            else
            {
                var internalPorts = kind == ComponentKind.PublicService
                    ? otherPorts
                    : [.. otherPorts.Append(httpPort ?? 0).Where(p => p > 0).Distinct().Order()];
                ServiceSpec Service(ImageSourceSpec img, List<EnvSpec> e) => Fill(new ServiceSpec
                {
                    Name = component,
                    Image = img,
                    HttpPort = kind == ComponentKind.PublicService ? httpPort : null,
                    InternalPorts = internalPorts.Count > 0 ? internalPorts : null,
                    HealthCheck = serviceCfg?.HealthCheckPath is { Length: > 0 } path
                        ? new HealthCheckSpec { HttpPath = path, Port = kind == ComponentKind.PublicService ? null : httpPort }
                        : null
                }, instanceSize, instanceCount, e);

                services.Add(Service(source, envs));
                displayServices.Add(Service(Masked(source), displayEnvs));
            }

            components.Add(new GeneratedComponent
            {
                Resource = name,
                Component = component,
                Kind = kind,
                Image = image is null ? null : ImageReference.Format(image, ServiceImage.Tag(name, config)),
                Route = kind == ComponentKind.PublicService ? routes.GetValueOrDefault(name) : null,
                InstanceCount = instanceCount,
                InstanceSize = instanceSize,
                Ports = [.. new[] { httpPort }.OfType<int>().Concat(otherPorts).Distinct().Select(p => p.ToString())],
                DisplayEnv = ResolvedEnvironment.ForDisplay(env.Plain, env.Secrets),
                Dependencies = resource.References?.Select(r => r.Name).ToList() ?? []
            });
        }

        foreach (var db in databases)
        {
            if (componentNames.TryGetValue(db.Component, out var other))
            {
                problems.Add($"{db.Resource} and {other} both become component '{db.Component}'; rename one of them.");
            }

            components.Add(new GeneratedComponent
            {
                Resource = db.Resource,
                Component = db.Component,
                Kind = ComponentKind.Database,
                Image = manifest.Resources[db.Resource].Image
            });
        }

        var publicRoutes = components.Where(c => c.Kind == ComponentKind.PublicService && c.Route is not null).ToList();
        foreach (var duplicate in publicRoutes.GroupBy(c => c.Route).Where(g => g.Count() > 1))
        {
            problems.Add($"Route '{duplicate.Key}' is used by {string.Join(" and ", duplicate.Select(c => c.Resource))}; " +
                         "set TargetConfig.digitalocean.Services.<name>.Route.");
        }

        var ingress = publicRoutes.Count == 0 ? null : new IngressSpec
        {
            Rules =
            [
                .. publicRoutes
                    // Longest prefix first, so "/" doesn't hide the others when read top to bottom.
                    .OrderByDescending(c => c.Route!.Length).ThenBy(c => c.Component, StringComparer.Ordinal)
                    .Select(c => new IngressRuleSpec
                    {
                        Match = new IngressMatchSpec { Path = new IngressPathSpec { Prefix = c.Route! } },
                        Component = new IngressComponentSpec { Name = c.Component }
                    })
            ]
        };

        if (services.Count + workers.Count == 0)
        {
            problems.Add("The manifest has nothing for App Platform to run: an app needs at least one service or worker.");
        }

        AppSpec Spec(List<ServiceSpec> s, List<WorkerSpec> w) => new()
        {
            Name = appName,
            Region = cfg.Region,
            Services = s.Count > 0 ? s : null,
            Workers = w.Count > 0 ? w : null,
            Databases = databases.Count > 0 ? [.. databases.Select(d => d.ToSpec())] : null,
            Ingress = ingress
        };

        return new GeneratedApp
        {
            Spec = Spec(services, workers),
            DisplaySpec = Spec(displayServices, displayWorkers),
            Components = components,
            Databases = databases,
            Problems = problems,
            Warnings = warnings,
            Unresolved = [.. resolver.Unresolved]
        };
    }

    /// <summary>Writes the masked spec. It carries no secret values, so the file is safe to keep and review.</summary>
    public static async Task<string> WriteAsync(GeneratedApp app, string outputDirectory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, app.FileName);
        var json = JsonSerializer.Serialize(app.DisplaySpec, AppSpec.JsonOptions);
        await File.WriteAllTextAsync(path, json + "\n", new UTF8Encoding(false), ct);
        return path;
    }

    private static async Task<ResolvedEnvironment> ResolveEnvironmentAsync(
        ExpressionResolver resolver, IReadOnlyDictionary<string, string>? env,
        IReadOnlyDictionary<string, string>? overrides, CancellationToken ct)
    {
        var result = new ResolvedEnvironment();
        foreach (var (key, value) in env ?? new Dictionary<string, string>())
        {
            result.Set(key, await resolver.ResolveAsync(value, ct));
        }

        foreach (var (key, value) in overrides ?? new Dictionary<string, string>())
        {
            result.SetPlain(key, value);
        }

        return result;
    }

    /// <summary>
    /// The binding that serves public HTTP and the ports everything else listens on. A project's
    /// https binding is left out when it has an http one: nothing listens on it (no certificate).
    /// </summary>
    private static (AspireBinding? Http, int? HttpPort, List<int> Others) Ports(
        string name, AspireResource resource, IReadOnlyDictionary<(string, string), int> ports)
    {
        var bindings = resource.Bindings ?? [];
        var hasHttp = bindings.Any(b => b.Value.Scheme == "http");
        var usable = bindings
            .Where(b => !(resource.IsProject && hasHttp && b.Value.Scheme == "https"))
            .ToList();

        var http = usable
            .Where(b => b.Value.IsHttp)
            .OrderByDescending(b => b.Value.IsExternal)
            .ThenBy(b => b.Value.Scheme == "http" ? 0 : 1)
            .Select(b => (KeyValuePair<string, AspireBinding>?)b)
            .FirstOrDefault();

        int? httpPort = http is { } h ? ports[(name, h.Key)] : null;
        var others = usable
            .Where(b => http is null || b.Key != http.Value.Key)
            .Select(b => ports[(name, b.Key)])
            .Where(p => p != httpPort)
            .Distinct()
            .Order()
            .ToList();

        return (http?.Value, httpPort, others);
    }

    /// <summary>
    /// Public path prefixes: configured routes first, then "/" for the first external service
    /// that has none (when nothing claimed "/"), then "/{component}" for the rest.
    /// </summary>
    private static Dictionary<string, string> AssignRoutes(
        IEnumerable<KeyValuePair<string, AspireResource>> workloads, DoTargetConfig cfg)
    {
        var external = workloads
            .Where(kv => kv.Value.Bindings?.Values.Any(b => b.IsHttp && b.IsExternal) == true)
            .Select(kv => kv.Key)
            .ToList();

        var routes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in external)
        {
            if (cfg.Services.TryGetValue(name, out var s) && s.Route is { Length: > 0 } route)
            {
                routes[name] = route.StartsWith('/') ? route : "/" + route;
            }
        }

        var rootTaken = routes.ContainsValue("/");
        foreach (var name in external.Where(n => !routes.ContainsKey(n)))
        {
            routes[name] = rootTaken ? "/" + Names.Component(name) : "/";
            rootTaken = true;
        }

        return routes;
    }

    private static void WarnAboutDifferences(string name, AspireResource resource, List<string> warnings)
    {
        if (resource.ExtensionData is { } ext)
        {
            if (ext.TryGetValue("args", out var args) && args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0)
            {
                warnings.Add($"{name}: its container args aren't passed on; App Platform runs the image's own command. " +
                             "Bake the arguments into the image to keep them.");
            }

            foreach (var key in new[] { "volumes", "bindMounts" })
            {
                if (ext.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
                {
                    warnings.Add($"{name}: App Platform has no persistent storage, so its {key} are dropped and " +
                                 "anything written there is lost on every deploy and restart.");
                }
            }
        }

        if (resource.HasDaprSidecar)
        {
            warnings.Add($"{name}: App Platform has no Dapr sidecars; it is deployed without one.");
        }
    }

    private static T Fill<T>(T component, string size, int count, List<EnvSpec> envs) where T : ComponentSpec
    {
        component.InstanceSizeSlug = size;
        component.InstanceCount = count;
        component.Envs = envs.Count > 0 ? envs : null;
        return component;
    }

    private static ImageSourceSpec Masked(ImageSourceSpec source) => source.RegistryCredentials is null
        ? source
        : new ImageSourceSpec
        {
            RegistryType = source.RegistryType,
            Registry = source.Registry,
            Repository = source.Repository,
            Tag = source.Tag,
            Digest = source.Digest,
            RegistryCredentials = "****"
        };
}

internal static partial class EnvKey
{
    public static bool IsValid(string key) => Pattern().IsMatch(key);

    [System.Text.RegularExpressions.GeneratedRegex("^[_A-Za-z][_A-Za-z0-9]*$")]
    private static partial System.Text.RegularExpressions.Regex Pattern();
}
