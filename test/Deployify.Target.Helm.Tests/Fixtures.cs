using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Deployify.Core.Models;

namespace Deployify.Target.Helm.Tests;

internal static class Fixtures
{
    /// <summary>A project with an external HTTP endpoint, a worker, and a container pinned to a tag.</summary>
    public static AspireManifest Manifest() => new()
    {
        Resources = new Dictionary<string, AspireResource>
        {
            ["api"] = new()
            {
                Type = "project.v0",
                Path = "Api/Api.csproj",
                Env = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Production" },
                Bindings = new Dictionary<string, AspireBinding>
                {
                    ["http"] = new() { ContainerPort = 8080, Protocol = "http", IsExternal = true }
                }
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

    /// <summary>A container whose env holds a secret parameter.</summary>
    public static AspireManifest ManifestWithSecret() => JsonSerializer.Deserialize<AspireManifest>("""
        {
          "resources": {
            "pg-password": {
              "type": "parameter.v0",
              "value": "{pg-password.inputs.value}",
              "inputs": { "value": { "type": "string", "secret": true } }
            },
            "pg": {
              "type": "container.v0",
              "image": "postgres:17",
              "env": { "POSTGRES_PASSWORD": "{pg-password.value}", "PLAIN": "hello" },
              "bindings": { "tcp": { "scheme": "tcp", "protocol": "tcp", "transport": "tcp", "targetPort": 5432 } }
            }
          }
        }
        """)!;

    public const string Secret = "S3cret-pw";

    public static EnvironmentConfig Config(
        string prefix = "test", string? domain = null, string? context = null, object? helm = null)
    {
        var config = new EnvironmentConfig
        {
            Target = DeploymentTargetType.Helm,
            ContainerRegistry = "registry.example.com/myapp",
            ImageTag = "v1.0.0",
            OutputPath = "deployify-output"
        };
        config.TargetConfig["kubernetes"] = Element(new
        {
            namespacePrefix = prefix, ingressBaseDomain = domain, context,
            generateIngress = domain is not null, defaultReplicas = 2,
            cpuRequest = "100m", cpuLimit = "500m", memoryRequest = "128Mi", memoryLimit = "512Mi",
            ingressClassName = "nginx"
        });
        if (helm is not null)
        {
            config.TargetConfig["helm"] = Element(helm);
        }

        config.Parameters["pg-password"] = Secret;
        return config;
    }

    private static JsonElement Element(object value) => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deployify-tests-" + System.IO.Path.GetRandomFileName());

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Read(params string[] parts) => System.IO.File.ReadAllText(File(parts));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
