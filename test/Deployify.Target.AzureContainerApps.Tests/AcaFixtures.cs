using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Deployify.Core.Models;

namespace Deployify.Target.AzureContainerApps.Tests;

internal static class AcaFixtures
{
    public static EnvironmentConfig AcaConfig(
        string resourceGroup = "test-rg",
        string location = "eastus",
        string environment = "test-env",
        string subscriptionId = "00000000-0000-0000-0000-000000000000")
        => WithTargetConfig(BaseConfig(), "aca", new AcaTargetConfig
        {
            SubscriptionId = subscriptionId,
            ResourceGroup = resourceGroup,
            Location = location,
            EnvironmentName = environment,
            MinReplicas = 0,
            MaxReplicas = 3,
            DefaultCpu = 0.25,
            DefaultMemory = "0.5Gi"
        });

    public static EnvironmentConfig AcaConfigWithAcr(string acrServer = "myregistry.azurecr.io")
        => WithTargetConfig(BaseConfig(), "aca", new AcaTargetConfig
        {
            SubscriptionId = "00000000-0000-0000-0000-000000000000",
            ResourceGroup = "test-rg",
            Location = "westeurope",
            EnvironmentName = "test-env",
            AcrLoginServer = acrServer
        });

    private static EnvironmentConfig BaseConfig() => new()
    {
        Target = DeploymentTargetType.AzureContainerApps,
        ContainerRegistry = "myregistry.azurecr.io/myapp",
        ImageTag = "v1.0.0",
        OutputPath = "deployify-output"
    };

    private static EnvironmentConfig WithTargetConfig(EnvironmentConfig config, string key, object cfg)
    {
        config.TargetConfig[key] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(cfg));
        return config;
    }

    public static AspireManifest SimpleManifest() => new()
    {
        Resources = new Dictionary<string, AspireResource>
        {
            ["api"] = new()
            {
                Type = "project.v0",
                Path = "Api/Api.csproj",
                Env = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Production" },
                Bindings = new Dictionary<string, AspireBinding> { ["http"] = new() { ContainerPort = 8080, Protocol = "http" } }
            },
            ["worker"] = new()
            {
                Type = "project.v0",
                Path = "Worker/Worker.csproj",
                References = [new AspireReference { Name = "api" }]
            },
            ["redis"] = new()
            {
                Type = "container.v0",
                Image = "redis:7",
                Bindings = new Dictionary<string, AspireBinding> { ["tcp"] = new() { ContainerPort = 6379, Protocol = "tcp" } }
            }
        }
    };

    public static AspireManifest SingleService(
        string name = "myapp", string type = "project.v0", int port = 8080, bool external = false) => new()
    {
        Resources = new Dictionary<string, AspireResource>
        {
            [name] = new()
            {
                Type = type,
                Image = type.StartsWith("container") ? "nginx:latest" : null,
                Bindings = new Dictionary<string, AspireBinding> { ["http"] = new() { ContainerPort = port, Protocol = "http", IsExternal = external } }
            }
        }
    };

    public static AspireManifest ManifestWithIgnoredTypes() => new()
    {
        Resources = new Dictionary<string, AspireResource>
        {
            ["param1"] = new() { Type = "parameter.v0" },
            ["val1"] = new() { Type = "value.v0" },
            ["api"] = new() { Type = "project.v0", Path = "Api/Api.csproj" }
        }
    };

    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "deployify-aca-tests-" + System.IO.Path.GetRandomFileName());

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
