using System.Collections.Generic;
using System.Text.Json;
using Deployify.Core.Expressions;
using Deployify.Core.Models;

namespace Deployify.Core.Tests.Expressions;

internal static class ExpressionFixtures
{
    /// <summary>
    /// A manifest in the shape Aspire emits: a postgres container with a generated secret
    /// password, a database child resource, a plain parameter, a value resource and two projects.
    /// </summary>
    public const string AppManifestJson = """
        {
          "resources": {
            "pg-password": {
              "type": "parameter.v0",
              "value": "{pg-password.inputs.value}",
              "inputs": {
                "value": {
                  "type": "string",
                  "secret": true,
                  "default": { "generate": { "minLength": 22, "special": false } }
                }
              }
            },
            "greeting": {
              "type": "parameter.v0",
              "value": "{greeting.inputs.value}",
              "inputs": { "value": { "type": "string", "default": { "value": "hello" } } }
            },
            "api-key": {
              "type": "parameter.v0",
              "value": "{api-key.inputs.value}",
              "inputs": { "value": { "type": "string", "secret": true } }
            },
            "pg": {
              "type": "container.v0",
              "image": "docker.io/library/postgres:17",
              "connectionString": "Host={pg.bindings.tcp.host};Port={pg.bindings.tcp.port};Username=postgres;Password={pg-password.value}",
              "env": { "POSTGRES_PASSWORD": "{pg-password.value}" },
              "bindings": { "tcp": { "scheme": "tcp", "protocol": "tcp", "transport": "tcp", "targetPort": 5432 } }
            },
            "appdb": {
              "type": "value.v0",
              "connectionString": "{pg.connectionString};Database=appdb",
              "parent": "pg"
            },
            "endpoint": {
              "type": "value.v0",
              "value": "{api.bindings.http.url}/v1"
            },
            "api": {
              "type": "project.v0",
              "path": "Api/Api.csproj",
              "env": {
                "ConnectionStrings__appdb": "{appdb.connectionString}",
                "GREETING": "{greeting.value}",
                "API_KEY": "{api-key.value}",
                "JSON": "{\"retries\":3}"
              },
              "bindings": {
                "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8080 },
                "https": { "scheme": "https", "protocol": "tcp", "transport": "http", "targetPort": 8443 }
              }
            },
            "web": {
              "type": "project.v0",
              "path": "Web/Web.csproj",
              "env": {
                "services__api__http__0": "{api.bindings.http.url}",
                "services__api__https__0": "{api.bindings.https.url}",
                "API_ENDPOINT": "{endpoint.value}"
              },
              "args": [ "--api", "{api.bindings.http.host}:{api.bindings.http.port}" ],
              "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 3000, "external": true } }
            }
          }
        }
        """;

    public static AspireManifest Parse(string json) => JsonSerializer.Deserialize<AspireManifest>(json)!;

    public static AspireManifest AppManifest() => Parse(AppManifestJson);

    public static ExpressionResolver Resolver(
        AspireManifest? manifest = null,
        EndpointStrategy? strategy = null,
        IParameterSource? parameters = null,
        bool strict = false,
        bool projectHttpsAsHttp = true,
        IEnumerable<IReferenceHandler>? handlers = null)
    {
        manifest ??= AppManifest();
        return new ExpressionResolver(manifest, ServicePorts.Assign(manifest), strategy ?? new EndpointStrategy(), parameters, handlers)
        {
            Strict = strict,
            ProjectHttpsAsHttp = projectHttpsAsHttp
        };
    }

    /// <summary>A parameter source with fixed values and no environment lookups.</summary>
    public static IParameterSource FixedParameters(params (string Name, string Value)[] values)
    {
        var map = new Dictionary<string, string>();
        foreach (var (name, value) in values)
        {
            map[name] = value;
        }

        return ParameterSources.Default(map, getVariable: _ => null);
    }
}
