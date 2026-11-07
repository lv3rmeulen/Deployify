using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

/// <summary>Secret parameters are read from Nomad Variables by a template block, never written to the job file.</summary>
public sealed class NomadSecretTests
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

    private readonly JobGenerator _sut = new(NullLogger<JobGenerator>.Instance);

    private static EnvironmentConfig Config()
    {
        var config = Fixtures.NomadConfig();
        config.Parameters["pg-password"] = Secret;
        return config;
    }

    [Fact]
    public async Task Hcl_HasTemplateAndNoSecret()
    {
        using var dir = new TempDir();
        var config = Config();
        var api = _sut.Generate(Manifest(), config).Single(j => j.Name == "api");

        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(api, config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        hcl.ShouldNotContain(Secret);
        hcl.ShouldContain("template {");
        hcl.ShouldContain("nomadVar \\\"nomad/jobs/api\\\"");
        hcl.ShouldContain("ConnectionStrings__db={{ index . \\\"ConnectionStrings__db\\\" }}");
        hcl.ShouldContain("env         = true");
        hcl.ShouldContain("destination = \"secrets/deployify.env\"");
        hcl.ShouldContain("\"PLAIN\" = \"hello\"");
        hcl.ShouldNotContain("\"ConnectionStrings__db\" =");
    }

    [Fact]
    public void Generate_KeepsSecretValuesForApply()
    {
        var api = _sut.Generate(Manifest(), Config()).Single(j => j.Name == "api");

        api.SecretEnv["ConnectionStrings__db"].ToString().ShouldBe($"Host=localhost;Password={Secret};Opt=a$b");
        api.Env.Keys.ShouldBe(new[] { "PLAIN" });
    }

    [Fact]
    public async Task Hcl_WithoutSecrets_HasNoTemplate()
    {
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig();
        var job = _sut.Generate(Fixtures.SingleService(), config)[0];

        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(job, config, dir.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        hcl.ShouldNotContain("nomadVar");
    }

    [Fact]
    public void TemplateData_ListsKeysInOrder()
    {
        NomadVariables.TemplateData("api", new[] { "B", "A-1" }).ShouldBe(
            "{{ with nomadVar \"nomad/jobs/api\" -}}\n" +
            "A-1={{ index . \"A-1\" }}\n" +
            "B={{ index . \"B\" }}\n" +
            "{{- end }}\n");
    }

    [Fact]
    public void JobPath_IsUnderNomadJobs()
    {
        NomadVariables.JobPath("api").ShouldBe("nomad/jobs/api");
    }
}
