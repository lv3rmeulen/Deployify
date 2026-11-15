using System.Text.Json;
using Deployify.Core.Models;

namespace Deployify.Target.NomadConsul.Tests;

/// <summary>
/// Manifests and configs that between them use every part of a generated Consul Connect job:
/// upstreams, TCP and HTTP checks, the ingress gateway, registry auth, Dapr, secrets and args.
/// </summary>
internal static class ConsulJobScenarios
{
    public static readonly string[] Names = ["tcp-checks", "http-checks-ingress", "no-registration"];

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
              "connectionString": "Host={pg.bindings.tcp.host};Port={pg.bindings.tcp.port};Password={pg-password.value}",
              "env": { "POSTGRES_PASSWORD": "{pg-password.value}" },
              "bindings": { "tcp": { "scheme": "tcp", "protocol": "tcp", "transport": "tcp", "targetPort": 5432 } }
            },
            "api": {
              "type": "project.v0",
              "path": "Api/Api.csproj",
              "env": { "ConnectionStrings__db": "{pg.connectionString}", "PLAIN": "hello \"quoted\" ${y}" },
              "bindings": {
                "metrics": { "scheme": "tcp", "protocol": "tcp", "transport": "tcp", "targetPort": 9090 },
                "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8080, "external": true }
              },
              "references": [ { "name": "pg" } ],
              "annotations": { "dapr/enabled": "true" }
            },
            "web": {
              "type": "container.v0",
              "image": "registry.example.com/myapp/web:1",
              "args": [ "--api", "{api.bindings.http.url}" ],
              "env": { "API_URL": "{api.bindings.http.url}" },
              "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 3000, "external": true } },
              "references": [ { "name": "api" }, { "name": "pg" } ]
            }
          }
        }
        """;

    public static AspireManifest Manifest() => JsonSerializer.Deserialize<AspireManifest>(ManifestJson)!;

    public static NomadConsulTargetConfig ConsulConfig(string scenario)
    {
        var cfg = Fixtures.DefaultConsulCfg(ns: "mesh", ingress: scenario == "http-checks-ingress");
        cfg.RegistryUsername = "ci";
        cfg.RegistryPassword = "p@ss${x}";
        switch (scenario)
        {
            case "http-checks-ingress":
                cfg.HealthCheckType = "http";
                cfg.HealthCheckPath = "/healthz";
                cfg.HealthCheckInterval = "1m30s";
                cfg.AutoRevert = false;
                break;
            case "no-registration":
                cfg.RegisterServices = false;
                cfg.RegistryUsername = null;
                break;
        }

        return cfg;
    }

    public static EnvironmentConfig Config(string scenario)
    {
        var config = Fixtures.EnvConfig(consulCfg: ConsulConfig(scenario));
        config.Parameters["pg-password"] = "S3cret-pw";
        config.Dapr.ComponentsPath = "./components";
        if (scenario == "no-registration")
        {
            config.ContainerRuntime = ContainerRuntime.Podman;
        }

        return config;
    }
}
