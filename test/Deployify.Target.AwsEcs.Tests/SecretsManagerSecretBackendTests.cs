using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.SecretBackends;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.AwsEcs.Tests;

/// <summary>An in-memory Secrets Manager.</summary>
internal sealed class FakeSecretsManager : ISecretsManagerClient
{
    public Dictionary<string, List<string>> Versions { get; } = new(StringComparer.Ordinal);

    public List<string> Puts { get; } = [];

    public HashSet<string> Deleted { get; } = new(StringComparer.Ordinal);

    public static string Arn(string name) => $"arn:aws:secretsmanager:us-east-1:111122223333:secret:{name}-AbCdEf";

    public Task<SecretsManagerSecret?> GetSecretAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Versions.TryGetValue(name, out var v) && !Deleted.Contains(name)
            ? new SecretsManagerSecret(Arn(name), name, new SensitiveValue(v[^1]), $"v{v.Count}")
            : null);

    public Task<SecretsManagerVersion> PutSecretAsync(string name, string value, CancellationToken ct = default)
    {
        Puts.Add(name);
        Deleted.Remove(name);
        if (!Versions.TryGetValue(name, out var v))
        {
            Versions[name] = v = [];
        }

        v.Add(value);
        return Task.FromResult(new SecretsManagerVersion(Arn(name), $"v{v.Count}"));
    }

    public Task<bool> DeleteSecretAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Versions.ContainsKey(name) && Deleted.Add(name));
}

