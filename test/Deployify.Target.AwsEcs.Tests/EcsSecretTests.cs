using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Target.AwsEcs.Tests;

/// <summary>Secret parameters are referenced from SSM Parameter Store, never written to the task definition.</summary>
public sealed class EcsSecretTests
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

    private readonly EcsTaskDefinitionGenerator _sut = new();

    private static EnvironmentConfig Config()
    {
        var config = EcsFixtures.EcsConfig();
        config.Parameters["pg-password"] = Secret;
        return config;
    }

    [Fact]
    public async Task TaskDefinition_ReferencesSsmAndOmitsTheValue()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = Config();
        var ecsCfg = new EcsTargetConfig { ClusterName = "prod" };
        var api = _sut.Generate(Manifest(), config, ecsCfg).Single(s => s.Name == "api");

        var path = await _sut.WriteTaskDefinitionAsync(api, config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        json.ShouldNotContain(Secret);
        var container = JsonDocument.Parse(json).RootElement.GetProperty("containerDefinitions")[0];
        var secret = container.GetProperty("secrets").EnumerateArray().ShouldHaveSingleItem();
        secret.GetProperty("name").GetString().ShouldBe("ConnectionStrings__db");
        secret.GetProperty("valueFrom").GetString().ShouldBe("/deployify/prod/api/ConnectionStrings__db");
        container.GetProperty("environment").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ShouldBe(new[] { "PLAIN" });
        api.SecretEnv["ConnectionStrings__db"].ToString().ShouldBe($"Host=pg;Password={Secret};Opt=a$b");
    }

    [Theory]
    [InlineData(null, "/deployify/c/api/KEY")]
    [InlineData("", "/deployify/c/api/KEY")]
    [InlineData("/myapp/prod", "/myapp/prod/api/KEY")]
    [InlineData("myapp/prod/", "/myapp/prod/api/KEY")]
    public void SecretParameterName_UsesPrefix(string? prefix, string expected)
    {
        var cfg = new EcsTargetConfig { ClusterName = "c", SecretsPrefix = prefix };

        EcsTaskDefinitionGenerator.SecretParameterName(cfg, "api", "KEY").ShouldBe(expected);
    }
}
