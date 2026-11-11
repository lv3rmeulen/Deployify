using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Kubernetes.Tests;

/// <summary>imagePullSecrets on the pods, and the registry Secret Deployify keeps in each namespace.</summary>
public sealed class KubernetesImagePullSecretTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KubernetesDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLogger<KubernetesDeploymentTarget>.Instance) { Processes = fake };

    private static EnvironmentConfig Config(object? registryCredentials = null, string[]? imagePullSecrets = null,
        string registry = "abc123.gra7.container-registry.ovh.net/myapp")
    {
        var config = Fixtures.DefaultConfig(DeploymentTargetType.Kubernetes, registry);
        return Fixtures.WithTargetConfig(config, "kubernetes", new
        {
            namespacePrefix = "test",
            imagePullSecrets = imagePullSecrets ?? [],
            registryCredentials
        });
    }

    private static async Task<string> DeploymentYamlAsync(EnvironmentConfig config, string service = "api")
    {
        using var dir = new TempDir();
        await Target(new FakeProcessRunner()).GenerateAsync(Fixtures.SimpleManifest(), config, dir.Path, Ct);
        return await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(dir.Path, service, "deployment.yaml"), Ct);
    }

    [Fact]
    public async Task WithoutSettings_PodsListNoPullSecrets()
        => (await DeploymentYamlAsync(Config())).ShouldNotContain("imagePullSecrets");

    [Fact]
    public async Task ConfiguredSecrets_AreListedOnEveryPod()
    {
        var yaml = await DeploymentYamlAsync(Config(imagePullSecrets: ["ghcr", "ghcr", "harbor"]));

        yaml.ReplaceLineEndings("\n").ShouldContain("""
                  imagePullSecrets:
                    - name: "ghcr"
                    - name: "harbor"
                  containers:
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task RegistryCredentials_AddTheirSecret_ToThePods_ButNotToTheFiles()
    {
        var yaml = await DeploymentYamlAsync(Config(new { username = "robot", password = "env:REG_PW" }, ["ghcr"]));

        yaml.ShouldContain("- name: \"ghcr\"");
        yaml.ShouldContain("- name: \"deployify-registry\"");
        yaml.ShouldNotContain("robot");
    }

    [Theory]
    [InlineData("registry.example.com/team/app", "registry.example.com")]
    [InlineData("registry.example.com:5000/app", "registry.example.com:5000")]
    [InlineData("https://ghcr.io/me", "ghcr.io")]
    [InlineData("localhost:5000", "localhost:5000")]
    [InlineData(null, null)]
    public void TheServer_DefaultsToTheRegistryHost(string? containerRegistry, string? expected)
        => KubernetesManifestGenerator.RegistryHost(containerRegistry).ShouldBe(expected);

    [Fact]
    public async Task Apply_PutsTheLoginInEachNamespace_FromMemory()
    {
        var variable = "DEPLOYIFY_TEST_REG_PW_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "s3cret-token");
        try
        {
            using var dir = new TempDir();
            var fake = new FakeProcessRunner();
            var target = Target(fake);
            var config = Config(new { username = "robot", password = "env:" + variable });
            await target.PlanAsync(Fixtures.SimpleManifest(), config, dir.Path, Ct);

            var results = await target.ApplyAsync(Fixtures.SimpleManifest(), config, dir.Path, Ct);

            results.ShouldAllBe(r => r.Success);
            var secrets = fake.Calls.Where(c => c.Stdin is { } s && s.Contains("kubernetes.io/dockerconfigjson")).ToList();
            secrets.Count.ShouldBe(3); // api, worker, redis
            var api = secrets.Single(c => c.Stdin!.Contains("\"namespace\":\"test-api\""));
            using var doc = JsonDocument.Parse(api.Stdin!);
            doc.RootElement.GetProperty("metadata").GetProperty("name").GetString().ShouldBe("deployify-registry");
            var dockerConfig = Encoding.UTF8.GetString(Convert.FromBase64String(
                doc.RootElement.GetProperty("data").GetProperty(".dockerconfigjson").GetString()!));
            using var auths = JsonDocument.Parse(dockerConfig);
            var entry = auths.RootElement.GetProperty("auths").GetProperty("abc123.gra7.container-registry.ovh.net");
            entry.GetProperty("username").GetString().ShouldBe("robot");
            entry.GetProperty("password").GetString().ShouldBe("s3cret-token");
            entry.GetProperty("auth").GetString().ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes("robot:s3cret-token")));

            // The namespace exists before the Secret, and the Secret before the Deployment.
            var ns = fake.Calls.FindIndex(c => c.Stdin is { } s && s.Contains("\"kind\":\"Namespace\"") && s.Contains("\"test-api\""));
            var secret = fake.Calls.IndexOf(api);
            var deploy = fake.Calls.FindIndex(c => c.Args.Contains("apply") && c.Args.Any(a => a.EndsWith("api", StringComparison.Ordinal)));
            ns.ShouldBeLessThan(secret);
            secret.ShouldBeLessThan(deploy);

            // Nothing on disk holds the password.
            foreach (var file in System.IO.Directory.GetFiles(dir.Path, "*", System.IO.SearchOption.AllDirectories))
            {
                (await System.IO.File.ReadAllTextAsync(file, Ct)).ShouldNotContain("s3cret-token");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task AMissingPassword_FailsTheService_NamingTheVariable()
    {
        using var dir = new TempDir();
        var target = Target(new FakeProcessRunner());
        var config = Config(new { username = "robot", password = "env:DEPLOYIFY_TEST_UNSET_VARIABLE" });
        await target.PlanAsync(Fixtures.SimpleManifest(), config, dir.Path, Ct);

        var results = await target.ApplyAsync(Fixtures.SimpleManifest(), config, dir.Path, Ct);

        results.ShouldAllBe(r => !r.Success && r.Error!.Contains("DEPLOYIFY_TEST_UNSET_VARIABLE"));
    }

    [Fact]
    public async Task RegistryCredentials_WithoutAServer_SayWhatToSet()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => DeploymentYamlAsync(Config(new { username = "robot", password = "env:X" }, registry: "")));

        ex.Message.ShouldContain("registryCredentials.server");
    }

    [Fact]
    public void ALiteralRegistryPassword_IsFlaggedInTheStateFile()
    {
        var state = new DeployifyStateFile { Environments = { ["prod"] = Config(new { username = "robot", password = "hunter2" }) } };

        StateFileSecrets.FindLiteral(state).ShouldContain("prod.kubernetes.registryCredentials.password");
    }
}
