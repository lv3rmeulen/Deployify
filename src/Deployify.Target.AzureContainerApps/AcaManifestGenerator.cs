using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Target.AzureContainerApps;

/// <summary>
/// Generates Azure Container Apps JSON spec files from Aspire resources.
///
/// Output per service:
///   {outputDir}/{serviceName}.containerapp.json
///
/// The JSON matches the `az containerapp create --yaml` / ARM template shape
/// and can be applied directly via:
///   az containerapp create --resource-group rg --environment env \
///       --yaml {service}.containerapp.json
/// </summary>
public sealed class AcaManifestGenerator
{
    public IReadOnlyList<AcaApp> Generate(
        AspireManifest manifest,
        EnvironmentConfig config,
        AcaTargetConfig acaConfig)
    {
        var apps = new List<AcaApp>();
        var ports = ServicePorts.Assign(manifest, acaConfig.BasePort);

        // Apps in one environment reach each other's HTTP ingress at http://<app-name>;
        // the ingress listens on 80 and forwards to the target port.
        var resolver = new PlaceholderResolver(manifest, ports, new EndpointStrategy
        {
            Port = (res, binding, containerPort) =>
                manifest.Resources[res].Bindings![binding].IsHttp ? 80 : containerPort,
            Url = (res, _, scheme, _) => scheme is "http" or "https" ? $"http://{res}" : null
        });

        foreach (var (name, resource) in manifest.Resources)
        {
            if (!resource.IsDeployable)
            {
                continue;
            }

            config.ServiceOverrides.TryGetValue(name, out var overrides);

            var app = new AcaApp
            {
                Name = name,
                ImageTag = overrides?.ImageTag ?? config.ImageTag,
                MinReplicas = acaConfig.MinReplicas,
                MaxReplicas = overrides?.Count ?? acaConfig.MaxReplicas,
                Cpu = acaConfig.DefaultCpu,
                Memory = acaConfig.DefaultMemory,
                IsProjectService = resource.Type.StartsWith("project.", StringComparison.Ordinal),
                Dependencies = resource.References?.Select(r => r.Name).ToList() ?? [],
                IngressType = acaConfig.IngressType
            };

            app.Image = app.IsProjectService
                ? config.ContainerRegistry is not null
                    ? $"{config.ContainerRegistry.TrimEnd('/')}/{name}"
                    : name
                : resource.Image;

            foreach (var (k, v) in resolver.ResolveAll(resource.Env))
            {
                app.Env[k] = v;
            }

            if (overrides?.Env is not null)
            {
                foreach (var (k, v) in overrides.Env)
                {
                    app.Env[k] = v;
                }
            }

            if (resource.Bindings is not null)
            {
                foreach (var (label, binding) in resource.Bindings)
                {
                    app.Ports.Add(new AcaPort
                    {
                        Name = label,
                        ContainerPort = ports[(name, label)],
                        Protocol = binding.IsHttp ? "http" : binding.Protocol?.ToLowerInvariant() ?? "tcp",
                        IsHttp = binding.IsHttp,
                        IsExternal = binding.IsExternal
                    });
                }
            }

            // Dapr
            if (resource.HasDaprSidecar)
            {
                app.DaprEnabled = true;
                app.DaprAppId = resource.DaprAppId ?? name;
                app.DaprAppPort = resource.HttpBinding is { } http ? ports[(name, http.Key)] : resource.DaprAppPort;
            }

            apps.Add(app);
        }

        Unresolved = [.. resolver.Unresolved];
        return apps;
    }

    /// <summary>Placeholders from the last <see cref="Generate"/> call that could not be resolved.</summary>
    public IReadOnlyList<string> Unresolved { get; private set; } = [];

    public async Task<string> WriteAppSpecAsync(
        AcaApp app,
        EnvironmentConfig config,
        AcaTargetConfig acaConfig,
        string outputDirectory,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var spec = BuildAppSpec(app, acaConfig);
        var json = JsonSerializer.Serialize(spec, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        var path = Path.Combine(outputDirectory, $"{app.Name}.containerapp.json");
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct);
        app.SpecFilePath = path;
        return path;
    }

