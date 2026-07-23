using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        foreach (var (name, resource) in manifest.Resources)
        {
            if (resource.Type is "value.v0" or "parameter.v0" or "dapr.v0" or "dapr.component.v0")
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
                IsProjectService = resource.Type.StartsWith("project.v"),
                Dependencies = resource.References?.Select(r => r.Name).ToList() ?? [],
                IngressType = acaConfig.IngressType
            };

            // Resolve image
            if (resource.Type.StartsWith("container.v"))
            {
	            app.Image = resource.Image;
            }
            else if (resource.Type.StartsWith("project.v"))
            {
	            app.Image = config.ContainerRegistry is not null
		            ? $"{config.ContainerRegistry.TrimEnd('/')}/{name}"
		            : name;
            }

            // Env vars
            if (resource.Env is not null)
            {
	            foreach (var (k, v) in resource.Env)
	            {
		            app.Env[k] = v;
	            }
            }

            if (overrides?.Env is not null)
            {
	            foreach (var (k, v) in overrides.Env)
	            {
		            app.Env[k] = v;
	            }
            }

            // Ports
            if (resource.Bindings is not null)
            {
                foreach (var (label, binding) in resource.Bindings)
                {
                    app.Ports.Add(new AcaPort
                    {
                        Name = label,
                        ContainerPort = binding.ContainerPort ?? 80,
                        Protocol = binding.Protocol?.ToLowerInvariant() ?? "http"
                    });
                }
            }

            // Dapr
            if (resource.HasDaprSidecar)
            {
                app.DaprEnabled = true;
                app.DaprAppId = resource.DaprAppId ?? name;
                app.DaprAppPort = resource.DaprAppPort;
            }

            apps.Add(app);
        }

        return apps;
    }

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
        await File.WriteAllTextAsync(path, json, Encoding.UTF8, ct);
        app.SpecFilePath = path;
        return path;
    }

    // ── Spec builder ──────────────────────────────────────────────────────────

    private static object BuildAppSpec(AcaApp app, AcaTargetConfig acaConfig)
    {
        var imageRef = $"{app.Image}:{app.ImageTag}";

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
            ["environmentId"] = $"/subscriptions/{acaConfig.SubscriptionId}/resourceGroups/{acaConfig.ResourceGroup}/providers/Microsoft.App/managedEnvironments/{acaConfig.EnvironmentName}",
            ["configuration"] = BuildConfiguration(app, acaConfig),
            ["template"] = template
        };

        if (app.DaprEnabled)
        {
            properties["dapr"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["appId"] = app.DaprAppId ?? app.Name,
                ["appPort"] = app.DaprAppPort
            };
        }

        return new Dictionary<string, object?>
        {
            ["location"] = acaConfig.Location,
            ["name"] = app.Name,
            ["type"] = "Microsoft.App/containerApps",
            ["properties"] = properties
        };
    }

    private static Dictionary<string, object?> BuildConfiguration(AcaApp app, AcaTargetConfig acaConfig)
    {
        var config = new Dictionary<string, object?>();

        // Ingress
        var httpPort = app.Ports.FirstOrDefault(p => p.IsHttp);
        if (app.IngressType != AcaIngressType.None && httpPort is not null)
        {
            config["ingress"] = new Dictionary<string, object>
            {
                ["external"] = app.IngressType == AcaIngressType.External,
                ["targetPort"] = httpPort.ContainerPort,
                ["transport"] = "http",
                ["allowInsecure"] = false
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
                    ["identity"] = "system"
                }
            };
        }

        return config;
    }
}
