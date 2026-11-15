using System;
using System.Collections.Generic;
using Deployify.Target.Kubernetes.Manifests;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;
using Deployify.Yaml;

namespace Deployify.Target.Kubernetes.Tests;

public sealed class KubernetesYamlTests
{
    private static ConfigMapManifest ConfigMap(Dictionary<string, Quoted> data, string name = "api-config") => new()
    {
        Metadata = new ObjectMeta { Name = name, Namespace = "shop-api" },
        Data = data
    };

    [Theory]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("off")]
    [InlineData("8080")]
    [InlineData("1e3")]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("")]
    [InlineData("0x1F")]
    [InlineData("1:20")]
    [InlineData(".5")]
    [InlineData("2026-11-15")]
    [InlineData(" padded")]
    public void PlainFields_StayStrings_WhenTheyLookLikeSomethingElse(string name)
    {
        var yaml = YamlWriter.CamelCase.Serialize(ConfigMap(new() { ["K"] = "v" }, name));

        // Read back with YAML 1.2 typing: a plain scalar that resolves to a bool, number or null fails.
        YamlSemantics.Normalize(yaml).ShouldContain($"\"name\": \"{name}\"");
    }

    [Theory]
    [InlineData("100m")]
    [InlineData("128Mi")]
    [InlineData("shop-api")]
    [InlineData("v1.2.3")]
    [InlineData("ClusterIP")]
    public void PlainFields_StayPlain_WhenUnambiguous(string value)
    {
        YamlWriter.IsAmbiguous(value).ShouldBeFalse();
        YamlWriter.CamelCase.Serialize(ConfigMap(new() { ["K"] = "v" }, value)).ShouldContain($"name: {value}\n");
    }

    [Fact]
    public void QuotedValues_AreDoubleQuoted()
    {
        var yaml = YamlWriter.CamelCase.Serialize(ConfigMap(new() { ["FLAG"] = "true", ["PORT"] = "8080", ["TEXT"] = "say \"hi\": #1" }));

        yaml.ShouldContain("FLAG: \"true\"");
        yaml.ShouldContain("PORT: \"8080\"");
        yaml.ShouldContain("TEXT: \"say \\\"hi\\\": #1\"");
    }

    [Fact]
    public void MultiLineValues_AreLiteralBlocks()
    {
        var yaml = YamlWriter.CamelCase.Serialize(ConfigMap(new() { ["T"] = "{\n  \"a\": 1\n}" }));

        yaml.ShouldContain("  T: |-\n    {\n      \"a\": 1\n    }\n");
    }

    [Fact]
    public void LongValues_StayOnOneLine()
    {
        var value = new string('x', 300);
        YamlWriter.CamelCase.Serialize(ConfigMap(new() { ["LONG"] = value })).ShouldContain($"LONG: \"{value}\"\n");
    }

    [Fact]
    public void Nulls_AreLeftOut_AndEmptyStringsKept()
    {
        var yaml = YamlWriter.CamelCase.Serialize(new ObjectMeta
        {
            Name = "api",
            Labels = new() { ["empty"] = "" },
            Annotations = new() { ["empty"] = "" }
        });

        yaml.ShouldBe("name: api\nlabels:\n  empty: \"\"\nannotations:\n  empty: \"\"\n");
    }

    [Fact]
    public void Replacements_SwapTheNodeForRawText()
    {
        var yaml = YamlWriter.CamelCase.SerializeWithReplacements(
            ConfigMap(new() { ["A"] = "1", ["B"] = "2" }),
            [
                ("data.A", _ => "{{ .Values.a }}"),
                ("metadata", column => $"\n{new string(' ', column + 2)}{{{{- include \"meta\" . | nindent {column + 2} }}}}")
            ]);

        yaml.ShouldContain("  A: {{ .Values.a }}\n");
        yaml.ShouldContain("  B: \"2\"\n");
        yaml.ShouldContain("metadata:\n  {{- include \"meta\" . | nindent 2 }}\n");
        yaml.ShouldNotContain("...");
    }

    [Fact]
    public void Replacements_ApplyAfterTheTransform()
    {
        var yaml = YamlWriter.CamelCase.SerializeWithReplacements(
            ConfigMap(new() { ["A"] = "x", ["B"] = "x" }),
            [("data.A", _ => "x-replaced")],
            text => text.Replace("x", "y", StringComparison.Ordinal));

        yaml.ShouldContain("A: x-replaced");
        yaml.ShouldContain("B: \"y\"");
    }

    [Theory]
    [InlineData("data.MISSING")]
    [InlineData("spec.replicas")]
    [InlineData("data[0]")]
    public void Replacements_FailOnAPathThatIsNotThere(string path)
        => Should.Throw<InvalidOperationException>(() =>
            YamlWriter.CamelCase.SerializeWithReplacements(ConfigMap(new() { ["A"] = "1" }), [(path, _ => "x")]));

    [Fact]
    public void ReadMetadata_ReadsQuotedAndPlainValues()
    {
        var meta = KubernetesYaml.ReadMetadata("""
            apiVersion: "apps/v1"
            kind: Deployment
            metadata:
              labels:
                name: not-this-one
              name: "yes"
              namespace: shop-api
            """);

        meta.ShouldBe(("Deployment", "apps/v1", "yes", "shop-api"));
    }

    [Fact]
    public void ReadMetadata_HasNoNamespace_ForAClusterScopedObject()
        => KubernetesYaml.ReadMetadata("apiVersion: v1\nkind: Namespace\nmetadata:\n  name: shop-api\n").Namespace.ShouldBeNull();
}