public sealed class SecretsManagerSecretBackendTests
{
    private const string Secret = "S3cret-pw";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
              "env": { "DB_PASSWORD": "{pg-password.value}", "CONN": "Host=pg;Password={pg-password.value}", "PLAIN": "hello" },
              "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8080 } }
            }
          }
        }
        """;

    private static AspireManifest Manifest() => JsonSerializer.Deserialize<AspireManifest>(ManifestJson)!;

    private static EnvironmentConfig Config(string secret = Secret)
    {
        var config = EcsFixtures.EcsConfig();
        config.Parameters["pg-password"] = secret;
        config.Secrets.Backend = SecretBackendType.SecretsManager;
        return config;
    }

    private static EcsTargetConfig Cfg() => new() { ClusterName = "shop" };

    private static async Task<EcsService> ApiAsync(EnvironmentConfig config)
        => (await new EcsTaskDefinitionGenerator().GenerateAsync(Manifest(), config, Cfg(), Ct)).Single(s => s.Name == "api");

    private static ProcessResult Ok(string json) => new(0, json, "");

    private static ProcessResult Fail(string message) => new(254, "", message);

    // ── Sync ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_WritesOneJsonSecretPerServiceAndRecordsEachKey()
    {
        var config = Config();
        var sm = new FakeSecretsManager();

        var version = await EcsDeploymentTarget.SyncSecretsManagerAsync(await ApiAsync(config), config, Cfg(), sm, Ct);

        version.ShouldNotBeNull();
        version.Arn.ShouldBe(FakeSecretsManager.Arn("deployify/shop/api"));
        sm.Versions.Keys.ShouldBe(["deployify/shop/api"]);
        var stored = SecretsManagerSecrets.ParseDocument(new SensitiveValue(sm.Versions["deployify/shop/api"].Single()));
        stored.Keys.Order().ShouldBe(["CONN", "DB_PASSWORD"]);
        stored["DB_PASSWORD"].Reveal().ShouldBe(Secret);

        var reference = config.Secrets.References["api/DB_PASSWORD"];
        reference.Backend.ShouldBe(SecretBackendType.SecretsManager);
        reference.Location.ShouldBe("deployify/shop/api");
        reference.Key.ShouldBe("DB_PASSWORD");
        reference.Version.ShouldBe("v1");
    }

    [Fact]
    public async Task Sync_SkipsAnUnchangedDocument_AndWritesANewVersionOnRotation()
    {
        var sm = new FakeSecretsManager();
        var config = Config();
        await EcsDeploymentTarget.SyncSecretsManagerAsync(await ApiAsync(config), config, Cfg(), sm, Ct);
        var again = await EcsDeploymentTarget.SyncSecretsManagerAsync(await ApiAsync(config), config, Cfg(), sm, Ct);

        sm.Puts.Count.ShouldBe(1);
        again!.VersionId.ShouldBe("v1");

        var rotated = Config("R0tated-pw");
        var next = await EcsDeploymentTarget.SyncSecretsManagerAsync(await ApiAsync(rotated), rotated, Cfg(), sm, Ct);
        sm.Puts.Count.ShouldBe(2);
        next!.VersionId.ShouldBe("v2");
        rotated.Secrets.References["api/CONN"].Version.ShouldBe("v2");
    }

    [Fact]
    public async Task Sync_ServiceWithoutSecrets_WritesNothing()
    {
        var sm = new FakeSecretsManager();
        var config = Config();
        var svc = await ApiAsync(config);
        svc.SecretEnv.Clear();

        (await EcsDeploymentTarget.SyncSecretsManagerAsync(svc, config, Cfg(), sm, Ct)).ShouldBeNull();
        sm.Puts.ShouldBeEmpty();
    }

    // ── Task definition ──────────────────────────────────────────────────────

    [Fact]
    public async Task TaskDefinition_ReferencesJsonKeysOfTheServiceSecret_AndHoldsNoValue()
    {
        using var dir = new EcsFixtures.TempDir();
        var config = Config();
        var path = await new EcsTaskDefinitionGenerator().WriteTaskDefinitionAsync(await ApiAsync(config), config, Cfg(), dir.Path, Ct);

        var json = await File.ReadAllTextAsync(path, Ct);
        json.ShouldNotContain(Secret);
        var secrets = JsonDocument.Parse(json).RootElement.GetProperty("containerDefinitions")[0].GetProperty("secrets");
        secrets.EnumerateArray().Select(s => s.GetProperty("valueFrom").GetString()).ShouldContain(
            "arn:aws:secretsmanager:us-east-1:ACCOUNT_ID:secret:deployify/shop/api:DB_PASSWORD::");
    }

    [Theory]
    [InlineData("cn-north-1", "aws-cn")]
    [InlineData("us-gov-west-1", "aws-us-gov")]
    [InlineData("eu-west-1", "aws")]
    public void ValueFrom_UsesTheRegionsPartition(string region, string partition)
        => EcsTaskDefinitionGenerator.SecretsManagerValueFrom(new EcsTargetConfig { Region = region, ClusterName = "c" }, "api", "K")
            .ShouldStartWith($"arn:{partition}:secretsmanager:{region}:");

    [Fact]
    public void Pin_ReplacesSecretsManagerReferencesWithTheFullArnAndVersion_AndLeavesSsmAlone()
    {
        const string json = """
            { "family": "api", "containerDefinitions": [ { "name": "api", "secrets": [
              { "name": "DB_PASSWORD", "valueFrom": "arn:aws:secretsmanager:us-east-1:ACCOUNT_ID:secret:deployify/shop/api:DB_PASSWORD::" },
              { "name": "OTHER", "valueFrom": "/deployify/shop/api/OTHER" } ] } ] }
            """;

        var pinned = EcsTaskDefinitionGenerator.PinSecretsManagerReferences(json, "arn:aws:secretsmanager:us-east-1:1:secret:deployify/shop/api-AbCdEf", "ver-1");

        var secrets = JsonDocument.Parse(pinned).RootElement.GetProperty("containerDefinitions")[0].GetProperty("secrets");
        secrets[0].GetProperty("valueFrom").GetString().ShouldBe("arn:aws:secretsmanager:us-east-1:1:secret:deployify/shop/api-AbCdEf:DB_PASSWORD::ver-1");
        secrets[1].GetProperty("valueFrom").GetString().ShouldBe("/deployify/shop/api/OTHER");
    }

    // ── Generated values store ───────────────────────────────────────────────

    [Fact]
    public async Task Store_SavesTheWholeDocument_AndLoadsItBack()
    {
        var sm = new FakeSecretsManager();
        var store = new SecretsManagerSecretStore(sm, "deployify/shop/_parameters");

        await store.SaveAsync(new Dictionary<string, SensitiveValue>
        {
            ["a.value"] = new("existing-1"),
            ["b.value"] = new("new-value-2")
        }, ["b.value"], Ct);

        var loaded = await store.LoadAsync(Ct);
        loaded.Keys.Order().ShouldBe(["a.value", "b.value"]);
        loaded["a.value"].Reveal().ShouldBe("existing-1");
        store.Description.ShouldBe("AWS Secrets Manager secret deployify/shop/_parameters");
    }

    [Fact]
    public async Task SwitchingFromSsm_CarriesGeneratedValuesOver_AndTheNextApplyCopiesThem()
    {
        var sm = new FakeSecretsManager();
        var ssm = new FakeSsm();
        ssm.Parameters["/deployify/shop/_parameters/pg-password.value"] = ("generated-pw-1", 1);
        var store = new SecretsManagerSecretStore(sm, "deployify/shop/_parameters", ssm, "/deployify/shop/_parameters");

        var session = await GeneratedSecretSession.OpenAsync(store, ct: Ct);
        session.Values["pg-password.value"].ShouldBe("generated-pw-1");
        session.Pending.ShouldBe(["pg-password.value"]);

        await session.FlushAsync(Ct);
        ssm.Parameters.Clear();

        var next = await GeneratedSecretSession.OpenAsync(
            new SecretsManagerSecretStore(sm, "deployify/shop/_parameters", ssm, "/deployify/shop/_parameters"), ct: Ct);
        next.Values["pg-password.value"].ShouldBe("generated-pw-1");
        next.Pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task OnceItsSecretExists_TheStoreNoLongerReadsSsm()
    {
        var sm = new FakeSecretsManager();
        await sm.PutSecretAsync("deployify/shop/_parameters", "{\"a.value\":\"x-1\"}", Ct);
        var ssm = Substitute.For<ISsmParameterClient>();
        var store = new SecretsManagerSecretStore(sm, "deployify/shop/_parameters", ssm, "/deployify/shop/_parameters");

        await GeneratedSecretSession.OpenAsync(store, ct: Ct);

        await ssm.DidNotReceive().GetParametersByPathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnreadablePreviousBackend_IsOnlyAWarning()
    {
        var ssm = Substitute.For<ISsmParameterClient>();
        ssm.GetParametersByPathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyDictionary<string, SsmParameter>>(
            _ => throw new InvalidOperationException("AccessDeniedException"));
        var store = new SecretsManagerSecretStore(new FakeSecretsManager(), "deployify/shop/_parameters", ssm, "/deployify/shop/_parameters");

        var session = await GeneratedSecretSession.OpenAsync(store, ct: Ct);

        session.LoadError.ShouldBeNull();
        session.Values.ShouldBeEmpty();
    }

    [Fact]
    public void ParseDocument_ErrorNeverQuotesTheValue()
    {
        var ex = Should.Throw<InvalidOperationException>(() => SecretsManagerSecrets.ParseDocument(new SensitiveValue("not-json-S3cret")));
        ex.Message.ShouldNotContain("S3cret");
    }

    // ── Target ───────────────────────────────────────────────────────────────

    [Fact]
    public void Target_DeclaresBothAwsBackends_AndPicksTheStore()
    {
        var target = new EcsDeploymentTarget(Substitute.For<IPublisher>(), NullLogger<EcsDeploymentTarget>.Instance);

        target.SupportedSecretBackends.ShouldBe([SecretBackendType.Ssm, SecretBackendType.SecretsManager]);
        var config = Config();
        config.TargetConfig["ecs"] = JsonSerializer.SerializeToElement(new { clusterName = "shop", secretsPrefix = "/apps/shop" });
        target.CreateSecretStore(config, NullLogger.Instance).Description.ShouldBe("AWS Secrets Manager secret apps/shop/_parameters");
    }

    [Fact]
    public async Task Destroy_SchedulesTheServiceSecretForDeletion()
    {
        var fake = new FakeProcessRunner()
            .When(["ssm", "get-parameters-by-path"], Ok("[]"))
            .When(["list-task-definitions"], Ok("""{"taskDefinitionArns":[]}"""))
            .When(["delete-secret"], Ok("""{"Name":"deployify/test-cluster/api"}"""));
        var target = new EcsDeploymentTarget(Substitute.For<IPublisher>(), NullLogger<EcsDeploymentTarget>.Instance) { Runner = fake };

        await target.DeleteServiceAsync(Config(), "api", new DeleteOptions(), Ct);

        var args = fake.Single("delete-secret").Args.ToList();
        args[args.IndexOf("--secret-id") + 1].ShouldBe("deployify/test-cluster/api");
        args[args.IndexOf("--recovery-window-in-days") + 1].ShouldBe("7");
    }

    [Fact]
    public async Task Destroy_KeepSecrets_LeavesTheSecret()
    {
        var fake = new FakeProcessRunner().When(["list-task-definitions"], Ok("""{"taskDefinitionArns":[]}"""));
        var target = new EcsDeploymentTarget(Substitute.For<IPublisher>(), NullLogger<EcsDeploymentTarget>.Instance) { Runner = fake };

        await target.DeleteServiceAsync(Config(), "api", new DeleteOptions { KeepSecrets = true }, Ct);

        fake.Calls.ShouldNotContain(c => c.Args.Contains("delete-secret"));
    }

    // ── CLI ──────────────────────────────────────────────────────────────────

    private static EcsCliClient Client(FakeProcessRunner fake, string? kmsKey = null)
        => new(new EcsTargetConfig { ClusterName = "c", DeployifyEnvironment = "prod", SecretsKmsKeyId = kmsKey }, NullLogger.Instance, fake);

    [Fact]
    public async Task Get_ReadsCliOutput_AndTreatsMissingOrDeletedSecretsAsAbsent()
    {
        var fake = new FakeProcessRunner()
            .When(["--secret-id", "there"], Ok("""{ "ARN": "arn:x-AbCdEf", "Name": "there", "VersionId": "ver-1", "SecretString": "{\"K\":\"v1-secret\"}" }"""))
            .When(["--secret-id", "missing"], Fail("An error occurred (ResourceNotFoundException) when calling the GetSecretValue operation"))
            .When(["--secret-id", "deleted"], Fail("An error occurred (InvalidRequestException): You can't perform this operation on the secret because it was marked for deletion."));
        var client = Client(fake);

        var secret = await client.GetSecretAsync("there", Ct);
        secret!.VersionId.ShouldBe("ver-1");
        secret.ToString().ShouldNotContain("v1-secret");
        (await client.GetSecretAsync("missing", Ct)).ShouldBeNull();
        (await client.GetSecretAsync("deleted", Ct)).ShouldBeNull();
        fake.Calls.ShouldAllBe(c => c.Sensitive);
    }

    [Fact]
    public async Task Put_CreatesAMissingSecret_TaggedAndWithTheKmsKey_ValueNeverOnTheCommandLine()
    {
        Dictionary<string, JsonElement>? createInput = null;
        var fake = new FakeProcessRunner()
            .When(["put-secret-value"], Fail("An error occurred (ResourceNotFoundException) when calling the PutSecretValue operation"))
            .When(spec => spec.Args.Contains("create-secret"), spec =>
            {
                var file = spec.Args[spec.Args.ToList().IndexOf("--cli-input-json") + 1]["file://".Length..];
                createInput = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file));
                return Ok("""{ "ARN": "arn:new-AbCdEf", "Name": "s", "VersionId": "ver-1" }""");
            });

        var version = await Client(fake, "alias/deployify").PutSecretAsync("s", "{\"K\":\"v1-secret\"}", Ct);

        version.ShouldBe(new SecretsManagerVersion("arn:new-AbCdEf", "ver-1"));
        createInput!["KmsKeyId"].GetString().ShouldBe("alias/deployify");
        createInput["Tags"].EnumerateArray().Select(t => t.GetProperty("Key").GetString())
            .ShouldBe([EcsOwnership.ManagedByTag, EcsOwnership.EnvironmentTag]);
        fake.Calls.ShouldAllBe(c => !string.Join(' ', c.Args).Contains("v1-secret"));
    }

    [Fact]
    public async Task Put_RestoresASecretScheduledForDeletion_ThenWrites()
    {
        var restored = false;
        var fake = new FakeProcessRunner()
            .When(spec => spec.Args.Contains("put-secret-value"), _ => restored
                ? Ok("""{ "ARN": "arn:s-AbCdEf", "Name": "s", "VersionId": "ver-2" }""")
                : Fail("An error occurred (InvalidRequestException): You can't perform this operation on the secret because it was marked for deletion."))
            .When(spec => spec.Args.Contains("restore-secret"), _ =>
            {
                restored = true;
                return Ok("""{ "ARN": "arn:s-AbCdEf", "Name": "s" }""");
            });

        (await Client(fake).PutSecretAsync("s", "{}", Ct)).VersionId.ShouldBe("ver-2");
        fake.Calls.ShouldNotContain(c => c.Args.Contains("create-secret"));
    }
}
