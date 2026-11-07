using System;
using System.Collections.Generic;
using System.Linq;
using Deployify.Core.Expressions;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Expressions;

public sealed class ParameterSourceTests
{
    private static readonly ParameterInput s_value = new() { Name = "value" };

    [Fact]
    public void ReadAll_ParsesManifestInputs()
    {
        var manifest = ExpressionFixtures.AppManifest();

        var pg = ParameterInput.ReadAll(manifest.Resources["pg-password"]).ShouldHaveSingleItem().Value;
        pg.Name.ShouldBe("value");
        pg.Type.ShouldBe("string");
        pg.Secret.ShouldBeTrue();
        pg.DefaultValue.ShouldBeNull();
        var generate = pg.Generate.ShouldNotBeNull();
        generate.MinLength.ShouldBe(22);
        generate.Special.ShouldBeFalse();
        generate.Lower.ShouldBeTrue();

        var greeting = ParameterInput.ReadAll(manifest.Resources["greeting"])["value"];
        greeting.Secret.ShouldBeFalse();
        greeting.DefaultValue.ShouldBe("hello");
        greeting.Generate.ShouldBeNull();
    }

    [Fact]
    public void ReadAll_ResourceWithoutInputs_IsEmpty()
    {
        ParameterInput.ReadAll(ExpressionFixtures.AppManifest().Resources["api"]).ShouldBeEmpty();
    }

    [Fact]
    public void Dictionary_MatchesByNameCaseInsensitively()
    {
        var source = new DictionaryParameterSource(new Dictionary<string, string> { ["PG-Password"] = "pw" });

        source.GetValue("pg-password", s_value).ShouldBe("pw");
        source.GetValue("other", s_value).ShouldBeNull();
    }

    [Fact]
    public void Dictionary_NamedInput_UsesDottedKey()
    {
        var source = new DictionaryParameterSource(new Dictionary<string, string>
        {
            ["p"] = "value-only",
            ["p.region"] = "westeurope"
        });

        source.GetValue("p", new ParameterInput { Name = "region" }).ShouldBe("westeurope");
        source.GetValue("p", new ParameterInput { Name = "zone" }).ShouldBeNull();
    }

    [Fact]
    public void Environment_UsesAspireConvention()
    {
        var env = new Dictionary<string, string> { ["Parameters__greeting"] = "hi" };
        var source = new EnvironmentParameterSource(n => env.GetValueOrDefault(n));

        source.GetValue("greeting", s_value).ShouldBe("hi");
    }

    [Fact]
    public void Environment_AcceptsUnderscoreForDash()
    {
        var env = new Dictionary<string, string> { ["Parameters__pg_password"] = "pw" };
        var source = new EnvironmentParameterSource(n => env.GetValueOrDefault(n));

        source.GetValue("pg-password", s_value).ShouldBe("pw");
    }

    [Fact]
    public void Environment_IgnoresEmptyValues()
    {
        var source = new EnvironmentParameterSource(_ => "");

        source.GetValue("greeting", s_value).ShouldBeNull();
    }

    [Fact]
    public void Environment_VariableNames()
    {
        EnvironmentParameterSource.VariableNames("pg-password")
            .ShouldBe(new[] { "Parameters__pg-password", "Parameters__pg_password" });
        EnvironmentParameterSource.VariableNames("p", "region").ShouldBe(new[] { "Parameters__p__region" });
    }

    [Fact]
    public void Generated_IsStoredAndReused()
    {
        var store = new Dictionary<string, string>();
        var input = new ParameterInput { Name = "value", Generate = new GenerateOptions { MinLength = 16 } };

        var first = new GeneratedParameterSource(store).GetValue("pg-password", input);
        var second = new GeneratedParameterSource(store).GetValue("pg-password", input);

        first.ShouldNotBeNull().Length.ShouldBe(16);
        second.ShouldBe(first);
        store["pg-password.value"].ShouldBe(first);
    }

    [Fact]
    public void Generated_UsesExistingStoreValue()
    {
        var store = new Dictionary<string, string> { ["pg-password.value"] = "from-state" };
        var input = new ParameterInput { Generate = new GenerateOptions() };

        new GeneratedParameterSource(store).GetValue("pg-password", input).ShouldBe("from-state");
    }

    [Fact]
    public void Generated_SkipsInputsWithoutGenerate()
    {
        new GeneratedParameterSource().GetValue("p", s_value).ShouldBeNull();
    }

