using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.SecretBackends.KeyVault;

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
        }, ParameterResolution.For(config));

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

            // Values that contain a secret parameter are kept apart and stored the target's own way.
            var env = resolver.ResolveEnvironment(resource.Env, overrides?.Env);
            app.Env = env.Plain;
            app.SecretEnv = env.Secrets;

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

        var keyVault = config.Secrets.Backend == SecretBackendType.KeyVault ? config.Secrets.KeyVault : null;
        var spec = BuildAppSpec(app, acaConfig, keyVault);
        var json = JsonSerializer.Serialize(spec, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        var path = Path.Combine(outputDirectory, $"{app.Name}.containerapp.json");
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct);
        if (app.SecretEnv.Count > 0 && keyVault is null && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        if (config.Secrets.IsExternal && app.SecretEnv.Count > 0)
        {
            SecretLeakScanner.EnsureClean([path], SecretKeys.SecretValues(app.SecretEnv.Values));
        }

        app.SpecFilePath = path;
        return path;
    }

    // ── Spec builder ──────────────────────────────────────────────────────────

    private static object BuildAppSpec(AcaApp app, AcaTargetConfig acaConfig, KeyVaultConfig? keyVault)
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

        var secretNames = SecretNames(app);
        if (app.Env.Count > 0 || app.SecretEnv.Count > 0)
        {
            container["env"] = app.Env
                .Select(kvp => new Dictionary<string, string>
                {
                    ["name"] = kvp.Key,
                    ["value"] = kvp.Value
                })
                .Concat(app.SecretEnv.Keys.Select(key => new Dictionary<string, string>
                {
                    ["name"] = key,
                    ["secretRef"] = secretNames[key]
                }))
                .ToList();
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
            ["configuration"] = BuildConfiguration(app, acaConfig, keyVault),
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

        if (BuildIdentity(app, acaConfig, keyVault) is { } identity)
        {
            spec["identity"] = identity;
        }

        return spec;
    }

    /// <summary>
    /// The registry entry and Key Vault secret references each name an identity, so the app
    /// must actually have them: every user-assigned identity they name, plus the system one
    /// when either uses "system".
    /// </summary>
    private static Dictionary<string, object>? BuildIdentity(AcaApp app, AcaTargetConfig acaConfig, KeyVaultConfig? keyVault)
    {
        var userAssigned = new List<string>();
        var system = false;

        void Use(string? identityResourceId)
        {
            if (identityResourceId is null)
            {
                system = true;
            }
            else if (!userAssigned.Contains(identityResourceId, StringComparer.OrdinalIgnoreCase))
            {
                userAssigned.Add(identityResourceId);
            }
        }

        if (acaConfig.AcrLoginServer is not null)
        {
            Use(acaConfig.AcrIdentityResourceId);
        }

        if (keyVault is not null && app.SecretEnv.Count > 0)
        {
            Use(keyVault.IdentityResourceId);
        }

        if (!system && userAssigned.Count == 0)
        {
            return null;
        }

        var identity = new Dictionary<string, object>
        {
            ["type"] = (system, userAssigned.Count > 0) switch
            {
                (true, true) => "SystemAssigned,UserAssigned",
                (true, false) => "SystemAssigned",
                _ => "UserAssigned"
            }
        };
        if (userAssigned.Count > 0)
        {
            identity["userAssignedIdentities"] = userAssigned.ToDictionary(id => id, _ => (object)new Dictionary<string, object>());
        }

        return identity;
    }

    /// <summary>
    /// Container Apps secret names for each secret env var. Secret names may only contain
    /// lowercase letters, digits, '-' and '.', so <c>ConnectionStrings__db</c> becomes
    /// <c>connectionstrings--db</c>; collisions get a numeric suffix.
    /// </summary>
    public static Dictionary<string, string> SecretNames(AcaApp app)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in app.SecretEnv.Keys.Order(StringComparer.Ordinal))
        {
            var name = new string([.. key.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '-')])
                .Trim('-', '.');
            if (name.Length == 0)
            {
                name = "secret";
            }

            if (name.Length > 240)
            {
                name = name[..240].TrimEnd('-', '.');
            }

            var candidate = name;
            for (var i = 2; !used.Add(candidate); i++)
            {
                candidate = $"{name}-{i}";
            }

            result[key] = candidate;
        }

        return result;
    }

    private static Dictionary<string, object?> BuildConfiguration(AcaApp app, AcaTargetConfig acaConfig, KeyVaultConfig? keyVault)
    {
        var config = new Dictionary<string, object?>();

        // Secret env values are declared as app secrets and referenced with secretRef. With
        // Key Vault, each app secret is a reference that Container Apps resolves with the
        // managed identity; the spec only holds the secret's URL.
        if (app.SecretEnv.Count > 0)
        {
            var names = SecretNames(app);
            config["secrets"] = app.SecretEnv
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp => keyVault is null
                    ? new Dictionary<string, string>
                    {
                        ["name"] = names[kvp.Key],
                        ["value"] = kvp.Value.ToString()
                    }
                    : new Dictionary<string, string>
                    {
                        ["name"] = names[kvp.Key],
                        ["keyVaultUrl"] = KeyVaultSecrets.SecretUrl(keyVault,
                            AcaKeyVault.AppSecretName(keyVault, acaConfig, app.Name, names[kvp.Key])),
                        ["identity"] = keyVault.IdentityResourceId ?? "system"
                    })
                .ToList();
        }

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
