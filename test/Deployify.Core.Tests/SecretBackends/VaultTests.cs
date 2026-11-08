using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.SecretBackends;
using Deployify.Core.SecretBackends.Vault;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.SecretBackends;

public sealed class VaultTemplateTests
{
    private static ParameterPart Secret(string name) => new(name, "value", "S3cret-pw", true);

    private static ResolvedValue Value(params object[] parts) => ResolvedValue.Concat(parts.Select(p => p switch
    {
        string s => ResolvedValue.Text(s),
        ValuePart v => ResolvedValue.Of(v),
        _ => throw new ArgumentException()
    }));

    [Fact]
    public void Expression_LooksUpSecretsAndQuotesText()
    {
        var expr = VaultTemplate.Expression(Value("Host=pg;Password=", Secret("pg-password"), ";Opt=a$b"));

        expr.ShouldBe("print \"Host=pg;Password=\" (index .Data.data \"pg-password\") \";Opt=a$b\"");
    }

    [Fact]
    public void Expression_InlinesPlainParametersAndUnresolvedReferences()
    {
        var expr = VaultTemplate.Expression(Value(
            new ParameterPart("user", "value", "postgres", false), ":", Secret("pw"), "@", new UnresolvedPart("{x.y}")));

        expr.ShouldBe("print \"postgres:\" (index .Data.data \"pw\") \"@{x.y}\"");
    }

    [Fact]
    public void Expression_EscapesQuotesAndBackslashesInText()
    {
        var expr = VaultTemplate.Expression(Value("a\"b\\c", Secret("pw")));

        expr.ShouldStartWith("print \"a\\u0022b\\\\c\"");
    }

    [Fact]
    public void JsonConfigFile_RendersConfigurationKeysWithoutSecrets()
    {
        var template = VaultTemplate.JsonConfigFile("secret/data/deployify/shop/api", new Dictionary<string, ResolvedValue>
        {
            ["ConnectionStrings__db"] = Value("Host=pg;Password=", Secret("pg-password")),
            ["Stripe__Key"] = Value(Secret("stripe-key"))
        });

        template.ShouldBe("""
            {{- with secret "secret/data/deployify/shop/api" -}}
            {
              "ConnectionStrings:db": {{ print "Host=pg;Password=" (index .Data.data "pg-password") | toJSON }},
              "Stripe:Key": {{ print (index .Data.data "stripe-key") | toJSON }}
            }
            {{- end }}

            """.ReplaceLineEndings("\n"));
        template.ShouldNotContain("S3cret-pw");
    }

    [Fact]
    public void Policy_GrantsReadOnOnePath()
    {
        var policy = VaultPolicy.ReadOnly("secret/data/deployify/shop/api");

        policy.ShouldContain("path \"secret/data/deployify/shop/api\"");
        policy.ShouldContain("capabilities = [\"read\"]");
        policy.ShouldNotContain("write");
    }
}

/// <summary>Records requests and replies with canned responses.</summary>
internal sealed class FakeVaultHandler : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string? Body, string? Token)> Requests { get; } = [];

    public Func<HttpRequestMessage, string?, HttpResponseMessage> Respond { get; set; }
        = (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        request.Headers.TryGetValues("X-Vault-Token", out var tokens);
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body, tokens?.Single()));
        return Respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage KvRead(int version, params (string Key, string Value)[] data)
        => Json(JsonSerializer.Serialize(new
        {
            data = new
            {
                data = data.ToDictionary(d => d.Key, d => d.Value),
                metadata = new { version }
            }
        }));
}

public sealed class VaultClientTests
{
    private readonly FakeVaultHandler _handler = new();

    private VaultClient Client() => new(new Uri("https://vault.test:8200"), "t0ken", "team-a", _handler);

    [Fact]
    public async Task ReadKv_ReturnsValuesAndVersion()
    {
        _handler.Respond = (_, _) => FakeVaultHandler.KvRead(3, ("pg-password", "S3cret-pw"));
        using var client = Client();

        var entry = await client.ReadKvAsync("secret", "deployify/shop/api", TestContext.Current.CancellationToken);

        entry.Version.ShouldBe(3);
        entry.Data["pg-password"].Reveal().ShouldBe("S3cret-pw");
        _handler.Requests.ShouldHaveSingleItem().Path.ShouldBe("/v1/secret/data/deployify/shop/api");
        _handler.Requests[0].Token.ShouldBe("t0ken");
    }

    [Fact]
    public async Task ReadKv_Missing_ReturnsEmpty()
    {
        _handler.Respond = (_, _) => FakeVaultHandler.Json("{\"errors\":[]}", HttpStatusCode.NotFound);
        using var client = Client();

        var entry = await client.ReadKvAsync("secret", "nope", TestContext.Current.CancellationToken);

        entry.ShouldBe(VaultKvEntry.Missing);
    }

