using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Expressions;

/// <summary>How parameter values reach the generators: config, environment, store file.</summary>
public sealed class ParameterWiringTests
{
    // ── ResolvedEnvironment ───────────────────────────────────────────────────

    private static ResolvedValue SecretValue(string text, string value)
        => ResolvedValue.Concat([ResolvedValue.Text(text), ResolvedValue.Of(new ParameterPart("pg-password", "value", value, IsSecret: true))]);

    [Fact]
    public void ResolvedEnvironment_SortsBySensitivity()
    {
        var env = new ResolvedEnvironment();

        env.Set("PLAIN", ResolvedValue.Text("x"));
        env.Set("DB", SecretValue("pw=", "s3cret"));

        env.Plain.ShouldBe(new Dictionary<string, string> { ["PLAIN"] = "x" });
        env.Secrets.Keys.ShouldBe(new[] { "DB" });
    }

    [Fact]
    public void ResolvedEnvironment_NonSecretParameter_IsPlain()
    {
        var env = new ResolvedEnvironment();

        env.Set("GREETING", ResolvedValue.Of(new ParameterPart("greeting", "value", "hi", IsSecret: false)));

        env.Plain["GREETING"].ShouldBe("hi");
        env.Secrets.ShouldBeEmpty();
    }

    [Fact]
    public void ResolvedEnvironment_LaterValueMovesKeyBetweenSets()
    {
        var env = new ResolvedEnvironment();

        env.Set("DB", SecretValue("pw=", "s3cret"));
        env.SetPlain("DB", "override");

        env.Plain["DB"].ShouldBe("override");
        env.Secrets.ShouldBeEmpty();

        env.Set("DB", SecretValue("pw=", "s3cret"));
        env.Plain.ShouldBeEmpty();
        env.Secrets.Keys.ShouldBe(new[] { "DB" });
    }

    [Fact]
    public void ResolvedEnvironment_ForDisplay_MasksSecrets()
    {
        var env = new ResolvedEnvironment();
        env.Set("PLAIN", ResolvedValue.Text("x"));
        env.Set("DB", SecretValue("pw=", "s3cret"));

        var display = env.ForDisplay();

        display["PLAIN"].ShouldBe("x");
        display["DB"].ShouldBe("pw=****");
    }

    [Theory]
    [InlineData("pg-password", "value", "Parameters__pg_password")]
    [InlineData("api.key", "value", "Parameters__api_key")]
    [InlineData("p", "region", "Parameters__p__region")]
    public void VariableName_FollowsAspireConvention(string parameter, string input, string expected)
    {
        ResolvedEnvironment.VariableName(new ParameterPart(parameter, input, "v", true)).ShouldBe(expected);
    }

    [Fact]
    public void Render_TextEscaper_OnlyTouchesLiteralText()
    {
        var value = ResolvedValue.Concat([ResolvedValue.Text("a$b;"), ResolvedValue.Of(new ParameterPart("p", "value", "x$y", true))]);

        value.Render(p => "${P}", text: t => t.Replace("$", "$$")).ShouldBe("a$$b;${P}");
        value.Render(text: t => t.ToUpperInvariant()).ShouldBe("A$B;x$y");
    }

    // ── PlaceholderResolver.ResolveEnvironment ────────────────────────────────

    [Fact]
    public async Task ResolveEnvironment_SplitsAndAppliesOverrides()
    {
        var manifest = ExpressionFixtures.AppManifest();
        var resolver = new PlaceholderResolver(manifest, ServicePorts.Assign(manifest), new EndpointStrategy(),
            ExpressionFixtures.FixedParameters(("pg-password", "pw"), ("api-key", "k")));

        var env = await resolver.ResolveEnvironmentAsync(manifest.Resources["api"].Env, new Dictionary<string, string> { ["API_KEY"] = "from-override" }, TestContext.Current.CancellationToken);

        env.Secrets.Keys.ShouldBe(new[] { "ConnectionStrings__appdb" });
        env.Plain["GREETING"].ShouldBe("hello");
        env.Plain["API_KEY"].ShouldBe("from-override");
        env.Plain["JSON"].ShouldBe("{\"retries\":3}");
    }

    // ── ParameterResolution ───────────────────────────────────────────────────

