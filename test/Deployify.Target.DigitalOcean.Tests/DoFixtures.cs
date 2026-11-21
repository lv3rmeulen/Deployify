using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Deployify.Core.Models;

namespace Deployify.Target.DigitalOcean.Tests;

internal static class DoFixtures
{
    public const string PgPassword = "pg-S3cret-value";
    public const string CachePassword = "cache-S3cret-value";
    public const string ApiKey = "api-key-S3cret";

    /// <summary>
    /// A shop as Aspire 13 writes it: a public API project, a worker project, an internal
    /// container on GHCR, Postgres with a database, and Redis.
    /// </summary>
    public static AspireManifest ShopManifest() => JsonSerializer.Deserialize<AspireManifest>(
        """
        {
          "resources": {
            "pg-password": {
              "type": "parameter.v0", "value": "{pg-password.inputs.value}",
              "inputs": { "value": { "type": "string", "secret": true } }
            },
            "pg": {
              "type": "container.v1",
              "connectionString": "Host={pg.bindings.tcp.host};Port={pg.bindings.tcp.port};Username=postgres;Password={pg-password.value}",
              "image": "docker.io/library/postgres:17.6",
              "env": { "POSTGRES_USER": "postgres", "POSTGRES_PASSWORD": "{pg-password.value}" },
              "bindings": { "tcp": { "scheme": "tcp", "protocol": "tcp", "transport": "tcp", "targetPort": 5432 } }
            },
            "appdb": { "type": "value.v0", "connectionString": "{pg.connectionString};Database=appdb" },
            "cache-password": {
              "type": "parameter.v0", "value": "{cache-password.inputs.value}",
              "inputs": { "value": { "type": "string", "secret": true } }
            },
            "cache": {
              "type": "container.v1",
              "connectionString": "{cache.bindings.tcp.host}:{cache.bindings.tcp.port},password={cache-password.value}",
              "image": "docker.io/library/redis:8.2",
              "entrypoint": "/bin/sh",
              "args": [ "-c", "redis-server --requirepass $REDIS_PASSWORD" ],
              "env": { "REDIS_PASSWORD": "{cache-password.value}" },
              "bindings": { "tcp": { "scheme": "tcp", "protocol": "tcp", "transport": "tcp", "targetPort": 6379 } }
            },
            "api-key": {
              "type": "parameter.v0", "value": "{api-key.inputs.value}",
              "inputs": { "value": { "type": "string", "secret": true } }
            },
            "catalog": {
              "type": "container.v1",
              "image": "ghcr.io/acme/catalog:2.1",
              "env": { "PORT": "{catalog.bindings.http.targetPort}" },
              "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 9000 } }
            },
            "api": {
              "type": "project.v1",
              "path": "../Shop.Api/Shop.Api.csproj",
              "env": {
                "HTTP_PORTS": "{api.bindings.http.targetPort}",
                "ConnectionStrings__appdb": "{appdb.connectionString}",
                "ConnectionStrings__cache": "{cache.connectionString}",
                "CATALOG_HTTP": "{catalog.bindings.http.url}",
                "API_KEY": "{api-key.value}"
              },
              "bindings": {
                "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "external": true },
                "https": { "scheme": "https", "protocol": "tcp", "transport": "http", "external": true }
              }
            },
            "orders": {
              "type": "project.v1",
              "path": "../Shop.Orders/Shop.Orders.csproj",
              "env": { "ConnectionStrings__appdb": "{appdb.connectionString}", "ConnectionStrings__cache": "{cache.connectionString}" }
            }
          }
        }
        """)!;

    public static EnvironmentConfig Config(Action<DoTargetConfig>? configure = null)
    {
        var cfg = new DoTargetConfig { Region = "ams" };
        configure?.Invoke(cfg);
        var config = new EnvironmentConfig
        {
            Name = "prod",
            Target = DeploymentTargetType.Parse(DoDeploymentTarget.Id),
            ContainerRegistry = "registry.digitalocean.com/acme",
            ImageTag = "v1.2.0",
            Parameters = { ["pg-password"] = PgPassword, ["cache-password"] = CachePassword, ["api-key"] = ApiKey }
        };
        config.TargetConfig[DoTargetConfig.Key] = JsonSerializer.SerializeToElement(cfg);
        return config;
    }

    public static AspireManifest Single(string name, string image, bool external = true, int port = 8080) => new()
    {
        Resources = new Dictionary<string, AspireResource>
        {
            [name] = new()
            {
                Type = "container.v1",
                Image = image,
                Bindings = new Dictionary<string, AspireBinding>
                {
                    ["http"] = new() { Scheme = "http", Protocol = "tcp", ContainerPort = port, IsExternal = external }
                }
            }
        }
    };

    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("deployify-do-tests-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    public static string Json(AppSpec spec) => JsonSerializer.Serialize(spec, AppSpec.JsonOptions);

    public static IEnumerable<EnvSpec> AllEnvs(AppSpec spec)
        => (spec.Services ?? []).SelectMany(s => s.Envs ?? []).Concat((spec.Workers ?? []).SelectMany(w => w.Envs ?? []));
}
