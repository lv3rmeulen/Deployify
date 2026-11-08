using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.AwsEcs.Tests;

/// <summary>An in-memory Parameter Store.</summary>
internal sealed class FakeSsm : ISsmParameterClient
{
    public Dictionary<string, (string Value, long Version)> Parameters { get; } = new(StringComparer.Ordinal);

    public List<string> Puts { get; } = [];

    public Task<SsmParameter?> GetParameterAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Parameters.TryGetValue(name, out var p) ? new SsmParameter(name, new SensitiveValue(p.Value), p.Version) : null);

    public Task<IReadOnlyDictionary<string, SsmParameter>> GetParametersByPathAsync(string path, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, SsmParameter>>(Parameters
            .Where(p => p.Key.StartsWith(path + "/", StringComparison.Ordinal))
            .ToDictionary(p => p.Key, p => new SsmParameter(p.Key, new SensitiveValue(p.Value.Value), p.Value.Version)));

    public Task<long> PutSecureStringParameterAsync(string name, string value, CancellationToken ct = default)
    {
        Puts.Add(name);
        var version = Parameters.TryGetValue(name, out var p) ? p.Version + 1 : 1;
        Parameters[name] = (value, version);
        return Task.FromResult(version);
    }
}

public sealed class SsmSecretBackendTests
{
    private const string Secret = "S3cret-pw";

    private const string ManifestJson = """
        {
          "resources": {
            "pg-password": {
              "type": "parameter.v0",
              "value": "{pg-password.inputs.value}",
              "inputs": { "value": { "type": "string", "secret": true } }
            },
            "api": {
              "type": "container.v0",
              "image": "api:1",
              "env": { "DB_PASSWORD": "{pg-password.value}", "PLAIN": "hello" },
              "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8080 } }
            }
          }
        }
        """;

    private static AspireManifest Manifest() => JsonSerializer.Deserialize<AspireManifest>(ManifestJson)!;

    private static EnvironmentConfig Config(SecretBackendType backend = SecretBackendType.Ssm, string secret = Secret)
    {
        var config = EcsFixtures.EcsConfig();
        config.Parameters["pg-password"] = secret;
        config.Secrets.Backend = backend;
        return config;
    }

    private static EcsTargetConfig Cfg() => new() { ClusterName = "shop" };

    private static EcsService Api(EnvironmentConfig config)
        => new EcsTaskDefinitionGenerator().Generate(Manifest(), config, Cfg()).Single(s => s.Name == "api");

    [Fact]
    public async Task Sync_WritesSecureStringAndRecordsVersion()
    {
        var config = Config();
        var ssm = new FakeSsm();

        await EcsDeploymentTarget.SyncSecretsAsync(Api(config), config, Cfg(), ssm, TestContext.Current.CancellationToken);

        ssm.Parameters["/deployify/shop/api/DB_PASSWORD"].ShouldBe((Secret, 1L));
        var reference = config.Secrets.References["api/DB_PASSWORD"];
        reference.Backend.ShouldBe(SecretBackendType.Ssm);
        reference.Location.ShouldBe("/deployify/shop/api/DB_PASSWORD");
        reference.Version.ShouldBe("1");
    }

    [Fact]
    public async Task Sync_SkipsUnchangedValues()
    {
        var ssm = new FakeSsm();
        var config = Config();
        await EcsDeploymentTarget.SyncSecretsAsync(Api(config), config, Cfg(), ssm, TestContext.Current.CancellationToken);
        await EcsDeploymentTarget.SyncSecretsAsync(Api(config), config, Cfg(), ssm, TestContext.Current.CancellationToken);

        ssm.Puts.Count.ShouldBe(1);

        var rotated = Config(secret: "R0tated-pw");
        await EcsDeploymentTarget.SyncSecretsAsync(Api(rotated), rotated, Cfg(), ssm, TestContext.Current.CancellationToken);
        ssm.Puts.Count.ShouldBe(2);
        rotated.Secrets.References["api/DB_PASSWORD"].Version.ShouldBe("2");
    }

    [Fact]
    public async Task Sync_InlineBackend_KeepsOldBehaviour()
    {
        var ssm = new FakeSsm();
        var config = Config(SecretBackendType.Inline);
        await EcsDeploymentTarget.SyncSecretsAsync(Api(config), config, Cfg(), ssm, TestContext.Current.CancellationToken);
        await EcsDeploymentTarget.SyncSecretsAsync(Api(config), config, Cfg(), ssm, TestContext.Current.CancellationToken);

        ssm.Puts.Count.ShouldBe(2);
        config.Secrets.References.ShouldBeEmpty();
    }

    [Fact]
    public async Task TaskDefinition_ReferencesParameterAndHoldsNoValue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deployify-ssm-" + Path.GetRandomFileName());
        try
        {
            var config = Config();
            var path = await new EcsTaskDefinitionGenerator().WriteTaskDefinitionAsync(
                Api(config), config, Cfg(), dir, TestContext.Current.CancellationToken);

            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            json.ShouldNotContain(Secret);
            json.ShouldContain("/deployify/shop/api/DB_PASSWORD");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Store_LoadsKeysUnderItsPathOnly()
    {
        var ssm = new FakeSsm();
        ssm.Parameters["/deployify/shop/_parameters/pg-password.value"] = ("generated-pw-1", 1);
        ssm.Parameters["/deployify/shop/api/DB_PASSWORD"] = ("other", 1);
        var store = new SsmSecretStore(ssm, "/deployify/shop/_parameters/");

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        loaded.Keys.ShouldBe(["pg-password.value"]);
        loaded["pg-password.value"].Reveal().ShouldBe("generated-pw-1");
        store.Description.ShouldBe("SSM Parameter Store /deployify/shop/_parameters");
    }

    [Fact]
    public async Task Store_WritesOnlyChangedValues()
    {
        var ssm = new FakeSsm();
        var store = new SsmSecretStore(ssm, "/deployify/shop/_parameters");

        await store.SaveAsync(new Dictionary<string, SensitiveValue>
        {
            ["a.value"] = new("existing-1"),
            ["b.value"] = new("new-value-2")
        }, ["b.value"], TestContext.Current.CancellationToken);

        ssm.Puts.ShouldBe(["/deployify/shop/_parameters/b.value"]);
    }

    [Fact]
    public void ParseParameters_ReadsCliOutput()
    {
        var parsed = EcsCliClient.ParseParameters("""
            { "Parameters": [ { "Name": "/p/a", "Type": "SecureString", "Value": "v1-secret", "Version": 3 } ] }
            """);

        parsed["/p/a"].Version.ShouldBe(3);
        parsed["/p/a"].Value.Reveal().ShouldBe("v1-secret");
        parsed["/p/a"].ToString().ShouldNotContain("v1-secret");
    }

    [Fact]
    public void Target_DeclaresSsmAndItsStore()
    {
        var target = new EcsDeploymentTarget(Substitute.For<IPublisher>(), NullLogger<EcsDeploymentTarget>.Instance);

        target.SupportedSecretBackends.ShouldBe([SecretBackendType.Ssm]);
        var config = Config();
        config.TargetConfig["ecs"] = JsonSerializer.SerializeToElement(new { clusterName = "shop", secretsPrefix = "/apps/shop" });
        target.CreateSecretStore(config, NullLogger.Instance).Description.ShouldBe("SSM Parameter Store /apps/shop/_parameters");
    }
}