    [Fact]
    public async Task WriteKv_SendsDataAndCheckAndSet()
    {
        _handler.Respond = (_, _) => FakeVaultHandler.Json("{\"data\":{\"version\":4}}");
        using var client = Client();

        var version = await client.WriteKvAsync("secret", "deployify/shop/api",
            new Dictionary<string, SensitiveValue> { ["pg-password"] = new("S3cret-pw") }, cas: 3, TestContext.Current.CancellationToken);

        version.ShouldBe(4);
        var request = _handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("data").GetProperty("pg-password").GetString().ShouldBe("S3cret-pw");
        body.RootElement.GetProperty("options").GetProperty("cas").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task WriteKvIfChanged_SkipsIdenticalData()
    {
        _handler.Respond = (_, _) => FakeVaultHandler.KvRead(5, ("pg-password", "S3cret-pw"));
        using var client = Client();

        var (version, written) = await client.WriteKvIfChangedAsync("secret", "p",
            new Dictionary<string, SensitiveValue> { ["pg-password"] = new("S3cret-pw") }, TestContext.Current.CancellationToken);

        (version, written).ShouldBe((5, false));
        _handler.Requests.ShouldAllBe(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task WriteKvIfChanged_WritesChangedData()
    {
        _handler.Respond = (req, _) => req.Method == HttpMethod.Get
            ? FakeVaultHandler.KvRead(5, ("pg-password", "old-password"))
            : FakeVaultHandler.Json("{\"data\":{\"version\":6}}");
        using var client = Client();

        var (version, written) = await client.WriteKvIfChangedAsync("secret", "p",
            new Dictionary<string, SensitiveValue> { ["pg-password"] = new("S3cret-pw") }, TestContext.Current.CancellationToken);

        (version, written).ShouldBe((6, true));
        JsonDocument.Parse(_handler.Requests[1].Body!).RootElement.GetProperty("options").GetProperty("cas").GetInt32().ShouldBe(5);
    }

    [Fact]
    public async Task Errors_ReportVaultErrorsButNotTheRequest()
    {
        _handler.Respond = (_, _) => FakeVaultHandler.Json("{\"errors\":[\"permission denied\"]}", HttpStatusCode.Forbidden);
        using var client = Client();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => client.WriteKvAsync("secret", "p",
            new Dictionary<string, SensitiveValue> { ["k"] = new("S3cret-pw") }, ct: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("403");
        ex.Message.ShouldContain("permission denied");
        ex.Message.ShouldNotContain("S3cret-pw");
    }

    [Fact]
    public async Task PolicyAndRole_UseTheirEndpoints()
    {
        using var client = Client();

        await client.WritePolicyAsync("shop-api", "path \"x\" {}", TestContext.Current.CancellationToken);
        await client.WriteKubernetesRoleAsync("kubernetes", "shop-api", "api", "shop-api", ["shop-api"], TestContext.Current.CancellationToken);

        _handler.Requests[0].Path.ShouldBe("/v1/sys/policies/acl/shop-api");
        _handler.Requests[0].Method.ShouldBe(HttpMethod.Put);
        _handler.Requests[1].Path.ShouldBe("/v1/auth/kubernetes/role/shop-api");
        using var role = JsonDocument.Parse(_handler.Requests[1].Body!);
        role.RootElement.GetProperty("bound_service_account_names")[0].GetString().ShouldBe("api");
        role.RootElement.GetProperty("bound_service_account_namespaces")[0].GetString().ShouldBe("shop-api");
        role.RootElement.GetProperty("token_policies")[0].GetString().ShouldBe("shop-api");
    }

    [Fact]
    public void FromConfig_RequiresAddressAndToken()
    {
        var ex = Should.Throw<InvalidOperationException>(() => VaultClient.FromConfig(new VaultConfig
        {
            Address = "env:DEPLOYIFY_TEST_UNSET_ADDR", Token = "env:DEPLOYIFY_TEST_UNSET_TOKEN"
        }));
        ex.Message.ShouldContain("address");
    }
}

public sealed class VaultSecretStoreTests
{
    [Fact]
    public async Task SaveUsesVersionFromLoad()
    {
        var handler = new FakeVaultHandler
        {
            Respond = (req, _) => req.Method == HttpMethod.Get
                ? FakeVaultHandler.KvRead(2, ("pg-password.value", "existing-password"))
                : FakeVaultHandler.Json("{\"data\":{\"version\":3}}")
        };
        var store = new VaultSecretStore(
            () => new VaultClient(new Uri("https://vault.test"), "t", handler: handler), "secret", "deployify/shop/_parameters");

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        loaded["pg-password.value"].Reveal().ShouldBe("existing-password");

        await store.SaveAsync(new Dictionary<string, SensitiveValue>
        {
            ["pg-password.value"] = new("existing-password"),
            ["redis-password.value"] = new("new-password-42")
        }, ["redis-password.value"], TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        body.RootElement.GetProperty("options").GetProperty("cas").GetInt32().ShouldBe(2);
        body.RootElement.GetProperty("data").EnumerateObject().Count().ShouldBe(2);
        store.Description.ShouldBe("Vault secret/data/deployify/shop/_parameters");
    }
}
