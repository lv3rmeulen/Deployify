using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.NomadConsul.Tests;

/// <summary>Consul Connect jobs read secrets from Nomad Variables too.</summary>
public sealed class NomadConsulSecretTests
{
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
              "connectionString": "Host={pg.bindings.tcp.host};Password={pg-password.value};Opt=a$b",
              "env": { "POSTGRES_PASSWORD": "{pg-password.value}" },
              "bindings": { "tcp": { "scheme": "tcp", "protocol": "tcp", "transport": "tcp", "targetPort": 5432 } }
            },
            "api": {
              "type": "container.v0",
              "image": "api:1",
              "env": { "ConnectionStrings__db": "{pg.connectionString}", "PLAIN": "hello" },
              "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8080 } }
            }
          }
        }
        """;

    private const string Secret = "S3cret-pw";

    private static AspireManifest Manifest() => JsonSerializer.Deserialize<AspireManifest>(ManifestJson)!;

    private readonly ConsulJobGenerator _sut = new(NullLogger<ConsulJobGenerator>.Instance);

    [Fact]
    public async Task Hcl_HasTemplateAndNoSecret()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        var config = Fixtures.EnvConfig(consulCfg: cfg);
        config.Parameters["pg-password"] = Secret;
        var api = _sut.Generate(Manifest(), config, cfg).Single(j => j.Name == "api");

        var path = await _sut.WriteHclAsync(api, config, cfg, dir.Path, TestContext.Current.CancellationToken);
        var hcl = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        api.SecretEnv.Keys.ShouldBe(new[] { "ConnectionStrings__db" });
        api.SecretEnv["ConnectionStrings__db"].ToString().ShouldContain($"Password={Secret}");
        hcl.ShouldNotContain(Secret);
        hcl.ShouldContain("nomadVar \\\"nomad/jobs/api\\\"");
        hcl.ShouldContain("\"PLAIN\" = \"hello\"");
    }
}
