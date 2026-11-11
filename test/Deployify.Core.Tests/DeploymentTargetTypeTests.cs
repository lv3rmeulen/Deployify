using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

public sealed class DeploymentTargetTypeTests
{
    [Theory]
    [InlineData("nomad")]
    [InlineData("Nomad")]
    [InlineData("NOMAD")]
    [InlineData(" nomad ")]
    public void Parse_MapsBuiltInNamesToCanonicalSpelling(string text)
    {
        var target = DeploymentTargetType.Parse(text);

        target.ShouldBe(DeploymentTargetType.Nomad);
        target.Value.ShouldBe("Nomad");
        target.IsBuiltIn.ShouldBeTrue();
    }

    [Theory]
    [InlineData("example")]
    [InlineData("sample-target")]
    [InlineData("acme.cloud_run")]
    public void Parse_AcceptsPluginIds(string text)
    {
        var target = DeploymentTargetType.Parse(text);

        target.Value.ShouldBe(text);
        target.IsBuiltIn.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("1fly")]
    [InlineData("example io")]
    [InlineData("../evil")]
    public void TryParse_RejectsInvalidIds(string text)
        => DeploymentTargetType.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void Equality_IgnoresCase()
    {
        DeploymentTargetType.Parse("Example").ShouldBe(DeploymentTargetType.Parse("example"));
        (DeploymentTargetType.Parse("Example") == DeploymentTargetType.Parse("EXAMPLE")).ShouldBeTrue();
        DeploymentTargetType.Parse("Example").GetHashCode().ShouldBe(DeploymentTargetType.Parse("example").GetHashCode());
    }

    [Fact]
    public void Default_IsNomad()
    {
        default(DeploymentTargetType).ShouldBe(DeploymentTargetType.Nomad);
        default(DeploymentTargetType).Value.ShouldBe("Nomad");
    }

    [Fact]
    public void Json_WritesTheSameStringsAsTheOldEnum()
    {
        // State files written before plugin targets used JsonStringEnumConverter on an enum.
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var config = new EnvironmentConfig { Target = DeploymentTargetType.AzureContainerApps };

        JsonSerializer.Serialize(config, options).ShouldContain("\"Target\":\"AzureContainerApps\"");
    }

    [Fact]
    public void Json_ReadsBuiltInAndPluginTargets()
    {
        var read = JsonSerializer.Deserialize<Dictionary<string, DeploymentTargetType>>(
            """{ "a": "compose", "b": "example" }""")!;

        read["a"].ShouldBe(DeploymentTargetType.Compose);
        read["a"].Value.ShouldBe("Compose");
        read["b"].Value.ShouldBe("example");
    }

    [Fact]
    public void Json_RejectsAnInvalidId()
        => Should.Throw<JsonException>(
            () => JsonSerializer.Deserialize<DeploymentTargetType>("\"not a target\""));

    [Fact]
    public void Json_WorksAsDictionaryKey()
    {
        var json = JsonSerializer.Serialize(new Dictionary<DeploymentTargetType, int> { [DeploymentTargetType.Helm] = 1 });
        json.ShouldBe("""{"Helm":1}""");
        JsonSerializer.Deserialize<Dictionary<DeploymentTargetType, int>>(json)!
            .ShouldContainKey(DeploymentTargetType.Helm);
    }

    [Fact]
    public void Parse_ThrowsFormatException_ForInvalidId()
        => Should.Throw<FormatException>(() => DeploymentTargetType.Parse("no spaces"));
}
