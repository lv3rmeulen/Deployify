using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Target.Kubernetes.Tests;

/// <summary>Secret parameters go into a Kubernetes Secret, not the ConfigMap.</summary>
public sealed class KubernetesSecretTests
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

    private readonly KubernetesManifestGenerator _sut = new();
    private readonly KubernetesTargetConfig _cfg = new() { NamespacePrefix = "test" };

    private static EnvironmentConfig Config()
    {
        var config = Fixtures.KubernetesConfig();
        config.Parameters["pg-password"] = Secret;
        return config;
    }

    [Fact]
    public async Task Generate_SplitsSecretAndPlainEnv()
    {
        var api = (await _sut.GenerateAsync(Manifest(), Config(), _cfg, TestContext.Current.CancellationToken)).Single(s => s.ServiceName == "api");

        api.Env.Keys.ShouldBe(new[] { "PLAIN" });
        api.HasSecret.ShouldBeTrue();
        api.SecretEnv["ConnectionStrings__db"].ToString().ShouldBe($"Host=pg.test-pg;Password={Secret};Opt=a$b");
    }

    [Fact]
    public async Task Write_SecretYamlHoldsSecretsAndConfigMapDoesNot()
    {
        using var dir = new TempDir();
        var api = (await _sut.GenerateAsync(Manifest(), Config(), _cfg, TestContext.Current.CancellationToken)).Single(s => s.ServiceName == "api");

        var files = await _sut.WriteYamlAsync(api, dir.Path, TestContext.Current.CancellationToken);

        var secret = await File.ReadAllTextAsync(files.Single(f => f.EndsWith("secret.yaml")), TestContext.Current.CancellationToken);
        secret.ShouldContain("kind: Secret");
        secret.ShouldContain("name: api-secrets");
        secret.ShouldContain("namespace: test-api");
        secret.ShouldContain("type: Opaque");
        secret.ShouldContain($"  ConnectionStrings__db: \"Host=pg.test-pg;Password={Secret};Opt=a$b\"");

        var configMap = await File.ReadAllTextAsync(files.Single(f => f.EndsWith("configmap.yaml")), TestContext.Current.CancellationToken);
        configMap.ShouldNotContain(Secret);
        configMap.ShouldContain("PLAIN");

        var deployment = await File.ReadAllTextAsync(files.Single(f => f.EndsWith("deployment.yaml")), TestContext.Current.CancellationToken);
        deployment.ShouldNotContain(Secret);
        deployment.ShouldContain("- configMapRef:");
        deployment.ShouldContain("- secretRef:");
        deployment.ShouldContain("name: api-secrets");
    }

    [Fact]
    public async Task Write_OnlySecrets_HasNoConfigMap()
    {
        using var dir = new TempDir();
        var pg = (await _sut.GenerateAsync(Manifest(), Config(), _cfg, TestContext.Current.CancellationToken)).Single(s => s.ServiceName == "pg");

        var files = await _sut.WriteYamlAsync(pg, dir.Path, TestContext.Current.CancellationToken);

        files.ShouldNotContain(f => f.EndsWith("configmap.yaml"));
        var deployment = await File.ReadAllTextAsync(files.Single(f => f.EndsWith("deployment.yaml")), TestContext.Current.CancellationToken);
        deployment.ShouldContain("envFrom:");
        deployment.ShouldNotContain("configMapRef");
        deployment.ShouldContain("secretRef");
    }

    [Fact]
    public async Task Write_RemovesStaleSecretWhenNoLongerNeeded()
    {
        using var dir = new TempDir();
        var config = Config();
        var api = (await _sut.GenerateAsync(Manifest(), config, _cfg, TestContext.Current.CancellationToken)).Single(s => s.ServiceName == "api");
        await _sut.WriteYamlAsync(api, dir.Path, TestContext.Current.CancellationToken);
        var secretPath = Path.Combine(dir.Path, "api", "secret.yaml");
        File.Exists(secretPath).ShouldBeTrue();

        config.ServiceOverrides["api"] = new ServiceOverride { Env = { ["ConnectionStrings__db"] = "plain" } };
        api = (await _sut.GenerateAsync(Manifest(), config, _cfg, TestContext.Current.CancellationToken)).Single(s => s.ServiceName == "api");
        var files = await _sut.WriteYamlAsync(api, dir.Path, TestContext.Current.CancellationToken);

        File.Exists(secretPath).ShouldBeFalse();
        files.ShouldNotContain(f => f.EndsWith("secret.yaml"));
    }
}
