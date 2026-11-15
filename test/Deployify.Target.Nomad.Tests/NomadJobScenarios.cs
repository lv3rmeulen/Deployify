using System.Collections.Generic;
using System.Text.Json;
using Deployify.Core.Models;

namespace Deployify.Target.Nomad.Tests;

/// <summary>
/// Manifests and configs that between them use every part of a generated Nomad job: Docker,
/// Podman and raw_exec, registry auth, service registration with each check type and provider,
/// Dapr, secrets from Nomad Variables, service discovery templates, args and artifacts.
/// </summary>
internal static class NomadJobScenarios
{
    public static readonly string[] Names = ["docker", "podman-no-registration", "rawexec"];

    private const string ManifestJson = """
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
              "connectionString": "Host={pg.bindings.tcp.host};Password={pg-password.value};Opt=a${b}",
              "env": { "POSTGRES_PASSWORD": "{pg-password.value}" },
              "bindings": { "tcp": { "scheme": "tcp", "protocol": "tcp", "transport": "tcp", "targetPort": 5432 } }
            },
            "api": {
              "type": "project.v0",
              "path": "Api/Api.csproj",
              "env": {
                "ConnectionStrings__db": "{pg.connectionString}",
                "PLAIN": "hello \"quoted\" %{x} ${y}",
                "Odd.Key-Name": "x"
              },
              "bindings": {
                "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8080, "external": true },
                "https": { "scheme": "https", "protocol": "tcp", "transport": "http", "targetPort": 8443 }
              },
              "annotations": { "dapr/enabled": "true", "dapr/app-id": "api-app" }
            },
            "worker": {
              "type": "container.v0",
              "image": "registry.example.com/myapp/worker:1",
              "args": [ "--mode", "fast", "--api", "{api.bindings.http.url}" ],
              "env": { "API_URL": "{api.bindings.http.url}" }
            }
          }
        }
        """;

    public static AspireManifest Manifest() => JsonSerializer.Deserialize<AspireManifest>(ManifestJson)!;

    public static EnvironmentConfig Config(string scenario)
    {
        var config = scenario switch
        {
            "docker" => Fixtures.WithTargetConfig(Fixtures.DefaultConfig(DeploymentTargetType.Nomad), "nomad", new
            {
                address = "http://localhost:4646",
                @namespace = "apps",
                datacenters = new[] { "dc1", "dc2" },
                deploymentMode = "Docker",
                registryUsername = "ci",
                registryPassword = "p@ss${x}",
                healthCheckType = "http",
                healthCheckPath = "/healthz",
                healthCheckInterval = "15s",
                healthCheckTimeout = "1500ms",
                serviceProvider = "consul",
                autoRevert = false,
                defaultCpu = 300,
                defaultMemory = 512
            }),
            "podman-no-registration" => Fixtures.WithTargetConfig(Fixtures.DefaultConfig(DeploymentTargetType.Nomad), "nomad", new
            {
                address = "http://localhost:4646",
                @namespace = "default",
                datacenters = new[] { "dc1" },
                deploymentMode = "Docker",
                registerServices = false
            }),
            "rawexec" => Fixtures.WithTargetConfig(Fixtures.DefaultConfig(DeploymentTargetType.Nomad), "nomad", new
            {
                address = "http://localhost:4646",
                @namespace = "default",
                datacenters = new[] { "dc1" },
                deploymentMode = "RawExec"
            }),
            _ => throw new System.ArgumentOutOfRangeException(nameof(scenario))
        };

        config.Parameters["pg-password"] = "S3cret-pw";
        config.Dapr.ComponentsPath = "./components";
        config.Dapr.ConfigPath = "./dapr.yaml";
        if (scenario == "podman-no-registration")
        {
            config.ContainerRuntime = ContainerRuntime.Podman;
        }

        if (scenario == "rawexec")
        {
            config.ServiceOverrides["api"] = new ServiceOverride { ArtifactUrl = "https://artifacts.example.com/api.zip", Count = 3 };
        }

        return config;
    }
}
