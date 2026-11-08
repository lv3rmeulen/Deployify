using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.SecretBackends.Vault;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Kubernetes.Tests;

internal static class SecretManifests
{
    public const string Secret = "S3cret-pw";

    /// <summary>A postgres container and a project that embeds its password in a connection string.</summary>
    public const string Json = """
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
              "type": "project.v0",
              "path": "Api/Api.csproj",
              "env": { "ConnectionStrings__db": "{pg.connectionString}", "PLAIN": "hello" },
              "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8080 } }
            }
          }
        }
        """;

    public static AspireManifest Manifest() => JsonSerializer.Deserialize<AspireManifest>(Json)!;

    public static EnvironmentConfig Config(SecretBackendType backend)
    {
        var config = Fixtures.KubernetesConfig(prefix: "shop");
        config.Parameters["pg-password"] = Secret;
        config.Secrets.Backend = backend;
        config.Secrets.Vault.Address = "https://vault.test";
        config.Secrets.Vault.Token = "t0ken";
        return config;
    }
}

public sealed class KubernetesSecretEnvTests
{
    private static ParameterPart P(string name, bool secret = true) => new(name, "value", "v4lue-" + name, secret);

    private static ResolvedValue V(params object[] parts) => ResolvedValue.Concat(parts.Select(p => p switch
    {
        string s => ResolvedValue.Text(s),
        ValuePart v => ResolvedValue.Of(v),
        _ => throw new ArgumentException()
    }));

    [Fact]
    public void BareSecret_IsASecretKeyRef()
    {
        var env = KubernetesSecretEnv.Render("api-secrets", new Dictionary<string, ResolvedValue> { ["PASSWORD"] = V(P("pw")) });

        env.ShouldHaveSingleItem().ShouldBe(new KubernetesEnvVar("PASSWORD", SecretName: "api-secrets", SecretKey: "pw"));
    }

    [Fact]
    public void ComposedValue_UsesHelperVariableDefinedFirst()
    {
        var env = KubernetesSecretEnv.Render("api-secrets", new Dictionary<string, ResolvedValue>
        {
            ["ConnectionStrings__db"] = V("Host=pg;Password=", P("pg-password"), ";Opt=a$b")
        });

        env.Count.ShouldBe(2);
        env[0].ShouldBe(new KubernetesEnvVar("Parameters__pg_password", SecretName: "api-secrets", SecretKey: "pg-password"));
        env[1].ShouldBe(new KubernetesEnvVar("ConnectionStrings__db", "Host=pg;Password=$(Parameters__pg_password);Opt=a$$b"));
    }

    [Fact]
    public void ComposedValue_ReusesABareSecretVariable()
    {
        var env = KubernetesSecretEnv.Render("s", new Dictionary<string, ResolvedValue>
        {
            ["PASSWORD"] = V(P("pw")),
            ["URL"] = V("u:", P("pw"))
        });

        env.Select(e => e.Name).ShouldBe(["PASSWORD", "URL"]);
        env[1].Value.ShouldBe("u:$(PASSWORD)");
    }

    [Fact]
    public void PlainParametersAndUnresolvedReferences_AreEscapedText()
    {
        var env = KubernetesSecretEnv.Render("s", new Dictionary<string, ResolvedValue>
        {
            ["URL"] = V(P("user", secret: false), ":", P("pw"), "@", new UnresolvedPart("{x.$y}"))
        });

        env[^1].Value.ShouldBe("v4lue-user:$(Parameters__pw)@{x.$$y}");
    }

    [Fact]
    public void HelperName_AvoidsExistingVariables()
    {
        var env = KubernetesSecretEnv.Render("s",
            new Dictionary<string, ResolvedValue> { ["URL"] = V("u:", P("pw")) },
            plainNames: ["Parameters__pw"]);

        env[0].Name.ShouldBe("Parameters__pw_");
        env[1].Value.ShouldBe("u:$(Parameters__pw_)");
    }
}

public sealed class KubernetesSecretModeTests
{
    private readonly KubernetesManifestGenerator _sut = new();
    private readonly KubernetesTargetConfig _cfg = new() { NamespacePrefix = "shop" };

    private async Task<(KubernetesServiceManifest Svc, Dictionary<string, string> Files)> WriteAsync(
        TempDir dir, string service, EnvironmentConfig config)
    {
        var svc = _sut.Generate(SecretManifests.Manifest(), config, _cfg).Single(s => s.ServiceName == service);
        var files = await _sut.WriteYamlAsync(svc, dir.Path, TestContext.Current.CancellationToken);
        return (svc, files.ToDictionary(Path.GetFileName, File.ReadAllText)!);
    }

