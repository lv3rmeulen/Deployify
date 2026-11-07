using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Target.Compose.Tests;

/// <summary>Secret parameters stay out of compose.yml.</summary>
public sealed class ComposeSecretTests
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

    private readonly ComposeFileGenerator _sut = new();

    private static EnvironmentConfig Config()
    {
        var config = Fixtures.ComposeConfig();
        config.Parameters["pg-password"] = Secret;
        return config;
    }

    [Fact]
    public void Generate_SplitsSecretAndPlainEnv()
    {
        var services = _sut.Generate(Manifest(), Config(), new ComposeTargetConfig());

        var api = services.Single(s => s.Name == "api");
        api.Env.Keys.ShouldBe(new[] { "PLAIN" });
        api.SecretEnv.Keys.ShouldBe(new[] { "ConnectionStrings__db" });
        api.SecretEnv["ConnectionStrings__db"].ToString().ShouldBe($"Host=pg;Password={Secret};Opt=a$b");
        services.Single(s => s.Name == "pg").SecretEnv.Keys.ShouldBe(new[] { "POSTGRES_PASSWORD" });
        _sut.Unresolved.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnvFileMode_SecretsGoToEnvFileOnly()
    {
        using var dir = new TempDir();
        var cfg = new ComposeTargetConfig { GenerateEnvFile = true };
        var api = _sut.Generate(Manifest(), Config(), cfg).Single(s => s.Name == "api");

        var composePath = await _sut.WriteServiceFileAsync(api, dir.Path, cfg, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(composePath, TestContext.Current.CancellationToken);
        yaml.ShouldNotContain(Secret);
        yaml.ShouldContain("env_file:");
        var env = await File.ReadAllTextAsync(api.EnvFilePath!, TestContext.Current.CancellationToken);
        env.ShouldContain($"ConnectionStrings__db='Host=pg;Password={Secret};Opt=a$b'");
        env.ShouldContain("PLAIN='hello'");
    }

    [Fact]
    public async Task InlineMode_SecretsBecomeInterpolationVariables()
    {
        using var dir = new TempDir();
        var cfg = new ComposeTargetConfig { GenerateEnvFile = false };
        var services = _sut.Generate(Manifest(), Config(), cfg);

        var mergedPath = await _sut.WriteMergedFileAsync(services, dir.Path, cfg, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(mergedPath, TestContext.Current.CancellationToken);
        yaml.ShouldNotContain(Secret);
        yaml.ShouldContain("\"ConnectionStrings__db\": \"Host=pg;Password=${Parameters__pg_password};Opt=a$$b\"");
        yaml.ShouldContain("\"POSTGRES_PASSWORD\": \"${Parameters__pg_password}\"");
        yaml.ShouldContain("\"PLAIN\": \"hello\"");

        var env = await File.ReadAllTextAsync(Path.Combine(dir.Path, ".env"), TestContext.Current.CancellationToken);
        env.ShouldContain($"Parameters__pg_password='{Secret}'");
        env.ShouldNotContain("PLAIN");
    }

    [Fact]
    public async Task InlineMode_PerServiceFileGetsItsOwnEnv()
    {
        using var dir = new TempDir();
        var cfg = new ComposeTargetConfig { GenerateEnvFile = false };
        var pg = _sut.Generate(Manifest(), Config(), cfg).Single(s => s.Name == "pg");

        await _sut.WriteServiceFileAsync(pg, dir.Path, cfg, TestContext.Current.CancellationToken);

        var env = await File.ReadAllTextAsync(Path.Combine(dir.Path, "pg", ".env"), TestContext.Current.CancellationToken);
        env.ShouldContain($"Parameters__pg_password='{Secret}'");
    }

    [Fact]
    public async Task InlineMode_NoSecrets_WritesNoEnvFile()
    {
        using var dir = new TempDir();
        var cfg = new ComposeTargetConfig { GenerateEnvFile = false };
        var services = _sut.Generate(Fixtures.SingleService("api", "container.v0"), Fixtures.ComposeConfig(), cfg);

        await _sut.WriteMergedFileAsync(services, dir.Path, cfg, TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(dir.Path, ".env")).ShouldBeFalse();
    }

    [Fact]
    public void InterpolationVariables_CollectsEveryParameterOnce()
    {
        var services = _sut.Generate(Manifest(), Config(), new ComposeTargetConfig());

        var variables = ComposeFileGenerator.InterpolationVariables(services);

        variables.ShouldHaveSingleItem().Key.ShouldBe("Parameters__pg_password");
        variables["Parameters__pg_password"].ShouldBe(Secret);
    }

    [Fact]
    public void Generate_GeneratedPassword_IsStableAcrossCallsWithSameConfig()
    {
        var manifest = Manifest();
        manifest.Resources["pg-password"] = JsonSerializer.Deserialize<AspireResource>("""
            { "type": "parameter.v0", "value": "{pg-password.inputs.value}",
              "inputs": { "value": { "type": "string", "secret": true, "default": { "generate": { "minLength": 16 } } } } }
            """)!;
        var config = Fixtures.ComposeConfig();

        var first = _sut.Generate(manifest, config, new ComposeTargetConfig()).Single(s => s.Name == "pg");
        var second = _sut.Generate(manifest, config, new ComposeTargetConfig()).Single(s => s.Name == "pg");

        var password = first.SecretEnv["POSTGRES_PASSWORD"].ToString();
        password.Length.ShouldBe(16);
        second.SecretEnv["POSTGRES_PASSWORD"].ToString().ShouldBe(password);
    }

    [Fact]
    public void Generate_OverrideReplacesSecret()
    {
        var config = Config();
        config.ServiceOverrides["api"] = new ServiceOverride { Env = { ["ConnectionStrings__db"] = "override" } };

        var api = _sut.Generate(Manifest(), config, new ComposeTargetConfig()).Single(s => s.Name == "api");

        api.SecretEnv.ShouldBeEmpty();
        api.Env["ConnectionStrings__db"].ShouldBe("override");
    }

    [Fact]
    public void Generate_MissingSecretParameter_StaysUnresolved()
    {
        var api = _sut.Generate(Manifest(), Fixtures.ComposeConfig(), new ComposeTargetConfig()).Single(s => s.Name == "api");

        // No value anywhere: the placeholder is kept (as plain text) and reported.
        api.Env["ConnectionStrings__db"].ShouldBe("Host=pg;Password={pg-password.inputs.value};Opt=a$b");
        _sut.Unresolved.ShouldContain("{pg-password.inputs.value}");
    }
}
