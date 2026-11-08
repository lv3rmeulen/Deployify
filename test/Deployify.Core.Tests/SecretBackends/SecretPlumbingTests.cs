using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.Services;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.SecretBackends;

public sealed class SensitiveValueTests
{
    [Fact]
    public void ToString_IsMasked()
    {
        var value = new SensitiveValue("hunter2-pw");

        value.ToString().ShouldBe(SensitiveValue.Mask);
        $"{value}".ShouldNotContain("hunter2");
        value.Reveal().ShouldBe("hunter2-pw");
    }

    [Fact]
    public void Serializing_Throws()
    {
        Should.Throw<SecretLeakException>(() => JsonSerializer.Serialize(new SensitiveValue("hunter2-pw")));
    }

    [Fact]
    public void Serializing_ObjectThatHoldsOne_Throws()
    {
        var holder = new Dictionary<string, object> { ["password"] = new SensitiveValue("hunter2-pw") };

        Should.Throw<SecretLeakException>(() => JsonSerializer.Serialize(holder));
    }

    [Fact]
    public void Deserializing_Throws()
    {
        Should.Throw<SecretLeakException>(() => JsonSerializer.Deserialize<SensitiveValue>("\"hunter2-pw\""));
    }

    [Fact]
    public void Equality_ComparesValues()
    {
        new SensitiveValue("a-secret").Equals(new SensitiveValue("a-secret")).ShouldBeTrue();
        new SensitiveValue("a-secret").Equals(new SensitiveValue("b-secret")).ShouldBeFalse();
        new SensitiveValue("a-secret").Equals(null).ShouldBeFalse();
    }
}

public sealed class SecretKeysTests
{
    private static ParameterPart Param(string name, string input = "value", string value = "v4lue", bool secret = true)
        => new(name, input, value, secret);

    [Theory]
    [InlineData("pg-password", "value", "pg-password")]
    [InlineData("api", "region", "api.region")]
    [InlineData("weird name", "value", "weird_name")]
    public void Id_FollowsParameterAndInput(string parameter, string input, string expected)
        => SecretKeys.Id(Param(parameter, input)).ShouldBe(expected);

    [Fact]
    public void SecretParameters_AreDistinctAndSkipPlainParameters()
    {
        var values = new[]
        {
            ResolvedValue.Concat([ResolvedValue.Text("a="), ResolvedValue.Of(Param("pw")), ResolvedValue.Of(Param("user", secret: false))]),
            ResolvedValue.Of(Param("pw")),
            ResolvedValue.Of(Param("key"))
        };

        SecretKeys.SecretParameters(values).Select(SecretKeys.Id).ShouldBe(["pw", "key"]);
    }

    [Fact]
    public void IsSingleSecret_OnlyForABareSecret()
    {
        SecretKeys.IsSingleSecret(ResolvedValue.Of(Param("pw")), out var p).ShouldBeTrue();
        p.Parameter.ShouldBe("pw");

        SecretKeys.IsSingleSecret(ResolvedValue.Concat([ResolvedValue.Text("x"), ResolvedValue.Of(Param("pw"))]), out _).ShouldBeFalse();
        SecretKeys.IsSingleSecret(ResolvedValue.Of(Param("pw", secret: false)), out _).ShouldBeFalse();
    }
}

public sealed class SecretLeakScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deployify-leak-" + Path.GetRandomFileName());

    public SecretLeakScannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Dictionary<string, SensitiveValue> Secrets(params (string Id, string Value)[] s)
        => s.ToDictionary(x => x.Id, x => new SensitiveValue(x.Value));

    [Fact]
    public void ScanText_ReportsIdNotValue()
    {
        var leaks = SecretLeakScanner.ScanText("f.yaml", "password: S3cret-pw", Secrets(("pg-password", "S3cret-pw")));

        leaks.ShouldHaveSingleItem().ShouldBe(new SecretLeak("f.yaml", "pg-password"));
    }

    [Fact]
    public void ScanText_IgnoresValuesTooShortToMatchReliably()
        => SecretLeakScanner.ScanText("f", "abc", Secrets(("tiny", "ab"))).ShouldBeEmpty();

    [Fact]
    public void ScanDirectory_LooksInSubdirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "api"));
        File.WriteAllText(Path.Combine(_dir, "api", "deployment.yaml"), "value: S3cret-pw");
        File.WriteAllText(Path.Combine(_dir, "clean.yaml"), "value: $(Parameters__pw)");

        var leaks = SecretLeakScanner.ScanDirectory(_dir, Secrets(("pw", "S3cret-pw")));

        leaks.ShouldHaveSingleItem().Location.ShouldEndWith("deployment.yaml");
    }

    [Fact]
    public void EnsureClean_DeletesLeakingFilesAndThrowsWithoutTheValue()
    {
        var leaking = Path.Combine(_dir, "leak.yaml");
        var clean = Path.Combine(_dir, "clean.yaml");
        File.WriteAllText(leaking, "value: S3cret-pw");
        File.WriteAllText(clean, "value: ok");

        var ex = Should.Throw<SecretLeakException>(
            () => SecretLeakScanner.EnsureClean([leaking, clean], Secrets(("pw", "S3cret-pw"))));

        ex.Message.ShouldContain("pw");
        ex.Message.ShouldNotContain("S3cret-pw");
        File.Exists(leaking).ShouldBeFalse();
        File.Exists(clean).ShouldBeTrue();
    }
}

