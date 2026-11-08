using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.Services;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>
/// The secrets backend through the real command pipeline. Only <c>generate</c> is used: it
/// needs no cluster. Reading generated values from the backend may fail here (no cluster or
/// Vault), which generate tolerates with a warning.
/// </summary>
public sealed class SecretBackendCommandTests
{
    private const string Secret = "S3cret-pw";

    private static RootCommand Root() => CommandRunner.BuildFull();

    private static async Task WriteSecretManifestAsync(string dir, CancellationToken ct)
    {
        var outputDir = Path.Combine(dir, "deployify-output");
        Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "manifest.json"), """
            {
              "resources": {
                "pg-password": {
                  "type": "parameter.v0",
                  "value": "{pg-password.inputs.value}",
                  "inputs": { "value": { "type": "string", "secret": true } }
                },
                "redis-password": {
                  "type": "parameter.v0",
                  "value": "{redis-password.inputs.value}",
                  "inputs": { "value": { "type": "string", "secret": true, "default": { "generate": { "minLength": 22 } } } }
                },
                "api": {
                  "type": "container.v0",
                  "image": "api:1",
                  "env": {
                    "ConnectionStrings__db": "Host=pg;Password={pg-password.value}",
                    "REDIS_PASSWORD": "{redis-password.value}"
                  },
                  "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8080 } }
                }
              }
            }
            """, ct);
    }

    private static async Task SetBackendAsync(string dir, SecretBackendType backend, CancellationToken ct)
    {
        var sm = new StateManager();
        var state = await sm.LoadFileAsync(dir, ct);
        var env = state.Environments[state.ActiveEnvironment];
        env.Secrets.Backend = backend;
        env.Parameters["pg-password"] = "env:DEPLOYIFY_TEST_PG_PASSWORD";
        await sm.SaveFileAsync(state, dir, ct);
    }

    [Fact]
    public async Task Generate_KubernetesSecret_KeepsValuesOutOfOutputAndParametersFile()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        await Harness.WriteKubernetesStateAsync(dir.Path, ct: ct);
        await SetBackendAsync(dir.Path, SecretBackendType.KubernetesSecret, ct);
        await WriteSecretManifestAsync(dir.Path, ct);

        System.Environment.SetEnvironmentVariable("DEPLOYIFY_TEST_PG_PASSWORD", Secret);
        int code;
        try
        {
            code = await Root().InvokeAsync(["generate", "--project-path", dir.Path], ct);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("DEPLOYIFY_TEST_PG_PASSWORD", null);
        }

        code.ShouldBe(0);
        var output = Path.Combine(dir.Path, "deployify-output");
        Directory.GetFiles(output, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("manifest.json"))
            .ShouldAllBe(f => !File.ReadAllText(f).Contains(Secret));
        File.Exists(Path.Combine(output, "api", "secret.yaml")).ShouldBeFalse();
        var deployment = await File.ReadAllTextAsync(Path.Combine(output, "api", "deployment.yaml"), ct);
        deployment.ShouldContain("secretKeyRef");
        deployment.ShouldContain("key: \"redis-password\"");
        // Generated passwords belong in the backend, not in a file next to the state.
        File.Exists(Path.Combine(dir.Path, ParameterStore.FileName)).ShouldBeFalse();
        (await File.ReadAllTextAsync(Path.Combine(dir.Path, "deployify-state.json"), ct)).ShouldNotContain(Secret);
    }

    [Fact]
    public async Task Generate_Inline_StillKeepsGeneratedValuesInParametersFile()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        await Harness.WriteKubernetesStateAsync(dir.Path, ct: ct);
        await WriteSecretManifestAsync(dir.Path, ct);

        var code = await Root().InvokeAsync(["generate", "--project-path", dir.Path], ct);

        code.ShouldBe(0);
        File.Exists(Path.Combine(dir.Path, ParameterStore.FileName)).ShouldBeTrue();
        File.Exists(Path.Combine(dir.Path, "deployify-output", "api", "secret.yaml")).ShouldBeTrue();
    }

    [Theory]
    [InlineData(SecretBackendType.Vault)]
    [InlineData(SecretBackendType.Ssm)]
    [InlineData(SecretBackendType.KeyVault)]
    public async Task Generate_FailsForABackendTheTargetDoesNotSupport(SecretBackendType backend)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        await Harness.WriteComposeStateAsync(dir.Path, ct: ct);
        await SetBackendAsync(dir.Path, backend, ct);
        await WriteSecretManifestAsync(dir.Path, ct);

        var code = await Root().InvokeAsync(["generate", "--project-path", dir.Path], ct);

        code.ShouldNotBe(0);
        Directory.GetFiles(Path.Combine(dir.Path, "deployify-output"), "*.yml", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task StateFile_WithSecretsSection_LoadsForEveryCommand()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        await Harness.WriteKubernetesStateAsync(dir.Path, ct: ct);
        await SetBackendAsync(dir.Path, SecretBackendType.Vault, ct);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir.Path, "deployify-state.json"), ct));
        doc.RootElement.GetProperty("Environments").GetProperty("local").GetProperty("Secrets")
            .GetProperty("Backend").GetString().ShouldBe("Vault");
    }
}
