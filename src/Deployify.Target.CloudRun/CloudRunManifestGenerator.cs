using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;

namespace Deployify.Target.CloudRun;

/// <summary>One Cloud Run service, derived from an Aspire resource.</summary>
public sealed class CloudRunService
{
    public string Name { get; set; } = string.Empty;
    public string? Image { get; set; }
    public string ImageTag { get; set; } = "latest";
    public int Port { get; set; }

    /// <summary>The Aspire binding is external: the service is public (ingress <c>all</c>).</summary>
    public bool External { get; set; }

    public int MinInstances { get; set; }
    public int MaxInstances { get; set; } = 3;
    public string Cpu { get; set; } = "1";
    public string Memory { get; set; } = "512Mi";
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Env vars whose value contains a secret parameter; each becomes a Secret Manager secret.</summary>
    public Dictionary<string, ResolvedValue> SecretEnv { get; set; } = [];

    public List<string> Dependencies { get; set; } = [];
    public bool IsProject { get; set; }
    public RolloutStrategy Rollout { get; set; } = RolloutStrategy.Rolling;
}

/// <summary>
/// Generates a Cloud Run service spec (Knative <c>serving.knative.dev/v1</c> Service, as JSON)
/// per HTTP service: <c>{service}.service.json</c>, applied with <c>gcloud run services replace</c>.
/// Services reach each other at their <c>https://{service}-{projectNumber}.{region}.run.app</c>
/// address. Secret env vars are Secret Manager references, never values.
/// </summary>
public sealed class CloudRunManifestGenerator
{
    public const string ManagedByLabel = "deployify-managed-by";
    public const string ManagedByValue = "deployify";
    public const string EnvironmentLabel = "deployify-env";
    public const string SpecLabel = "deployify-spec";
    public const string ServiceLabel = "deployify-service";

    /// <summary>Tag that makes a canary or blue-green candidate reachable at <c>https://candidate---{host}</c>.</summary>
    public const string CandidateTag = "candidate";

    /// <summary>Variables Cloud Run sets itself and refuses in a spec.</summary>
    private static readonly HashSet<string> s_reserved = new(StringComparer.Ordinal) { "PORT", "K_SERVICE", "K_REVISION", "K_CONFIGURATION" };

    public static string SpecFileName(string service) => $"{service}.service.json";

    /// <summary>The host name Cloud Run gives a service: <c>{service}-{projectNumber}.{region}.run.app</c>.</summary>
    public static string Host(string service, string projectNumber, string region) => $"{service}-{projectNumber}.{region}.run.app";

