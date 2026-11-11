using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Target.AzureContainerApps.Tests;

/// <summary>Secret parameters become Container App secrets referenced with secretRef.</summary>
public sealed class AcaSecretTests
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

    private readonly AcaManifestGenerator _sut = new();

    private static EnvironmentConfig Config()
    {
        var config = AcaFixtures.AcaConfig();
        config.Parameters["pg-password"] = Secret;
        return config;
    }

    private async Task<JsonElement> SpecForAsync(string service, AcaFixtures.TempDir dir)
    {
        var config = Config();
        var acaCfg = new AcaTargetConfig { SubscriptionId = "sub", ResourceGroup = "rg", EnvironmentName = "env" };
        var app = (await _sut.GenerateAsync(Manifest(), config, acaCfg, TestContext.Current.CancellationToken)).Single(a => a.Name == service);
        var path = await _sut.WriteAppSpecAsync(app, config, acaCfg, dir.Path, TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task Spec_DeclaresSecretAndReferencesIt()
    {
        using var dir = new AcaFixtures.TempDir();

        var spec = await SpecForAsync("api", dir);

        var secrets = spec.GetProperty("properties").GetProperty("configuration").GetProperty("secrets");
        var secret = secrets.EnumerateArray().ShouldHaveSingleItem();
        secret.GetProperty("name").GetString().ShouldBe("connectionstrings--db");
        secret.GetProperty("value").GetString().ShouldBe($"Host=pg;Password={Secret};Opt=a$b");

        var env = spec.GetProperty("properties").GetProperty("template").GetProperty("containers")[0].GetProperty("env");
        var byName = env.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!);
        byName["PLAIN"].GetProperty("value").GetString().ShouldBe("hello");
        byName["ConnectionStrings__db"].GetProperty("secretRef").GetString().ShouldBe("connectionstrings--db");
        byName["ConnectionStrings__db"].TryGetProperty("value", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Spec_WithoutSecrets_HasNoSecretsSection()
    {
        using var dir = new AcaFixtures.TempDir();
        var config = AcaFixtures.AcaConfig();
        var acaCfg = new AcaTargetConfig();
        var app = (await _sut.GenerateAsync(AcaFixtures.SingleService("api"), config, acaCfg, TestContext.Current.CancellationToken))[0];
        var path = await _sut.WriteAppSpecAsync(app, config, acaCfg, dir.Path, TestContext.Current.CancellationToken);

        var spec = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).RootElement;

        spec.GetProperty("properties").GetProperty("configuration").TryGetProperty("secrets", out _).ShouldBeFalse();
    }

    [Fact]
    public void SecretNames_AreValidAndUnique()
    {
        var app = new AcaApp
        {
            SecretEnv =
            {
                ["DB_PASSWORD"] = ResolvedValue.Text("a"),
                ["db-password"] = ResolvedValue.Text("b"),
                ["__"] = ResolvedValue.Text("c")
            }
        };

        var names = AcaManifestGenerator.SecretNames(app);

        names["DB_PASSWORD"].ShouldBe("db-password");
        names["db-password"].ShouldBe("db-password-2");
        names["__"].ShouldBe("secret");
        names.Values.ShouldAllBe(n => System.Text.RegularExpressions.Regex.IsMatch(n, "^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$"));
    }
}