    [Fact]
    public void ExplicitValues_ResolveEnvReferencesAndSkipMissing()
    {
        var variable = $"DEPLOYIFY_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, "from-env");
        try
        {
            var config = new EnvironmentConfig
            {
                Parameters =
                {
                    ["literal"] = "value",
                    ["indirect"] = $"env:{variable}",
                    ["missing"] = "env:DEPLOYIFY_DOES_NOT_EXIST_123",
                    ["empty"] = ""
                }
            };

            var values = ParameterResolution.ExplicitValues(config);

            values.Count.ShouldBe(2);
            values["literal"].ShouldBe("value");
            values["indirect"].ShouldBe("from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void For_CreatesOneSourcePerConfig()
    {
        var config = new EnvironmentConfig();

        var first = ParameterResolution.For(config);

        ParameterResolution.For(config).ShouldBeSameAs(first);
        config.ParameterSource.ShouldBeSameAs(first);
    }

    [Fact]
    public void For_UsesAttachedSource()
    {
        var attached = new DictionaryParameterSource(new Dictionary<string, string>());
        var config = new EnvironmentConfig { ParameterSource = attached };

        ParameterResolution.For(config).ShouldBeSameAs(attached);
    }

    [Fact]
    public async Task For_ExplicitValueIsUsed()
    {
        var config = new EnvironmentConfig { Parameters = { ["pg-password"] = "from-config" } };

        (await ParameterResolution.For(config).GetValueAsync("pg-password", new ParameterInput(), TestContext.Current.CancellationToken)).ShouldBe("from-config");
    }

    [Fact]
    public void ParameterSource_IsNotSerialized()
    {
        var config = new EnvironmentConfig { ParameterSource = new GeneratedParameterSource(), Parameters = { ["a"] = "b" } };

        var json = System.Text.Json.JsonSerializer.Serialize(config);

        json.ShouldNotContain("ParameterSource");
        json.ShouldContain("\"Parameters\":{\"a\":\"b\"}");
    }

    // ── ParameterStore ────────────────────────────────────────────────────────

    private static readonly ParameterInput s_generated = new() { Secret = true, Generate = new GenerateOptions { MinLength = 20 } };

    [Fact]
    public async Task Store_SavesGeneratedValueImmediately()
    {
        using var dir = new TempDir();
        var store = ParameterStore.Load(dir.Path, "prod");

        var value = await store.CreateSource(new EnvironmentConfig()).GetValueAsync("pg-password", s_generated, TestContext.Current.CancellationToken);

        value.ShouldNotBeNull().Length.ShouldBe(20);
        var json = File.ReadAllText(Path.Combine(dir.Path, ParameterStore.FileName));
        json.ShouldContain("\"prod\"");
        json.ShouldContain($"\"pg-password.value\": \"{value}\"");
    }

    [Fact]
    public async Task Store_ReusesValueOnNextRun()
    {
        using var dir = new TempDir();
        var first = await ParameterStore.Load(dir.Path, "prod").CreateSource(new EnvironmentConfig()).GetValueAsync("pg-password", s_generated, TestContext.Current.CancellationToken);

        var second = await ParameterStore.Load(dir.Path, "prod").CreateSource(new EnvironmentConfig()).GetValueAsync("pg-password", s_generated, TestContext.Current.CancellationToken);

        second.ShouldBe(first);
    }

    [Fact]
    public async Task Store_KeepsEnvironmentsApart()
    {
        using var dir = new TempDir();
        var prod = await ParameterStore.Load(dir.Path, "prod").CreateSource(new EnvironmentConfig()).GetValueAsync("pg-password", s_generated, TestContext.Current.CancellationToken);

        var dev = await ParameterStore.Load(dir.Path, "dev").CreateSource(new EnvironmentConfig()).GetValueAsync("pg-password", s_generated, TestContext.Current.CancellationToken);

        dev.ShouldNotBe(prod);
        ParameterStore.Load(dir.Path, "prod").Values["pg-password.value"].ShouldBe(prod);
        ParameterStore.Load(dir.Path, "dev").Values["pg-password.value"].ShouldBe(dev);
    }

    [Fact]
    public async Task Store_ExplicitValueWinsAndIsNotStored()
    {
        using var dir = new TempDir();
        var config = new EnvironmentConfig { Parameters = { ["pg-password"] = "explicit" } };

        (await ParameterStore.Load(dir.Path, "prod").CreateSource(config).GetValueAsync("pg-password", s_generated, TestContext.Current.CancellationToken)).ShouldBe("explicit");

        File.Exists(Path.Combine(dir.Path, ParameterStore.FileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task Store_FileIsOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        await ParameterStore.Load(dir.Path, "prod").CreateSource(new EnvironmentConfig()).GetValueAsync("pg-password", s_generated, TestContext.Current.CancellationToken);

        File.GetUnixFileMode(Path.Combine(dir.Path, ParameterStore.FileName))
            .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // ── StateFileSecrets ──────────────────────────────────────────────────────

    [Fact]
    public void StateFileSecrets_FlagsLiteralSecretParameters()
    {
        var state = new DeployifyStateFile
        {
            Environments =
            {
                ["prod"] = new EnvironmentConfig
                {
                    Parameters =
                    {
                        ["pg-password"] = "literal",
                        ["api-key"] = "literal",
                        ["db-password"] = "env:DB_PASSWORD",
                        ["greeting"] = "hello"
                    }
                }
            }
        };

        StateFileSecrets.FindLiteral(state).ShouldBe(
            new[] { "prod.parameters.pg-password", "prod.parameters.api-key" }, ignoreOrder: true);
    }
}