    [Fact]
    public void Default_ExplicitBeatsEnvironmentBeatsDefaultBeatsGenerated()
    {
        var input = new ParameterInput { DefaultValue = "default", Generate = new GenerateOptions() };
        var env = new Dictionary<string, string> { ["Parameters__p"] = "env" };

        ParameterSources.Default(new Dictionary<string, string> { ["p"] = "explicit" }, getVariable: n => env.GetValueOrDefault(n))
            .GetValue("p", input).ShouldBe("explicit");
        ParameterSources.Default(getVariable: n => env.GetValueOrDefault(n))
            .GetValue("p", input).ShouldBe("env");
        ParameterSources.Default(getVariable: _ => null)
            .GetValue("p", input).ShouldBe("default");
        ParameterSources.Default(getVariable: _ => null)
            .GetValue("p", new ParameterInput { Generate = new GenerateOptions() }).ShouldNotBeNull().Length.ShouldBe(22);
    }

    [Fact]
    public void Default_WritesGeneratedValuesToGivenStore()
    {
        var store = new Dictionary<string, string>();
        var source = ParameterSources.Default(generatedStore: store, getVariable: _ => null);

        var value = source.GetValue("p", new ParameterInput { Generate = new GenerateOptions() });

        store.ShouldHaveSingleItem().ShouldBe(new KeyValuePair<string, string>("p.value", value!));
    }

    [Fact]
    public void Composite_ReturnsFirstNonNull()
    {
        var source = new CompositeParameterSource(
            new DictionaryParameterSource(new Dictionary<string, string>()),
            new DictionaryParameterSource(new Dictionary<string, string> { ["p"] = "second" }),
            new DictionaryParameterSource(new Dictionary<string, string> { ["p"] = "third" }));

        source.GetValue("p", s_value).ShouldBe("second");
    }

    // ── PasswordGenerator ─────────────────────────────────────────────────────

    [Fact]
    public void Generate_DefaultsUseAllClassesAndMinLength()
    {
        var value = PasswordGenerator.Generate(new GenerateOptions());

        value.Length.ShouldBe(22);
        value.ShouldAllBe(c => PasswordGenerator.LowerChars.Contains(c) || PasswordGenerator.UpperChars.Contains(c) ||
                               PasswordGenerator.NumericChars.Contains(c) || PasswordGenerator.SpecialChars.Contains(c));
    }

    [Fact]
    public void Generate_RespectsDisabledClasses()
    {
        for (var i = 0; i < 20; i++)
        {
            var value = PasswordGenerator.Generate(new GenerateOptions { Upper = false, Special = false, MinLength = 40 });

            value.ShouldAllBe(c => PasswordGenerator.LowerChars.Contains(c) || PasswordGenerator.NumericChars.Contains(c));
        }
    }

    [Fact]
    public void Generate_MeetsMinimumPerClass()
    {
        for (var i = 0; i < 20; i++)
        {
            var value = PasswordGenerator.Generate(new GenerateOptions
            {
                MinLength = 8,
                MinLower = 2,
                MinUpper = 2,
                MinNumeric = 2,
                MinSpecial = 2
            });

            value.Count(c => PasswordGenerator.LowerChars.Contains(c)).ShouldBeGreaterThanOrEqualTo(2);
            value.Count(c => PasswordGenerator.UpperChars.Contains(c)).ShouldBeGreaterThanOrEqualTo(2);
            value.Count(c => PasswordGenerator.NumericChars.Contains(c)).ShouldBeGreaterThanOrEqualTo(2);
            value.Count(c => PasswordGenerator.SpecialChars.Contains(c)).ShouldBeGreaterThanOrEqualTo(2);
        }
    }

    [Fact]
    public void Generate_LengthGrowsToFitMinimums()
    {
        PasswordGenerator.Generate(new GenerateOptions { MinLength = 2, MinLower = 3, MinNumeric = 3 }).Length.ShouldBe(6);
    }

    [Fact]
    public void Generate_ValuesDiffer()
    {
        var values = Enumerable.Range(0, 10).Select(_ => PasswordGenerator.Generate(new GenerateOptions())).ToHashSet();

        values.Count.ShouldBe(10);
    }

    [Fact]
    public void Generate_SpecialCharsAreSafeInConnectionStringsAndTemplates()
    {
        PasswordGenerator.SpecialChars.ShouldNotContain(";");
        PasswordGenerator.SpecialChars.ShouldNotContain("=");
        PasswordGenerator.SpecialChars.ShouldNotContain("{");
        PasswordGenerator.SpecialChars.ShouldNotContain("$");
        PasswordGenerator.SpecialChars.ShouldNotContain("'");
    }

    [Fact]
    public void Generate_NoClassesEnabled_Throws()
    {
        Should.Throw<ArgumentException>(() => PasswordGenerator.Generate(
            new GenerateOptions { Lower = false, Upper = false, Numeric = false, Special = false }));
    }

    [Fact]
    public void Generate_MinimumForDisabledClass_Throws()
    {
        Should.Throw<ArgumentException>(() => PasswordGenerator.Generate(
            new GenerateOptions { Special = false, MinSpecial = 1 })).Message.ShouldContain("special");
    }
}
