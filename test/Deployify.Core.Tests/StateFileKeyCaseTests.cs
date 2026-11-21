using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Deployify.Core.State;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

/// <summary>
/// Keys in deployify-state.json are case-insensitive, as the stability policy promises: the
/// docs write them in camelCase, Deployify writes them in PascalCase, and both must load.
/// </summary>
public sealed class StateFileKeyCaseTests
{
    private static DeployifyStateFile Read(string json) => StateManager.Read(Encoding.UTF8.GetBytes(json), "deployify-state.json");

    [Fact]
    public void CamelCaseKeys_AreRead()
    {
        var file = Read("""
            {
              "activeEnvironment": "prod",
              "environments": {
                "prod": {
                  "target": "Kubernetes",
                  "containerRegistry": "registry.example.com/myapp",
                  "imageTag": "stable",
                  "outputPath": "out",
                  "prune": true,
                  "ignoreResources": ["storage"],
                  "serviceOverrides": { "api": { "imageTag": "canary", "count": 2 } },
                  "targetConfig": { "kubernetes": { "namespacePrefix": "myapp" } }
                }
              }
            }
            """);

        file.ActiveEnvironment.ShouldBe("prod");
        var prod = file.Environments["prod"];
        prod.Target.ShouldBe(DeploymentTargetType.Kubernetes);
        prod.ContainerRegistry.ShouldBe("registry.example.com/myapp");
        prod.ImageTag.ShouldBe("stable");
        prod.OutputPath.ShouldBe("out");
        prod.Prune.ShouldBeTrue();
        prod.IgnoreResources.ShouldBe(["storage"]);
        prod.ServiceOverrides["api"].ImageTag.ShouldBe("canary");
        prod.ServiceOverrides["api"].Count.ShouldBe(2);
        prod.TargetConfig.ShouldContainKey("kubernetes");
    }

    [Fact]
    public void MixedCaseKeys_AreRead()
    {
        var file = Read("""{ "ACTIVEENVIRONMENT": "dev", "Environments": { "dev": { "TARGET": "Compose", "imagetag": "v2" } } }""");

        file.Environments["dev"].Target.ShouldBe(DeploymentTargetType.Compose);
        file.Environments["dev"].ImageTag.ShouldBe("v2");
    }

    [Fact]
    public void TargetConfigSections_AreFoundWhateverTheirCase()
    {
        var file = Read("""{ "environments": { "dev": { "targetConfig": { "Nomad": { "address": "http://nomad:4646" } } } } }""");

        var config = file.Environments["dev"].TargetConfig;
        config.ShouldContainKey("nomad");
        ((JsonElement)config["nomad"]!).GetProperty("address").GetString().ShouldBe("http://nomad:4646");
    }

    [Fact]
    public void TargetConfig_StaysCaseInsensitive_WhenReplaced()
    {
        var config = new EnvironmentConfig { TargetConfig = new Dictionary<string, object?> { ["Kubernetes"] = 1 } };
        config.TargetConfig.ShouldContainKey("kubernetes");

        config.WithTargetConfig("ovhcloud", 2).TargetConfig.ShouldContainKey("Kubernetes");
    }

    [Fact]
    public void TheSameKeyInDifferentCase_IsRefused()
    {
        var ex = Should.Throw<StateFormatException>(() =>
            Read("""{ "environments": { "dev": { "target": "Compose", "Target": "Nomad" } } }"""));
        ex.Message.ShouldContain("deployify-state.json");
    }

    [Fact]
    public void TwoTargetConfigSectionsDifferingInCase_AreRefused()
        => Should.Throw<StateFormatException>(() =>
            Read("""{ "environments": { "dev": { "targetConfig": { "nomad": {}, "Nomad": {} } } } }"""));

    [Fact]
    public void ACamelCaseNewerVersion_IsRefused_NotReadAsUnversioned()
        => Should.Throw<StateFormatException>(() => Read("""{ "formatVersion": 99, "environments": {} }"""))
            .Message.ShouldContain("format version 99");

    [Fact]
    public void TwoVersionKeys_AreRefused()
        => Should.Throw<StateFormatException>(() => Read("""{ "formatVersion": 1, "FormatVersion": 1, "environments": {} }"""))
            .Message.ShouldContain("more than once");

    [Fact]
    public void ACamelCaseVersion_IsReadAndWrittenBackInPascalCase()
    {
        var file = Read("""{ "formatVersion": 1, "environments": { "dev": {} } }""");

        file.FormatVersion.ShouldBe(StateFormat.StateFile);
        file.Environments.ShouldContainKey("dev");
    }

    // ── The examples in the docs ──────────────────────────────────────────────

    public static TheoryData<string, int> DocsStateFileExamples()
    {
        var data = new TheoryData<string, int>();
        var docs = Path.Combine(RepositoryRoot(), "docs", "src", "content", "docs");
        foreach (var page in Directory.EnumerateFiles(docs, "*.md*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var blocks = Regex.Matches(File.ReadAllText(page).Replace("\r\n", "\n"), "```json\n(.*?)```", RegexOptions.Singleline);
            for (var i = 0; i < blocks.Count; i++)
            {
                if (TryParse(blocks[i].Groups[1].Value) is JsonObject root &&
                    root.Any(p => string.Equals(p.Key, "environments", StringComparison.OrdinalIgnoreCase)))
                {
                    data.Add(Path.GetRelativePath(docs, page).Replace('\\', '/'), i);
                }
            }
        }

        return data;
    }

    /// <summary>
    /// Every state file shown in the docs loads, and every value it sets survives a load and a
    /// save: a key Deployify doesn't know (misspelled, or in the wrong place) would be dropped
    /// without a word, and the reader's environment would silently use a default.
    /// </summary>
    [Theory]
    [MemberData(nameof(DocsStateFileExamples))]
    public void DocsExamples_LoadWithEveryValue(string page, int block)
    {
        var docs = Path.Combine(RepositoryRoot(), "docs", "src", "content", "docs");
        var json = Regex.Matches(File.ReadAllText(Path.Combine(docs, page)).Replace("\r\n", "\n"), "```json\n(.*?)```",
            RegexOptions.Singleline)[block].Groups[1].Value;

        var file = Read(json);
        // Written the way StateManager writes it.
        var written = JsonSerializer.SerializeToNode(file, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;

        var missing = Leaves(JsonNode.Parse(json)!, "")
            .Where(leaf => Find(written, leaf.Path) is not { } value || !JsonNode.DeepEquals(value, leaf.Value))
            .Select(leaf => leaf.Path)
            .ToList();
        missing.ShouldBeEmpty($"{page}, JSON block {block}: values Deployify didn't read");
    }

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<(string Path, JsonNode? Value)> Leaves(JsonNode? node, string path) => node switch
    {
        JsonObject obj => obj.SelectMany(p => Leaves(p.Value, path + "/" + p.Key)),
        _ => [(path, node)]
    };

    /// <summary>Follows <paramref name="path"/>, matching object keys case-insensitively.</summary>
    private static JsonNode? Find(JsonNode? node, string path)
    {
        foreach (var key in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is not JsonObject obj)
            {
                return null;
            }

            node = obj.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        }

        return node;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Deployify.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root (Deployify.slnx).");
    }
}