public sealed class SecretRegistryTests
{
    private static readonly ParameterInput SecretInput = new() { Name = "value", Secret = true };
    private static readonly ParameterInput PlainInput = new() { Name = "value", Secret = false };

    [Fact]
    public void RecordingSource_RecordsOnlySecretInputs()
    {
        var registry = new SecretRegistry();
        var source = new RecordingParameterSource(
            new DictionaryParameterSource(new Dictionary<string, string> { ["pw"] = "S3cret-pw", ["user"] = "postgres" }),
            registry);

        source.GetValue("pw", SecretInput).ShouldBe("S3cret-pw");
        source.GetValue("user", PlainInput).ShouldBe("postgres");

        registry.Snapshot().Keys.ShouldBe(["pw"]);
    }

    [Fact]
    public void RecordingSource_NamesNonDefaultInputs()
    {
        var registry = new SecretRegistry();
        var source = new RecordingParameterSource(
            new DictionaryParameterSource(new Dictionary<string, string> { ["api.key"] = "k3y-value" }), registry);

        source.GetValue("api", new ParameterInput { Name = "key", Secret = true });

        registry.Snapshot().Keys.ShouldBe(["api.key"]);
    }
}

public sealed class StateManagerSecretTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deployify-state-" + Path.GetRandomFileName());

    public StateManagerSecretTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static DeployifyStateFile State(Action<EnvironmentConfig>? configure = null)
    {
        var env = new EnvironmentConfig();
        configure?.Invoke(env);
        return new DeployifyStateFile { Environments = { ["prod"] = env } };
    }

    [Fact]
    public async Task Save_RefusesWhenAResolvedSecretWouldBeWritten()
    {
        var registry = new SecretRegistry();
        registry.Record("pg-password", "S3cret-pw");
        var sut = new StateManager(registry);

        var ex = await Should.ThrowAsync<SecretLeakException>(() => sut.SaveFileAsync(
            State(e => e.Parameters["pg-password"] = "S3cret-pw"), _dir, TestContext.Current.CancellationToken));

        ex.Message.ShouldNotContain("S3cret-pw");
        File.Exists(Path.Combine(_dir, "deployify-state.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task Save_AllowsReferences()
    {
        var registry = new SecretRegistry();
        registry.Record("pg-password", "S3cret-pw");
        var sut = new StateManager(registry);

        await sut.SaveFileAsync(State(e => e.Parameters["pg-password"] = "env:PG_PASSWORD"), _dir, TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(_dir, "deployify-state.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task SecretsSection_RoundTrips()
    {
        var sut = new StateManager();
        await sut.SaveFileAsync(State(e =>
        {
            e.Secrets.Backend = SecretBackendType.Vault;
            e.Secrets.Vault.Address = "https://vault:8200";
            e.Secrets.References["api/pg-password"] = new SecretReference
            {
                Backend = SecretBackendType.Vault, Location = "secret/data/deployify/shop/api", Key = "pg-password", Version = "7"
            };
        }), _dir, TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(Path.Combine(_dir, "deployify-state.json"), TestContext.Current.CancellationToken);
        json.ShouldContain("\"Backend\": \"Vault\"");

        var loaded = (await sut.LoadFileAsync(_dir, TestContext.Current.CancellationToken)).Environments["prod"].Secrets;
        loaded.Backend.ShouldBe(SecretBackendType.Vault);
        loaded.IsExternal.ShouldBeTrue();
        loaded.References["api/pg-password"].Version.ShouldBe("7");
    }

    [Fact]
    public async Task OldStateFile_DefaultsToInline()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "deployify-state.json"),
            """{ "ActiveEnvironment": "prod", "Environments": { "prod": { "Target": "Kubernetes" } } }""",
            TestContext.Current.CancellationToken);

        var loaded = await new StateManager().LoadFileAsync(_dir, TestContext.Current.CancellationToken);

        loaded.Environments["prod"].Secrets.Backend.ShouldBe(SecretBackendType.Inline);
        loaded.Environments["prod"].Secrets.IsExternal.ShouldBeFalse();
    }
}

/// <summary>An in-memory <see cref="ISecretStore"/> for tests.</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Exception? LoadFailure { get; set; }

    public List<IReadOnlyCollection<string>> Saves { get; } = [];

    public string Description => "fake store";

    public Task<IReadOnlyDictionary<string, SensitiveValue>> LoadAsync(CancellationToken ct = default)
        => LoadFailure is not null
            ? Task.FromException<IReadOnlyDictionary<string, SensitiveValue>>(LoadFailure)
            : Task.FromResult<IReadOnlyDictionary<string, SensitiveValue>>(
                Values.ToDictionary(kv => kv.Key, kv => new SensitiveValue(kv.Value)));

    public Task SaveAsync(IReadOnlyDictionary<string, SensitiveValue> all, IReadOnlyCollection<string> changed, CancellationToken ct = default)
    {
        foreach (var (k, v) in all)
        {
            Values[k] = v.Reveal();
        }

        Saves.Add(changed);
        return Task.CompletedTask;
    }
}

public sealed class GeneratedSecretSessionTests
{
    private static readonly ParameterInput Generated = new()
    {
        Name = "value", Secret = true, Generate = new GenerateOptions { MinLength = 20, Special = false }
    };

    private static async Task<(GeneratedSecretSession Session, IParameterSource Source)> OpenAsync(
        FakeSecretStore store, IReadOnlyDictionary<string, string>? legacy = null)
    {
        var session = await GeneratedSecretSession.OpenAsync(store, legacy, ct: TestContext.Current.CancellationToken);
        var source = ParameterSources.Default(generatedStore: session.Values, getVariable: _ => null, onGenerated: session.OnGenerated);
        return (session, source);
    }

    [Fact]
    public async Task ReusesValueFromStore()
    {
        var store = new FakeSecretStore { Values = { ["pg-password.value"] = "existing-password-1" } };
        var (session, source) = await OpenAsync(store);

        source.GetValue("pg-password", Generated).ShouldBe("existing-password-1");
        session.Pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task NewValue_IsPendingUntilFlushed()
    {
        var store = new FakeSecretStore();
        var (session, source) = await OpenAsync(store);

        var value = source.GetValue("pg-password", Generated)!;
        value.Length.ShouldBe(20);
        session.Pending.ShouldBe(["pg-password.value"]);
        store.Values.ShouldBeEmpty();

        await session.FlushAsync(TestContext.Current.CancellationToken);

        store.Values["pg-password.value"].ShouldBe(value);
        store.Saves.ShouldHaveSingleItem().ShouldBe(["pg-password.value"]);
        session.Pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task Flush_WithNothingPending_DoesNotWrite()
    {
        var store = new FakeSecretStore { Values = { ["pg-password.value"] = "existing-password-1" } };
        var (session, _) = await OpenAsync(store);

        await session.FlushAsync(TestContext.Current.CancellationToken);

        store.Saves.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnreadableStore_RefusesToFlush()
    {
        // A value generated now could replace a password a running database already uses.
        var store = new FakeSecretStore { LoadFailure = new InvalidOperationException("cluster unreachable") };
        var (session, source) = await OpenAsync(store);
        source.GetValue("pg-password", Generated);

        session.LoadError.ShouldNotBeNull();
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => session.FlushAsync(TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("cluster unreachable");
        store.Saves.ShouldBeEmpty();
    }

    [Fact]
    public async Task LegacyValues_AreMigratedButStoreWins()
    {
        var store = new FakeSecretStore { Values = { ["a.value"] = "from-store-123" } };
        var legacy = new Dictionary<string, string> { ["a.value"] = "from-file-123", ["b.value"] = "from-file-456" };
        var (session, source) = await OpenAsync(store, legacy);

        source.GetValue("a", Generated).ShouldBe("from-store-123");
        source.GetValue("b", Generated).ShouldBe("from-file-456");
        session.Pending.ShouldBe(["b.value"]);

        await session.FlushAsync(TestContext.Current.CancellationToken);
        store.Values["b.value"].ShouldBe("from-file-456");
    }
}