    [Fact]
    public async Task NoSecretValueOrSecretManifestIsWritten()
    {
        using var dir = new TempDir();

        var (svc, files) = await WriteAsync(dir, "api", SecretManifests.Config(SecretBackendType.KubernetesSecret));

        files.Keys.ShouldNotContain("secret.yaml");
        files.Values.ShouldAllBe(text => !text.Contains(SecretManifests.Secret));
        svc.UsesSecretRefs.ShouldBeTrue();
        svc.SecretData["pg-password"].Reveal().ShouldBe(SecretManifests.Secret);
    }

    [Fact]
    public async Task Deployment_ReferencesTheSecretAndExpandsIt()
    {
        using var dir = new TempDir();

        var (_, files) = await WriteAsync(dir, "api", SecretManifests.Config(SecretBackendType.KubernetesSecret));

        var deployment = files["deployment.yaml"].ReplaceLineEndings("\n");
        deployment.ShouldContain("""
                      env:
                        - name: Parameters__pg_password
                          valueFrom:
                            secretKeyRef:
                              name: api-secrets
                              key: "pg-password"
                        - name: ConnectionStrings__db
                          value: "Host=pg.shop-pg;Password=$(Parameters__pg_password);Opt=a$$b"
            """.ReplaceLineEndings("\n"));
        deployment.ShouldContain("configMapRef");
        deployment.ShouldNotContain("secretRef:\n");
        files["configmap.yaml"].ShouldContain("PLAIN");
    }

    [Fact]
    public async Task SwitchingFromInline_RemovesTheOldSecretManifest()
    {
        using var dir = new TempDir();
        await WriteAsync(dir, "api", SecretManifests.Config(SecretBackendType.Inline));
        File.Exists(Path.Combine(dir.Path, "api", "secret.yaml")).ShouldBeTrue();

        await WriteAsync(dir, "api", SecretManifests.Config(SecretBackendType.KubernetesSecret));

        File.Exists(Path.Combine(dir.Path, "api", "secret.yaml")).ShouldBeFalse();
    }

    [Fact]
    public async Task InlineBackend_IsUnchanged()
    {
        using var dir = new TempDir();

        var (_, files) = await WriteAsync(dir, "api", SecretManifests.Config(SecretBackendType.Inline));

        files["secret.yaml"].ShouldContain(SecretManifests.Secret);
        files["deployment.yaml"].ShouldContain("secretRef:");
        files["deployment.yaml"].ShouldNotContain("secretKeyRef");
    }

    [Fact]
    public async Task LeakGuard_StopsASecretCopiedIntoPlainConfig()
    {
        using var dir = new TempDir();
        var config = SecretManifests.Config(SecretBackendType.KubernetesSecret);
        // A plain override that happens to hold the secret value would end up in the ConfigMap.
        config.ServiceOverrides["api"] = new ServiceOverride { Env = { ["DEBUG_COPY"] = SecretManifests.Secret } };

        var ex = await Should.ThrowAsync<SecretLeakException>(() => WriteAsync(dir, "api", config));

        ex.Message.ShouldContain("configmap.yaml");
        ex.Message.ShouldNotContain(SecretManifests.Secret);
        File.Exists(Path.Combine(dir.Path, "api", "configmap.yaml")).ShouldBeFalse();
    }
}

public sealed class VaultModeTests
{
    private readonly KubernetesManifestGenerator _sut = new();
    private readonly KubernetesTargetConfig _cfg = new() { NamespacePrefix = "shop" };

    private async Task<(KubernetesServiceManifest Svc, Dictionary<string, string> Files)> WriteAsync(TempDir dir, string service)
    {
        var svc = _sut.Generate(SecretManifests.Manifest(), SecretManifests.Config(SecretBackendType.Vault), _cfg)
            .Single(s => s.ServiceName == service);
        var files = await _sut.WriteYamlAsync(svc, dir.Path, TestContext.Current.CancellationToken);
        return (svc, files.ToDictionary(Path.GetFileName, File.ReadAllText)!);
    }

