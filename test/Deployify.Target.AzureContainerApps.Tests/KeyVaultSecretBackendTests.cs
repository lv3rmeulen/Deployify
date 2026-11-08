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
using Deployify.Core.SecretBackends.KeyVault;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.AzureContainerApps.Tests;

/// <summary>An in-memory Key Vault.</summary>
internal sealed class FakeKeyVault : IKeyVaultClient
{
    private int _version;

    public Dictionary<string, (string Value, string Version, Dictionary<string, string> Tags)> Secrets { get; } = new(StringComparer.Ordinal);

    public List<string> Sets { get; } = [];

    public Task<KeyVaultSecret?> GetSecretAsync(string vaultName, string name, CancellationToken ct = default)
        => Task.FromResult(Secrets.TryGetValue(name, out var s) ? new KeyVaultSecret(name, new SensitiveValue(s.Value), s.Version) : null);

    public Task<IReadOnlyList<KeyVaultSecretInfo>> ListSecretsAsync(string vaultName, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<KeyVaultSecretInfo>>([.. Secrets.Select(s => new KeyVaultSecretInfo(s.Key, s.Value.Tags))]);

    public Task<string> SetSecretAsync(string vaultName, string name, string value, IReadOnlyDictionary<string, string> tags, CancellationToken ct = default)
    {
        Sets.Add(name);
        var version = $"v{++_version}";
        Secrets[name] = (value, version, new Dictionary<string, string>(tags));
        return Task.FromResult(version);
    }
}

public sealed class KeyVaultSecretBackendTests
{
    private const string Secret = "S3cret-pw";
    private const string IdentityId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/apps";

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
              "env": { "ConnectionStrings__db": "Host=pg;Password={pg-password.value}", "PLAIN": "hello" },
              "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8080 } }
            }
          }
        }
        """;

    private static AspireManifest Manifest() => JsonSerializer.Deserialize<AspireManifest>(ManifestJson)!;

    private static AcaTargetConfig Cfg(string? acrServer = null, string? acrIdentity = null) => new()
    {
        SubscriptionId = "sub", ResourceGroup = "rg", EnvironmentName = "prod-env",
        AcrLoginServer = acrServer, AcrIdentityResourceId = acrIdentity
    };

    private static EnvironmentConfig Config(string secret = Secret, string? identity = IdentityId)
    {
        var config = AcaFixtures.AcaConfig();
        config.Parameters["pg-password"] = secret;
        config.Secrets.Backend = SecretBackendType.KeyVault;
        config.Secrets.KeyVault.VaultName = "shop-kv";
        config.Secrets.KeyVault.IdentityResourceId = identity;
        return config;
    }

    private static AcaApp Api(EnvironmentConfig config, AcaTargetConfig cfg)
        => new AcaManifestGenerator().Generate(Manifest(), config, cfg).Single(a => a.Name == "api");

    private static async Task<(JsonElement Spec, string Text)> SpecAsync(EnvironmentConfig config, AcaTargetConfig cfg)
    {
        using var dir = new AcaFixtures.TempDir();
        var path = await new AcaManifestGenerator().WriteAppSpecAsync(Api(config, cfg), config, cfg, dir.Path, TestContext.Current.CancellationToken);
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        return (JsonDocument.Parse(text).RootElement.Clone(), text);
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(new[] { "deployify-prod", "api", "connectionstrings--db" }, "deployify-prod-api-connectionstrings-db")]
    [InlineData(new[] { "p", "parameters", "pg-password.value" }, "p-parameters-pg-password-value")]
    [InlineData(new[] { "--", "__" }, "secret")]
    public void SecretName_IsValidForKeyVault(string[] parts, string expected)
        => KeyVaultSecrets.SecretName(parts).ShouldBe(expected);

    [Fact]
    public void SecretName_TooLong_IsShortenedButUnique()
    {
        var a = KeyVaultSecrets.SecretName(new string('a', 200), "one");
        var b = KeyVaultSecrets.SecretName(new string('a', 200), "two");

        a.Length.ShouldBeLessThanOrEqualTo(127);
        a.ShouldNotBe(b);
        a.ShouldMatch("^[a-zA-Z0-9-]+$");
    }

    [Fact]
    public void Prefix_DefaultsToEnvironmentName()
    {
        AcaKeyVault.Prefix(new KeyVaultConfig(), Cfg()).ShouldBe("deployify-prod-env");
        AcaKeyVault.Prefix(new KeyVaultConfig { SecretPrefix = "shop" }, Cfg()).ShouldBe("shop");
    }

    [Fact]
    public void VaultUri_DefaultsToPublicCloudAndCanBeOverridden()
    {
        KeyVaultSecrets.SecretUrl(new KeyVaultConfig { VaultName = "kv" }, "s").ShouldBe("https://kv.vault.azure.net/secrets/s");
        var china = new KeyVaultConfig { VaultUri = "https://kv.vault.azure.cn/" };
        KeyVaultSecrets.SecretUrl(china, "s").ShouldBe("https://kv.vault.azure.cn/secrets/s");
        KeyVaultSecrets.VaultName(china).ShouldBe("kv");
        Should.Throw<InvalidOperationException>(() => new KeyVaultConfig().ResolveVaultUri());
    }

    // ── App spec ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Spec_ReferencesKeyVaultAndHoldsNoValue()
    {
        var (spec, text) = await SpecAsync(Config(), Cfg());

        text.ShouldNotContain(Secret);
        var secret = spec.GetProperty("properties").GetProperty("configuration").GetProperty("secrets").EnumerateArray().Single();
        secret.GetProperty("name").GetString().ShouldBe("connectionstrings--db");
        secret.GetProperty("keyVaultUrl").GetString()
            .ShouldBe("https://shop-kv.vault.azure.net/secrets/deployify-prod-env-api-connectionstrings-db");
        secret.GetProperty("identity").GetString().ShouldBe(IdentityId);
        secret.TryGetProperty("value", out _).ShouldBeFalse();

        var env = spec.GetProperty("properties").GetProperty("template").GetProperty("containers")[0].GetProperty("env");
        env.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "ConnectionStrings__db")
            .GetProperty("secretRef").GetString().ShouldBe("connectionstrings--db");
    }

    [Fact]
    public async Task Spec_AssignsTheKeyVaultIdentity()
    {
        var (spec, _) = await SpecAsync(Config(), Cfg());

        var identity = spec.GetProperty("identity");
        identity.GetProperty("type").GetString().ShouldBe("UserAssigned");
        identity.GetProperty("userAssignedIdentities").EnumerateObject().Select(p => p.Name).ShouldBe([IdentityId]);
    }

    [Fact]
    public async Task Spec_CombinesRegistryAndKeyVaultIdentities()
    {
        var acrIdentity = IdentityId.Replace("apps", "acr");

        var (spec, _) = await SpecAsync(Config(), Cfg("reg.azurecr.io", acrIdentity));

        spec.GetProperty("identity").GetProperty("userAssignedIdentities").EnumerateObject().Select(p => p.Name)
            .ShouldBe([acrIdentity, IdentityId]);
    }

    [Fact]
    public async Task Spec_WithoutIdentity_UsesSystemAlongsideRegistryIdentity()
    {
        var acrIdentity = IdentityId.Replace("apps", "acr");

        var (spec, _) = await SpecAsync(Config(identity: null), Cfg("reg.azurecr.io", acrIdentity));

        spec.GetProperty("identity").GetProperty("type").GetString().ShouldBe("SystemAssigned,UserAssigned");
        spec.GetProperty("properties").GetProperty("configuration").GetProperty("secrets")[0]
            .GetProperty("identity").GetString().ShouldBe("system");
    }

    [Fact]
    public async Task Spec_InlineBackend_IsUnchanged()
    {
        var config = Config();
        config.Secrets.Backend = SecretBackendType.Inline;

        var (spec, text) = await SpecAsync(config, Cfg());

        text.ShouldContain(Secret);
        spec.TryGetProperty("identity", out _).ShouldBeFalse();
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_WritesSecretWithTagsAndRecordsReference()
    {
        var config = Config();
        var kv = new FakeKeyVault();

        var changed = await AcaDeploymentTarget.SyncSecretsAsync(Api(config, Cfg()), config, Cfg(), kv, TestContext.Current.CancellationToken);

        changed.ShouldBeFalse("a new secret needs no restart");
        var stored = kv.Secrets["deployify-prod-env-api-connectionstrings-db"];
        stored.Value.ShouldBe($"Host=pg;Password={Secret}");
        stored.Tags[KeyVaultSecrets.StoreTag].ShouldBe("deployify-prod-env");
        stored.Tags[KeyVaultSecrets.AppTag].ShouldBe("api");
        var reference = config.Secrets.References["api/ConnectionStrings__db"];
        reference.Backend.ShouldBe(SecretBackendType.KeyVault);
        reference.Location.ShouldBe("https://shop-kv.vault.azure.net/secrets/deployify-prod-env-api-connectionstrings-db");
        reference.Version.ShouldBe("v1");
    }

    [Fact]
    public async Task Sync_SkipsUnchangedAndReportsRotation()
    {
        var kv = new FakeKeyVault();
        var config = Config();
        await AcaDeploymentTarget.SyncSecretsAsync(Api(config, Cfg()), config, Cfg(), kv, TestContext.Current.CancellationToken);
        (await AcaDeploymentTarget.SyncSecretsAsync(Api(config, Cfg()), config, Cfg(), kv, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
        kv.Sets.Count.ShouldBe(1);

        var rotated = Config(secret: "R0tated-pw");
        (await AcaDeploymentTarget.SyncSecretsAsync(Api(rotated, Cfg()), rotated, Cfg(), kv, TestContext.Current.CancellationToken))
            .ShouldBeTrue("a changed secret needs the running revision restarted");
        kv.Sets.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Sync_OtherBackend_DoesNothing()
    {
        var config = Config();
        config.Secrets.Backend = SecretBackendType.Inline;
        var kv = new FakeKeyVault();

        await AcaDeploymentTarget.SyncSecretsAsync(Api(config, Cfg()), config, Cfg(), kv, TestContext.Current.CancellationToken);

        kv.Sets.ShouldBeEmpty();
    }

    // ── Generated values ──────────────────────────────────────────────────────

    [Fact]
    public async Task Store_SavesTaggedAndLoadsOnlyItsOwnSecrets()
    {
        var kv = new FakeKeyVault();
        kv.Secrets["other-env-parameters-x"] = ("nope", "v9", new() { [KeyVaultSecrets.StoreTag] = "other", [KeyVaultSecrets.KeyTag] = "x.value" });
        kv.Secrets["unrelated"] = ("nope", "v9", new());
        var store = new KeyVaultSecretStore(kv, "shop-kv", "deployify-prod-env");

        await store.SaveAsync(new Dictionary<string, SensitiveValue>
        {
            ["pg-password.value"] = new("generated-pw-1"),
            ["redis.value"] = new("existing-pw-2")
        }, ["pg-password.value"], TestContext.Current.CancellationToken);

        kv.Sets.ShouldBe(["deployify-prod-env-parameters-pg-password-value"]);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        loaded.Keys.ShouldBe(["pg-password.value"]);
        loaded["pg-password.value"].Reveal().ShouldBe("generated-pw-1");
    }

    [Fact]
    public void Target_DeclaresKeyVaultAndItsStore()
    {
        var target = new AcaDeploymentTarget(Substitute.For<IPublisher>(), NullLogger<AcaDeploymentTarget>.Instance);
        var config = Config();
        config.TargetConfig["aca"] = JsonSerializer.SerializeToElement(new { environmentName = "prod-env" });

        target.SupportedSecretBackends.ShouldBe([SecretBackendType.KeyVault]);
        target.CreateSecretStore(config, NullLogger.Instance).Description.ShouldBe("Key Vault shop-kv (deployify-prod-env-parameters-*)");
    }

    // ── CLI output parsing ────────────────────────────────────────────────────

    [Fact]
    public void ParseSecret_ReadsValueAndVersion()
    {
        var secret = AzureCliKeyVaultClient.ParseSecret("""
            { "id": "https://shop-kv.vault.azure.net/secrets/db/0123abcd", "name": "db", "value": "v4lue-pw", "tags": null }
            """);

        secret.Name.ShouldBe("db");
        secret.Version.ShouldBe("0123abcd");
        secret.Value.Reveal().ShouldBe("v4lue-pw");
        secret.ToString().ShouldNotContain("v4lue-pw");
    }

    [Fact]
    public void ParseSecretList_ReadsNamesAndTags()
    {
        var list = AzureCliKeyVaultClient.ParseSecretList("""
            [
              { "id": "https://kv.vault.azure.net/secrets/a", "name": "a", "tags": { "deployify-store": "p", "deployify-key": "a.value" } },
              { "id": "https://kv.vault.azure.net/secrets/b", "name": "b", "tags": null }
            ]
            """);

        list.Select(s => s.Name).ShouldBe(["a", "b"]);
        list[0].Tags[KeyVaultSecrets.KeyTag].ShouldBe("a.value");
        list[1].Tags.ShouldBeEmpty();
    }
}
