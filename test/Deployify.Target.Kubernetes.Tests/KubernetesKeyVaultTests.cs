using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

namespace Deployify.Target.Kubernetes.Tests;

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

public sealed class KubernetesKeyVaultTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private readonly KubernetesManifestGenerator _generator = new();
    private readonly KubernetesTargetConfig _cfg = new() { NamespacePrefix = "shop" };

    private static EnvironmentConfig Config(
        KeyVaultKubernetesIdentity identity = KeyVaultKubernetesIdentity.WorkloadIdentity, string secret = SecretManifests.Secret)
    {
        var config = SecretManifests.Config(SecretBackendType.KeyVault);
        config.Parameters["pg-password"] = secret;
        config.Secrets.KeyVault.VaultName = "shop-kv";
        config.Secrets.KeyVault.TenantId = TenantId;
        config.Secrets.KeyVault.ClientId = ClientId;
        config.Secrets.KeyVault.KubernetesIdentity = identity;
        return config;
    }

    private async Task<KubernetesServiceManifest> ServiceAsync(EnvironmentConfig config, string name = "api")
        => (await _generator.GenerateAsync(SecretManifests.Manifest(), config, _cfg, TestContext.Current.CancellationToken)).Single(s => s.ServiceName == name);

    private async Task<Dictionary<string, string>> WriteAsync(TempDir dir, EnvironmentConfig config, string name = "api")
    {
        var files = await _generator.WriteYamlAsync(await ServiceAsync(config, name), dir.Path, TestContext.Current.CancellationToken);
        return files.ToDictionary(f => Path.GetFileName(f), f => File.ReadAllText(f).ReplaceLineEndings("\n"));
    }

    private static KubernetesDeploymentTarget Target(FakeKeyVault kv) => new(
        Substitute.For<IPublisher>(), NullLogger<KubernetesDeploymentTarget>.Instance)
    {
        KeyVaultClientFactory = _ => kv
    };

    // ── Manifests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SecretProviderClass_MountsVaultSecretsAndSyncsThem()
    {
        using var dir = new TempDir();

        var files = await WriteAsync(dir, Config());

        files["secretproviderclass.yaml"].ShouldBe("""
            apiVersion: secrets-store.csi.x-k8s.io/v1
            kind: SecretProviderClass
            metadata:
              name: api-keyvault
              namespace: shop-api
              labels:
                app: api
                app.kubernetes.io/managed-by: deployify
            spec:
              provider: azure
              secretObjects:
                - secretName: api-secrets
                  type: Opaque
                  labels:
                    app: api
                  data:
                    - objectName: "pg-password"
                      key: "pg-password"
              parameters:
                usePodIdentity: "false"
                clientID: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
                keyvaultName: "shop-kv"
                tenantId: "11111111-2222-3333-4444-555555555555"
                objects: |-
                  array:
                    - |
                      objectName: deployify-shop-api-pg-password
                      objectType: secret
                      objectAlias: pg-password

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Deployment_UsesWorkloadIdentityCsiVolumeAndSecretKeyRefs()
    {
        using var dir = new TempDir();

        var files = await WriteAsync(dir, Config());

        var deployment = files["deployment.yaml"];
        deployment.ShouldContain("""
                  labels:
                    app: api
                    azure.workload.identity/use: "true"
            """.ReplaceLineEndings("\n"));
        deployment.ShouldContain("serviceAccountName: api");
        deployment.ShouldContain("value: \"Host=pg.shop-pg;Password=$(Parameters__pg_password);Opt=a$$b\"");
        deployment.ShouldContain("name: api-secrets");
        deployment.ShouldContain("""
                      volumeMounts:
                        - name: keyvault-secrets
                          mountPath: /mnt/secrets-store
                          readOnly: true
                  volumes:
                    - name: keyvault-secrets
                      csi:
                        driver: secrets-store.csi.k8s.io
                        readOnly: true
                        volumeAttributes:
                          secretProviderClass: api-keyvault
            """.ReplaceLineEndings("\n"));
        files["serviceaccount.yaml"].ShouldContain($"azure.workload.identity/client-id: \"{ClientId}\"");
        files.Keys.ShouldNotContain("secret.yaml");
        files.Values.ShouldAllBe(text => !text.Contains(SecretManifests.Secret));
    }

    [Fact]
    public async Task Containers_WorkTheSameWay()
    {
        // Env vars come from a synced Secret, so a postgres container reads its password as usual.
        using var dir = new TempDir();

        var files = await WriteAsync(dir, Config(), "pg");

        files["deployment.yaml"].ShouldContain("secretKeyRef");
        files["deployment.yaml"].ShouldContain("secretProviderClass: pg-keyvault");
        files["secretproviderclass.yaml"].ShouldContain("objectName: deployify-shop-pg-pg-password");
    }

    [Fact]
    public async Task VmManagedIdentity_NeedsNoServiceAccount()
    {
        using var dir = new TempDir();

        var files = await WriteAsync(dir, Config(KeyVaultKubernetesIdentity.VmManagedIdentity));

        files["secretproviderclass.yaml"].ShouldContain("useVMManagedIdentity: \"true\"");
        files["secretproviderclass.yaml"].ShouldContain($"userAssignedIdentityID: \"{ClientId}\"");
        files["secretproviderclass.yaml"].ShouldNotContain("clientID:");
        files.Keys.ShouldNotContain("serviceaccount.yaml");
        files["deployment.yaml"].ShouldNotContain("azure.workload.identity");
        files["deployment.yaml"].ShouldNotContain("serviceAccountName");
    }

    [Fact]
    public async Task SwitchingAway_RemovesProviderClassAndServiceAccount()
    {
        using var dir = new TempDir();
        await WriteAsync(dir, Config());

        await WriteAsync(dir, SecretManifests.Config(SecretBackendType.KubernetesSecret));

        File.Exists(Path.Combine(dir.Path, "api", "secretproviderclass.yaml")).ShouldBeFalse();
        File.Exists(Path.Combine(dir.Path, "api", "serviceaccount.yaml")).ShouldBeFalse();
    }

    [Fact]
    public async Task MissingSettings_AreReportedTogether()
    {
        var config = SecretManifests.Config(SecretBackendType.KeyVault);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => ServiceAsync(config));

        ex.Message.ShouldContain("secrets.keyVault.vaultName");
        ex.Message.ShouldContain("secrets.keyVault.tenantId");
        ex.Message.ShouldContain("secrets.keyVault.clientId");
    }

    [Fact]
    public async Task CloudName_IsPassedToTheProvider()
    {
        var config = Config();
        config.Secrets.KeyVault.CloudName = "AzureChinaCloud";
        using var dir = new TempDir();

        var files = await _generator.WriteYamlAsync(await ServiceAsync(config), dir.Path, TestContext.Current.CancellationToken);

        File.ReadAllText(files.Single(f => f.EndsWith("secretproviderclass.yaml"))).ShouldContain("cloudName: \"AzureChinaCloud\"");
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_WritesToVaultNotToTheCluster()
    {
        var config = Config();
        var kv = new FakeKeyVault();
        var cluster = new FakeCluster();

        var changed = await Target(kv).SyncSecretsAsync(await ServiceAsync(config), config, cluster, TestContext.Current.CancellationToken);

        changed.ShouldBeFalse();
        cluster.Applied.ShouldBeEmpty("the CSI driver owns the synced Secret");
        var stored = kv.Secrets["deployify-shop-api-pg-password"];
        stored.Value.ShouldBe(SecretManifests.Secret);
        stored.Tags[KeyVaultSecrets.StoreTag].ShouldBe("deployify-shop");
        stored.Tags[KeyVaultSecrets.AppTag].ShouldBe("api");
        var reference = config.Secrets.References["api/pg-password"];
        reference.Backend.ShouldBe(SecretBackendType.KeyVault);
        reference.Location.ShouldBe("https://shop-kv.vault.azure.net/secrets/deployify-shop-api-pg-password");
        reference.Version.ShouldBe("v1");
    }

    [Fact]
    public async Task Sync_RotationAsksForRestart()
    {
        var kv = new FakeKeyVault();
        var target = Target(kv);
        var config = Config();
        await target.SyncSecretsAsync(await ServiceAsync(config), config, new FakeCluster(), TestContext.Current.CancellationToken);
        (await target.SyncSecretsAsync(await ServiceAsync(config), config, new FakeCluster(), TestContext.Current.CancellationToken)).ShouldBeFalse();
        kv.Sets.Count.ShouldBe(1);

        var rotated = Config(secret: "R0tated-pw");
        (await target.SyncSecretsAsync(await ServiceAsync(rotated), rotated, new FakeCluster(), TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public void Store_UsesNamespacePrefix()
    {
        var store = Target(new FakeKeyVault()).CreateSecretStore(Config(), NullLogger.Instance);

        store.Description.ShouldBe("Key Vault shop-kv (deployify-shop-parameters-*)");
    }
}