    [Fact]
    public async Task Project_GetsAgentAnnotationsServiceAccountAndPolicy()
    {
        using var dir = new TempDir();

        var (svc, files) = await WriteAsync(dir, "api");

        svc.UsesVaultAgent.ShouldBeTrue();
        svc.VaultKvApiPath.ShouldBe("secret/data/deployify/shop/api");
        var deployment = files["deployment.yaml"].ReplaceLineEndings("\n");
        deployment.ShouldContain("vault.hashicorp.com/agent-inject: \"true\"");
        deployment.ShouldContain("vault.hashicorp.com/role: \"shop-api\"");
        deployment.ShouldContain("vault.hashicorp.com/agent-inject-secret-appsettings.secrets.json: \"secret/data/deployify/shop/api\"");
        deployment.ShouldContain("vault.hashicorp.com/agent-inject-template-appsettings.secrets.json: |-\n" +
                                 "          {{- with secret \"secret/data/deployify/shop/api\" -}}");
        deployment.ShouldContain("\"ConnectionStrings:db\": {{ print \"Host=pg.shop-pg;Password=\" (index .Data.data \"pg-password\") \";Opt=a$b\" | toJSON }}");
        deployment.ShouldContain("serviceAccountName: api");
        deployment.ShouldContain("- name: DEPLOYIFY_SECRETS_FILE");
        deployment.ShouldContain("value: \"/vault/secrets/appsettings.secrets.json\"");
        deployment.ShouldNotContain("secretKeyRef");
        files["serviceaccount.yaml"].ShouldContain("kind: ServiceAccount");
        files["vault-policy.hcl"].ShouldContain("path \"secret/data/deployify/shop/api\"");
        files.Values.ShouldAllBe(text => !text.Contains(SecretManifests.Secret));
    }

    [Fact]
    public async Task Container_FallsBackToSecretKeyRef()
    {
        // postgres can't read a .NET configuration file, so it gets a Kubernetes Secret instead.
        using var dir = new TempDir();

        var (svc, files) = await WriteAsync(dir, "pg");

        svc.UsesVaultAgent.ShouldBeFalse();
        svc.UsesSecretRefs.ShouldBeTrue();
        files["deployment.yaml"].ShouldContain("secretKeyRef");
        files["deployment.yaml"].ShouldNotContain("vault.hashicorp.com");
        files.Keys.ShouldNotContain("serviceaccount.yaml");
        files.Values.ShouldAllBe(text => !text.Contains(SecretManifests.Secret));
    }

    [Fact]
    public async Task VaultAnnotations_SitNextToDaprAnnotations()
    {
        using var dir = new TempDir();
        var manifest = SecretManifests.Manifest();
        manifest.Resources["api"].Annotations = new Dictionary<string, string> { [DaprConstants.EnabledAnnotation] = "true" };
        var svc = _sut.Generate(manifest, SecretManifests.Config(SecretBackendType.Vault), _cfg).Single(s => s.ServiceName == "api");

        await _sut.WriteYamlAsync(svc, dir.Path, TestContext.Current.CancellationToken);

        var deployment = File.ReadAllText(Path.Combine(dir.Path, "api", "deployment.yaml")).ReplaceLineEndings("\n");
        deployment.Split("annotations:").Length.ShouldBe(2, "a single annotations block");
        deployment.ShouldContain("dapr.io/enabled: \"true\"");
        deployment.ShouldContain("vault.hashicorp.com/agent-inject: \"true\"");
        // Pod annotations must be in template.metadata; one level up, Kubernetes rejects them as an unknown field.
        deployment.ShouldContain("""
              template:
                metadata:
                  labels:
                    app: api
                  annotations:
                    dapr.io/enabled: "true"
            """.ReplaceLineEndings("\n"));
    }
}

/// <summary>Records what would be applied to the cluster.</summary>
internal sealed class FakeCluster : IKubernetesSecretClient
{
    private int _version = 100;

    public Dictionary<string, Dictionary<string, SensitiveValue>> Secrets { get; } = [];

    public List<JsonDocument> Applied { get; } = [];