    public async Task<IReadOnlyList<CloudRunService>> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, CloudRunTargetConfig cfg, string? projectNumber,
        CancellationToken ct = default)
    {
        var ports = ServicePorts.Assign(manifest, cfg.BasePort);
        var resolver = new PlaceholderResolver(manifest, ports, new EndpointStrategy
        {
            Host = name => Host(name, projectNumber ?? throw MissingProjectNumber(name), cfg.Region),
            Port = (_, _, _) => 443,
            Url = (res, _, scheme, _) => scheme is "http" or "https"
                ? $"https://{Host(res, projectNumber ?? throw MissingProjectNumber(res), cfg.Region)}"
                : null
        }, ParameterResolution.For(config));

        var services = new List<CloudRunService>();
        var withoutHttp = new List<string>();
        foreach (var (name, resource) in manifest.Resources)
        {
            if (!resource.IsDeployable || config.IgnoreResources.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (resource.HttpBinding is not { } http)
            {
                withoutHttp.Add(name);
                continue;
            }

            config.ServiceOverrides.TryGetValue(name, out var overrides);
            var env = await resolver.ResolveEnvironmentAsync(resource.Env, overrides?.Env, ct);
            services.Add(new CloudRunService
            {
                Name = name,
                Image = ServiceImage.For(name, resource, config),
                ImageTag = overrides?.ImageTag ?? config.ImageTag,
                Port = ports[(name, http.Key)],
                External = http.Value.IsExternal,
                MinInstances = cfg.MinInstances,
                MaxInstances = overrides?.Count ?? cfg.MaxInstances,
                Cpu = cfg.Cpu,
                Memory = cfg.Memory,
                Env = env.Plain.Where(kv => !s_reserved.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                SecretEnv = env.Secrets,
                Dependencies = resource.References?.Select(r => r.Name).ToList() ?? [],
                IsProject = resource.IsProject,
                Rollout = RolloutStrategies.For(config, name)
            });
        }

        if (withoutHttp.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cloud Run runs HTTP services, and {string.Join(", ", withoutHttp)} {(withoutHttp.Count == 1 ? "has" : "have")} no HTTP binding. " +
                "Run them elsewhere (Memorystore, Cloud SQL, a VM), pass their address in as a parameter, and list them in ignoreResources.");
        }

        Unresolved = [.. resolver.Unresolved];
        return services;
    }

    /// <summary>Placeholders from the last <see cref="GenerateAsync"/> that could not be resolved.</summary>
    public IReadOnlyList<string> Unresolved { get; private set; } = [];

    private static InvalidOperationException MissingProjectNumber(string service)
        => new($"Another service refers to {service}'s address, which contains the project number. " +
               "Set cloudrun.projectNumber, or make sure 'gcloud projects describe' works so Deployify can look it up.");

    /// <summary>Secret Manager secret holding env var <paramref name="key"/> of <paramref name="service"/>.</summary>
    public static string SecretName(string prefix, string service, string key)
    {
        var name = $"{prefix}-{service}-{key}";
        var clean = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
        return clean.Length > 255 ? clean[..255] : clean;
    }

    /// <summary>Label values: lowercase letters, digits, '-' and '_', at most 63 characters.</summary>
    public static string LabelValue(string value)
    {
        var clean = new string([.. value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
        return clean.Length > 63 ? clean[..63] : clean;
    }

    /// <summary>The service spec, with the spec hash label set (see <see cref="SpecLabel"/>).</summary>
    public static JsonObject BuildSpec(CloudRunService svc, CloudRunTargetConfig cfg, string secretPrefix)
    {
        var labels = new JsonObject { [ManagedByLabel] = ManagedByValue };
        if (cfg.DeployifyEnvironment is { Length: > 0 } environment)
        {
            labels[EnvironmentLabel] = LabelValue(environment);
        }

        var env = new JsonArray();
        foreach (var (key, value) in svc.Env.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            env.Add(new JsonObject { ["name"] = key, ["value"] = value });
        }

        foreach (var key in svc.SecretEnv.Keys.Order(StringComparer.Ordinal))
        {
            env.Add(new JsonObject
            {
                ["name"] = key,
                ["valueFrom"] = new JsonObject
                {
                    ["secretKeyRef"] = new JsonObject { ["name"] = SecretName(secretPrefix, svc.Name, key), ["key"] = "latest" }
                }
            });
        }

        var container = new JsonObject
        {
            ["image"] = ImageReference.Format(svc.Image!, svc.ImageTag),
            ["ports"] = new JsonArray(new JsonObject { ["name"] = "http1", ["containerPort"] = svc.Port }),
            ["resources"] = new JsonObject { ["limits"] = new JsonObject { ["cpu"] = svc.Cpu, ["memory"] = svc.Memory } }
        };
        if (env.Count > 0)
        {
            container["env"] = env;
        }

        var templateAnnotations = new JsonObject
        {
            ["autoscaling.knative.dev/minScale"] = svc.MinInstances.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["autoscaling.knative.dev/maxScale"] = svc.MaxInstances.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (cfg.VpcConnector is { Length: > 0 } connector)
        {
            templateAnnotations["run.googleapis.com/vpc-access-connector"] = connector;
            templateAnnotations["run.googleapis.com/vpc-access-egress"] = cfg.VpcEgress;
        }
        else if (cfg.Network is { Length: > 0 } network)
        {
            var nic = new JsonObject { ["network"] = network };
            if (cfg.Subnet is { Length: > 0 } subnet)
            {
                nic["subnetwork"] = subnet;
            }

            templateAnnotations["run.googleapis.com/network-interfaces"] = new JsonArray(nic).ToJsonString();
            templateAnnotations["run.googleapis.com/vpc-access-egress"] = cfg.VpcEgress;
        }

        var podSpec = new JsonObject();
        if (cfg.ServiceAccount is { Length: > 0 } account)
        {
            podSpec["serviceAccountName"] = account;
        }

        if (cfg.Concurrency is { } concurrency)
        {
            podSpec["containerConcurrency"] = concurrency;
        }

        podSpec["containers"] = new JsonArray(container);

        var spec = new JsonObject
        {
            ["apiVersion"] = "serving.knative.dev/v1",
            ["kind"] = "Service",
            ["metadata"] = new JsonObject
            {
                ["name"] = svc.Name,
                ["labels"] = labels,
                ["annotations"] = new JsonObject { ["run.googleapis.com/ingress"] = svc.External ? "all" : cfg.InternalIngress }
            },
            ["spec"] = new JsonObject
            {
                ["template"] = new JsonObject
                {
                    ["metadata"] = new JsonObject { ["labels"] = labels.DeepClone(), ["annotations"] = templateAnnotations },
                    ["spec"] = podSpec
                },
                ["traffic"] = new JsonArray(new JsonObject { ["latestRevision"] = true, ["percent"] = 100 })
            }
        };

        spec["metadata"]!["labels"]![SpecLabel] = Hash(spec);
        return spec;
    }

    /// <summary>A short hash of the spec (without its own hash label), for plan and diff.</summary>
    internal static string Hash(JsonObject spec)
    {
        var copy = spec.DeepClone().AsObject();
        copy["metadata"]?["labels"]?.AsObject().Remove(SpecLabel);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(copy.ToJsonString()));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true };

    public async Task<string> WriteSpecAsync(
        CloudRunService svc, EnvironmentConfig config, CloudRunTargetConfig cfg, string secretPrefix, string outputDirectory,
        CancellationToken ct = default)
    {
        if (svc.SecretEnv.Count > 0 && config.Secrets.Backend != SecretBackendType.GcpSecretManager)
        {
            throw new InvalidOperationException(
                $"{svc.Name} has secret values, and Cloud Run reads secrets from Secret Manager: set secrets.backend to GcpSecretManager.");
        }

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, SpecFileName(svc.Name));
        await File.WriteAllTextAsync(path, BuildSpec(svc, cfg, secretPrefix).ToJsonString(s_write) + "\n", new UTF8Encoding(false), ct);
        if (svc.SecretEnv.Count > 0)
        {
            SecretLeakScanner.EnsureClean([path], SecretKeys.SecretValues(svc.SecretEnv.Values));
        }

        return path;
    }

    /// <summary>
    /// The spec as it is deployed: every secret reference pinned to the version just written, so
    /// a revision keeps its secrets (and going back to it brings back their old values), and for
    /// a canary or blue-green candidate the traffic left on <paramref name="stableRevision"/>, with
    /// the new revision tagged <see cref="CandidateTag"/>.
    /// </summary>
    public static JsonObject ForDeploy(JsonObject spec, IReadOnlyDictionary<string, string> secretVersions, string? stableRevision)
    {
        var copy = spec.DeepClone().AsObject();
        var containers = copy["spec"]?["template"]?["spec"]?["containers"]?.AsArray() ?? [];
        foreach (var container in containers)
        {
            foreach (var env in container?["env"]?.AsArray() ?? [])
            {
                if (env?["valueFrom"]?["secretKeyRef"] is JsonObject reference &&
                    (string?)reference["name"] is { } name && secretVersions.TryGetValue(name, out var version))
                {
                    reference["key"] = version;
                }
            }
        }

        if (stableRevision is not null)
        {
            copy["spec"]!["traffic"] = new JsonArray(
                new JsonObject { ["revisionName"] = stableRevision, ["percent"] = 100 },
                new JsonObject { ["latestRevision"] = true, ["percent"] = 0, ["tag"] = CandidateTag });
        }

        return copy;
    }
}