    // ── Spec builder ──────────────────────────────────────────────────────────

    private static object BuildAppSpec(AcaApp app, AcaTargetConfig acaConfig)
    {
        var imageRef = ImageReference.Format(app.Image!, app.ImageTag);

        var container = new Dictionary<string, object?>
        {
            ["name"] = app.Name,
            ["image"] = imageRef,
            ["resources"] = new Dictionary<string, object>
            {
                ["cpu"] = app.Cpu,
                ["memory"] = app.Memory
            }
        };

        if (app.Env.Count > 0)
        {
            container["env"] = app.Env
                .Select(kvp => new Dictionary<string, string>
                {
                    ["name"] = kvp.Key,
                    ["value"] = kvp.Value
                }).ToList();
        }

        var template = new Dictionary<string, object>
        {
            ["containers"] = new[] { container },
            ["scale"] = new Dictionary<string, object>
            {
                ["minReplicas"] = app.MinReplicas,
                ["maxReplicas"] = app.MaxReplicas
            }
        };

        var properties = new Dictionary<string, object?>
        {
            ["configuration"] = BuildConfiguration(app, acaConfig),
            ["template"] = template
        };
        // Without a subscription ID the environment ID would be malformed. Plan and apply
        // fill SubscriptionId from `az account show` when it isn't configured.
        if (!string.IsNullOrWhiteSpace(acaConfig.SubscriptionId))
        {
            properties["environmentId"] =
                $"/subscriptions/{acaConfig.SubscriptionId}/resourceGroups/{acaConfig.ResourceGroup}/providers/Microsoft.App/managedEnvironments/{acaConfig.EnvironmentName}";
        }

        if (app.DaprEnabled)
        {
            properties["dapr"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["appId"] = app.DaprAppId ?? app.Name,
                ["appPort"] = app.DaprAppPort
            };
        }

        var spec = new Dictionary<string, object?>
        {
            ["location"] = acaConfig.Location,
            ["name"] = app.Name,
            ["type"] = "Microsoft.App/containerApps",
            ["properties"] = properties
        };

        // The registry entry refers to an identity, so the app must actually have one.
        if (acaConfig.AcrLoginServer is not null)
        {
            spec["identity"] = acaConfig.AcrIdentityResourceId is not null
                ? new Dictionary<string, object>
                {
                    ["type"] = "UserAssigned",
                    ["userAssignedIdentities"] = new Dictionary<string, object>
                    {
                        [acaConfig.AcrIdentityResourceId] = new Dictionary<string, object>()
                    }
                }
                : new Dictionary<string, object> { ["type"] = "SystemAssigned" };
        }

        return spec;
    }

    private static Dictionary<string, object?> BuildConfiguration(AcaApp app, AcaTargetConfig acaConfig)
    {
        var config = new Dictionary<string, object?>();

        // Ingress: public only when Aspire marked the binding external; other HTTP services
        // get internal ingress so peers in the environment can still call them.
        var httpPort = app.Ports.FirstOrDefault(p => p.IsHttp);
        if (app.IngressType != AcaIngressType.None && httpPort is not null)
        {
            config["ingress"] = new Dictionary<string, object>
            {
                ["external"] = app.IngressType == AcaIngressType.External && httpPort.IsExternal,
                ["targetPort"] = httpPort.ContainerPort,
                ["transport"] = "http",
                // Internal calls use plain http://<app>; only external traffic is forced to HTTPS.
                ["allowInsecure"] = !(app.IngressType == AcaIngressType.External && httpPort.IsExternal)
            };
        }

        // ACR registry credential
        if (acaConfig.AcrLoginServer is not null)
        {
            config["registries"] = new[]
            {
                new Dictionary<string, string>
                {
                    ["server"] = acaConfig.AcrLoginServer,
                    ["identity"] = acaConfig.AcrIdentityResourceId ?? "system"
                }
            };
        }

        return config;
    }
}