    public Task<IReadOnlyDictionary<string, SensitiveValue>?> GetSecretDataAsync(string ns, string name, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, SensitiveValue>?>(Secrets.GetValueOrDefault($"{ns}/{name}"));

    public Task<string?> GetResourceVersionAsync(string kind, string ns, string name, CancellationToken ct = default)
        => Task.FromResult(Secrets.ContainsKey($"{ns}/{name}") ? _version.ToString() : null);

    public Task<string> ServerSideApplyAsync(string json, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(json);
        Applied.Add(doc);
        var root = doc.RootElement;
        if (root.GetProperty("kind").GetString() == "Secret")
        {
            var data = KubernetesSecretManifest.ReadData(json).ToDictionary();
            var key = $"{root.GetProperty("metadata").GetProperty("namespace").GetString()}/{root.GetProperty("metadata").GetProperty("name").GetString()}";
            if (!Secrets.TryGetValue(key, out var existing) || !existing.OrderBy(k => k.Key).SequenceEqual(data.OrderBy(k => k.Key)))
            {
                _version++;
            }

            Secrets[key] = data;
        }

        return Task.FromResult(_version.ToString());
    }
}

public sealed class KubernetesSecretSyncTests
{
    private readonly KubernetesManifestGenerator _generator = new();
    private readonly KubernetesTargetConfig _cfg = new() { NamespacePrefix = "shop" };

    private KubernetesServiceManifest Service(EnvironmentConfig config, string name = "api")
        => _generator.Generate(SecretManifests.Manifest(), config, _cfg).Single(s => s.ServiceName == name);

    private static KubernetesDeploymentTarget Target(FakeVaultHandler? vault = null) => new(
        Substitute.For<IPublisher>(), NullLogger<KubernetesDeploymentTarget>.Instance)
    {
        VaultClientFactory = c => new VaultClient(new Uri(c.Address!), c.Token!, handler: vault ?? new FakeVaultHandler())
    };

    [Fact]
    public async Task KubernetesSecret_AppliesNamespaceThenSecretAndRecordsReference()
    {
        var config = SecretManifests.Config(SecretBackendType.KubernetesSecret);
        var cluster = new FakeCluster();

        var changed = await Target().SyncSecretsAsync(Service(config), config, cluster, TestContext.Current.CancellationToken);

        changed.ShouldBeFalse("a new secret needs no restart");
        cluster.Applied.Select(d => d.RootElement.GetProperty("kind").GetString()).ShouldBe(["Namespace", "Secret"]);
        cluster.Applied[0].RootElement.GetProperty("metadata").GetProperty("labels")
            .GetProperty("app.kubernetes.io/managed-by").GetString().ShouldBe("deployify");
        cluster.Secrets["shop-api/api-secrets"]["pg-password"].Reveal().ShouldBe(SecretManifests.Secret);

        var reference = config.Secrets.References["api/pg-password"];
        reference.Backend.ShouldBe(SecretBackendType.KubernetesSecret);
        reference.Location.ShouldBe("shop-api/api-secrets");
        reference.Key.ShouldBe("pg-password");
        reference.Version.ShouldNotBeNull();
    }

    [Fact]
    public async Task KubernetesSecret_ReportsChangeOnlyWhenDataChanged()
    {
        var config = SecretManifests.Config(SecretBackendType.KubernetesSecret);
        var cluster = new FakeCluster();
        var target = Target();
        await target.SyncSecretsAsync(Service(config), config, cluster, TestContext.Current.CancellationToken);

        (await target.SyncSecretsAsync(Service(config), config, cluster, TestContext.Current.CancellationToken)).ShouldBeFalse();

        var rotated = SecretManifests.Config(SecretBackendType.KubernetesSecret);
        rotated.Parameters["pg-password"] = "R0tated-pw";
        (await target.SyncSecretsAsync(Service(rotated), rotated, cluster, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task Inline_DoesNothing()
    {
        var config = SecretManifests.Config(SecretBackendType.Inline);
        var cluster = new FakeCluster();

        (await Target().SyncSecretsAsync(Service(config), config, cluster, TestContext.Current.CancellationToken)).ShouldBeFalse();

        cluster.Applied.ShouldBeEmpty();
        config.Secrets.References.ShouldBeEmpty();
    }

    [Fact]
    public async Task Vault_WritesKvPolicyAndRole()
    {
        var config = SecretManifests.Config(SecretBackendType.Vault);
        var vault = new FakeVaultHandler
        {
            Respond = (req, _) => req.Method == HttpMethod.Get
                ? FakeVaultHandler.Json("{}", HttpStatusCode.NotFound)
                : req.RequestUri!.AbsolutePath.StartsWith("/v1/secret/")
                    ? FakeVaultHandler.Json("{\"data\":{\"version\":1}}")
                    : new HttpResponseMessage(HttpStatusCode.NoContent)
        };
        var cluster = new FakeCluster();

        var changed = await Target(vault).SyncSecretsAsync(Service(config), config, cluster, TestContext.Current.CancellationToken);

        changed.ShouldBeFalse("first write needs no restart");
        cluster.Applied.ShouldBeEmpty("a project under Vault needs no Kubernetes Secret");
        vault.Requests.Select(r => $"{r.Method} {r.Path}").ShouldBe(
        [
            "GET /v1/secret/data/deployify/shop/api",
            "POST /v1/secret/data/deployify/shop/api",
            "PUT /v1/sys/policies/acl/shop-api",
            "POST /v1/auth/kubernetes/role/shop-api"
        ]);
        JsonDocument.Parse(vault.Requests[1].Body!).RootElement.GetProperty("data").GetProperty("pg-password").GetString()
            .ShouldBe(SecretManifests.Secret);
        config.Secrets.References["api/pg-password"].Location.ShouldBe("secret/data/deployify/shop/api");
        config.Secrets.References["api/pg-password"].Version.ShouldBe("1");
    }

    [Fact]
    public async Task Vault_WithoutManageAuth_OnlyWritesKv()
    {
        var config = SecretManifests.Config(SecretBackendType.Vault);
        config.Secrets.Vault.ManageAuth = false;
        var vault = new FakeVaultHandler
        {
            Respond = (req, _) => req.Method == HttpMethod.Get
                ? FakeVaultHandler.Json("{}", HttpStatusCode.NotFound)
                : FakeVaultHandler.Json("{\"data\":{\"version\":1}}")
        };

        await Target(vault).SyncSecretsAsync(Service(config), config, new FakeCluster(), TestContext.Current.CancellationToken);

        vault.Requests.ShouldAllBe(r => r.Path.StartsWith("/v1/secret/"));
    }

    [Fact]
    public async Task Vault_ContainerUsesKubernetesSecret()
    {
        var config = SecretManifests.Config(SecretBackendType.Vault);
        var vault = new FakeVaultHandler();
        var cluster = new FakeCluster();

        await Target(vault).SyncSecretsAsync(Service(config, "pg"), config, cluster, TestContext.Current.CancellationToken);

        vault.Requests.ShouldBeEmpty();
        cluster.Secrets.Keys.ShouldBe(["shop-pg/pg-secrets"]);
    }

    [Fact]
    public void Target_DeclaresSupportedBackendsAndStores()
    {
        var target = Target();

        target.SupportedSecretBackends.ShouldBe([SecretBackendType.KubernetesSecret, SecretBackendType.Vault, SecretBackendType.KeyVault]);
        target.CreateSecretStore(SecretManifests.Config(SecretBackendType.KubernetesSecret), NullLogger.Instance)
            .Description.ShouldBe("Kubernetes secret shop-system/deployify-parameters");
        target.CreateSecretStore(SecretManifests.Config(SecretBackendType.Vault), NullLogger.Instance)
            .Description.ShouldBe("Vault secret/data/deployify/shop/_parameters");
    }
}

public sealed class KubernetesSecretStoreTests
{
    [Fact]
    public async Task LoadsNothingWhenSecretMissing()
    {
        var store = new KubernetesSecretStore(new FakeCluster(), "shop-system");

        (await store.LoadAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SavesAllValuesAndReadsThemBack()
    {
        var cluster = new FakeCluster();
        var store = new KubernetesSecretStore(cluster, "shop-system");

        await store.SaveAsync(new Dictionary<string, SensitiveValue>
        {
            ["a.value"] = new("first-value-1"),
            ["b.value"] = new("second-value-2")
        }, ["b.value"], TestContext.Current.CancellationToken);

        cluster.Applied[0].RootElement.GetProperty("kind").GetString().ShouldBe("Namespace");
        cluster.Applied[0].RootElement.GetProperty("metadata").GetProperty("labels").TryGetProperty("app.kubernetes.io/managed-by", out _)
            .ShouldBeFalse("plan would otherwise list the secrets namespace as a service to delete");
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        loaded.Keys.Order().ShouldBe(["a.value", "b.value"]);
        loaded["b.value"].Reveal().ShouldBe("second-value-2");
    }

    [Fact]
    public void Manifest_EncodesAndDecodes()
    {
        var json = KubernetesSecretManifest.Build("ns", "s", new Dictionary<string, SensitiveValue> { ["k"] = new("välue with spaces") });

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("data").GetProperty("k").GetString()
            .ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes("välue with spaces")));
        KubernetesSecretManifest.ReadData(json)["k"].Reveal().ShouldBe("välue with spaces");
    }
}
