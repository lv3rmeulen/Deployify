using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.Services;
using Deployify.Core.State;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.State;

/// <summary>
/// Layered environments: an environment that extends another inherits its settings and the
/// state file keeps only what differs, across loads and saves.
/// </summary>
public sealed class EnvironmentInheritanceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDir _dir = new();
    private readonly StateManager _sut = new();

    public void Dispose() => _dir.Dispose();

    private string StatePath => Path.Combine(_dir.Path, "deployify-state.json");

    private const string Layered = """
        {
          "activeEnvironment": "prod",
          "environments": {
            "base": {
              "abstract": true,
              "target": "Nomad",
              "containerRegistry": "registry.example.com/shop",
              "imageTag": "latest",
              "ignoreResources": ["storage", "frontend"],
              "parameters": { "db-user": "app", "region": "eu" },
              "targetConfig": {
                "nomad": { "address": "http://nomad:4646", "namespace": "default", "datacenters": ["dc1"] }
              }
            },
            "staging": {
              "extends": "base",
              "imageTag": "staging",
              "targetConfig": { "nomad": { "namespace": "staging" } }
            },
            "prod": {
              "extends": "staging",
              "imageTag": "stable",
              "protected": true,
              "ignoreResources": ["storage"],
              "parameters": { "region": null },
              "targetConfig": { "nomad": { "namespace": "production", "token": "env:NOMAD_TOKEN" } }
            }
          }
        }
        """;

    private async Task<DeployifyStateFile> LoadAsync(string json)
    {
        await File.WriteAllTextAsync(StatePath, json, Ct);
        return await _sut.LoadFileAsync(_dir.Path, Ct);
    }

    private async Task<JsonObject> SavedEnvAsync(string name)
        => JsonNode.Parse(await File.ReadAllTextAsync(StatePath, Ct))!["Environments"]![name]!.AsObject();

    private static JsonObject Nomad(EnvironmentConfig config)
        => JsonSerializer.SerializeToNode(config.TargetConfig["nomad"])!.AsObject();

    [Fact]
    public async Task AnEnvironment_InheritsItsBases_AndOverridesWhatItSets()
    {
        var file = await LoadAsync(Layered);
        var prod = file.Environments["prod"];

        prod.Target.ShouldBe(DeploymentTargetType.Nomad);
        prod.ContainerRegistry.ShouldBe("registry.example.com/shop"); // from base, through staging
        prod.ImageTag.ShouldBe("stable");
        prod.Protected.ShouldBeTrue();
        prod.Abstract.ShouldBeFalse(); // not inherited
        prod.Extends.ShouldBe("staging");

        // Objects merge key by key.
        var nomad = Nomad(prod);
        ((string)nomad["address"]!).ShouldBe("http://nomad:4646");
        ((string)nomad["namespace"]!).ShouldBe("production");
        ((string)nomad["token"]!).ShouldBe("env:NOMAD_TOKEN");

        // Lists replace; null removes.
        prod.IgnoreResources.ShouldBe(["storage"]);
        prod.Parameters.ShouldBe(new Dictionary<string, string> { ["db-user"] = "app" });

        var staging = file.Environments["staging"];
        staging.ImageTag.ShouldBe("staging");
        ((string)Nomad(staging)["namespace"]!).ShouldBe("staging");
        staging.Parameters["region"].ShouldBe("eu");
    }

    [Fact]
    public async Task KeysIgnoreCase_AcrossLayers()
    {
        var file = await LoadAsync("""
            {
              "Environments": {
                "base": { "Target": "Compose", "ImageTag": "latest", "TargetConfig": { "Compose": { "ProjectName": "shop" } } },
                "dev": { "EXTENDS": "base", "imagetag": "dev", "targetconfig": { "compose": { "projectname": "shop-dev" } } }
              }
            }
            """);

        var dev = file.Environments["dev"];
        dev.ImageTag.ShouldBe("dev");
        dev.Target.ShouldBe(DeploymentTargetType.Compose);
        ((string)JsonSerializer.SerializeToNode(dev.TargetConfig["compose"])!["projectname"]!).ShouldBe("shop-dev");
    }

    [Fact]
    public async Task Saving_WithoutChanges_KeepsEachLayerAsWritten()
    {
        var file = await LoadAsync(Layered);
        await _sut.SaveFileAsync(file, _dir.Path, Ct);

        var prod = await SavedEnvAsync("prod");
        prod.Select(p => p.Key).ShouldBe(
            ["Extends", "imageTag", "protected", "ignoreResources", "parameters", "targetConfig"], ignoreOrder: true);
        prod["parameters"]!["region"].ShouldBeNull();
        (await SavedEnvAsync("staging")).Select(p => p.Key).ShouldBe(["Extends", "imageTag", "targetConfig"], ignoreOrder: true);

        // Base environments (not extending anything) are written as before.
        ((bool)(await SavedEnvAsync("base"))["Abstract"]!).ShouldBeTrue();

        var reloaded = await _sut.LoadFileAsync(_dir.Path, Ct);
        reloaded.Environments["prod"].Parameters.ShouldNotContainKey("region");
        reloaded.Environments["prod"].ContainerRegistry.ShouldBe("registry.example.com/shop");
    }

    [Fact]
    public async Task AChangeToABase_ReachesEnvironmentsThatInheritIt()
    {
        var file = await LoadAsync(Layered);
        file.Environments["base"].ContainerRegistry = "ghcr.io/acme/shop";
        await _sut.SaveFileAsync(file, _dir.Path, Ct);

        // prod still holds the old value in memory, but didn't change it, so it keeps inheriting.
        (await SavedEnvAsync("prod")).ContainsKey("ContainerRegistry").ShouldBeFalse();
        var reloaded = await _sut.LoadFileAsync(_dir.Path, Ct);
        reloaded.Environments["prod"].ContainerRegistry.ShouldBe("ghcr.io/acme/shop");
    }

    [Fact]
    public async Task AChangeToAnInheritedSetting_BecomesAnOverride()
    {
        var file = await LoadAsync(Layered);
        file.Environments["staging"].ContainerRegistry = "registry.example.com/staging";
        file.Environments["staging"].ServiceOverrides["api"] = new ServiceOverride { Count = 2 };
        await _sut.SaveFileAsync(file, _dir.Path, Ct);

        var saved = await SavedEnvAsync("staging");
        ((string)saved["ContainerRegistry"]!).ShouldBe("registry.example.com/staging");
        ((int)saved["ServiceOverrides"]!["api"]!["Count"]!).ShouldBe(2);

        var reloaded = await _sut.LoadFileAsync(_dir.Path, Ct);
        reloaded.Environments["base"].ContainerRegistry.ShouldBe("registry.example.com/shop");
        reloaded.Environments["staging"].ContainerRegistry.ShouldBe("registry.example.com/staging");
        reloaded.Environments["prod"].ContainerRegistry.ShouldBe("registry.example.com/staging");
        reloaded.Environments["prod"].ServiceOverrides["api"].Count.ShouldBe(2);
    }

    [Fact]
    public async Task RemovingAnInheritedEntry_IsWrittenAsNull()
    {
        var file = await LoadAsync(Layered);
        file.Environments["staging"].Parameters.Remove("db-user");
        file.Environments["staging"].ContainerRegistry = null;
        await _sut.SaveFileAsync(file, _dir.Path, Ct);

        var saved = await SavedEnvAsync("staging");
        saved["Parameters"]!.AsObject().ContainsKey("db-user").ShouldBeTrue();
        saved["Parameters"]!["db-user"].ShouldBeNull();

        var reloaded = await _sut.LoadFileAsync(_dir.Path, Ct);
        reloaded.Environments["staging"].Parameters.ShouldNotContainKey("db-user");
        reloaded.Environments["staging"].ContainerRegistry.ShouldBeNull();
        reloaded.Environments["base"].Parameters["db-user"].ShouldBe("app");
    }

    [Fact]
    public async Task ANewExtendingEnvironment_IsWrittenAsItsDifferences()
    {
        var file = await LoadAsync(Layered);
        var qa = StateManager.CreateExtending(file.Environments["staging"], "staging");
        qa.ImageTag = "qa";
        file.Environments["qa"] = qa;
        await _sut.SaveFileAsync(file, _dir.Path, Ct);

        var saved = await SavedEnvAsync("qa");
        saved.Select(p => p.Key).ShouldBe(["Extends", "ImageTag"], ignoreOrder: true);
        var reloaded = await _sut.LoadFileAsync(_dir.Path, Ct);
        ((string)Nomad(reloaded.Environments["qa"])["namespace"]!).ShouldBe("staging");
    }

    [Fact]
    public async Task SecretReferences_AreNotInherited()
    {
        var file = await LoadAsync(Layered);
        file.Environments["staging"].Secrets.References["api/db"] = new SecretReference { Location = "staging/api" };
        await _sut.SaveFileAsync(file, _dir.Path, Ct);

        var reloaded = await _sut.LoadFileAsync(_dir.Path, Ct);
        reloaded.Environments["staging"].Secrets.References.ShouldContainKey("api/db");
        reloaded.Environments["prod"].Secrets.References.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheFormatVersion_IsRaisedOnlyForLayeredFiles()
    {
        var file = await LoadAsync(Layered);
        await _sut.SaveFileAsync(file, _dir.Path, Ct);
        ((int)JsonNode.Parse(await File.ReadAllTextAsync(StatePath, Ct))!["FormatVersion"]!).ShouldBe(2);

        var flat = new DeployifyStateFile { Environments = { ["dev"] = new EnvironmentConfig() } };
        await _sut.SaveFileAsync(flat, _dir.Path, Ct);
        ((int)JsonNode.Parse(await File.ReadAllTextAsync(StatePath, Ct))!["FormatVersion"]!).ShouldBe(1);
    }

    [Fact]
    public async Task AnAbstractEnvironment_CantBeLoadedForDeploying()
    {
        await LoadAsync(Layered);
        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => _sut.LoadActiveAsync(_dir.Path, "base", Ct));
        ex.Message.ShouldContain("abstract");
        ex.Message.ShouldContain("staging, prod");
        (await _sut.LoadActiveAsync(_dir.Path, "staging", Ct)).Config.Name.ShouldBe("staging");
    }

    [Theory]
    [InlineData("""{ "Environments": { "dev": { "Extends": "ghost" } } }""", "doesn't exist")]
    [InlineData("""{ "Environments": { "dev": { "Extends": "Base" }, "base": {} } }""", "did you mean 'base'")]
    [InlineData("""{ "Environments": { "dev": { "Extends": "dev" } } }""", "loop (dev → dev)")]
    [InlineData("""{ "Environments": { "a": { "Extends": "b" }, "b": { "Extends": "a" } } }""", "loop (a → b → a)")]
    [InlineData("""{ "Environments": { "dev": { "Extends": 3 } } }""", "must be the name of another environment")]
    [InlineData("""{ "Environments": { "base": {}, "dev": { "Extends": "base", "ImageTag": "a", "imageTag": "b" } } }""", "more than once")]
    public async Task ABrokenChain_IsRefused_AndTheFileLeftAlone(string json, string message)
    {
        var ex = await Should.ThrowAsync<StateFormatException>(() => LoadAsync(json));
        ex.Message.ShouldContain(message);
        (await File.ReadAllTextAsync(StatePath, Ct)).ShouldBe(json);
    }

    [Fact]
    public async Task AChainDeeperThanTheLimit_IsRefused()
    {
        var envs = new JsonObject { ["e0"] = new JsonObject() };
        for (var i = 1; i <= EnvironmentInheritance.MaxDepth; i++)
        {
            envs[$"e{i}"] = new JsonObject { ["Extends"] = $"e{i - 1}" };
        }

        var ex = await Should.ThrowAsync<StateFormatException>(
            () => LoadAsync(new JsonObject { ["Environments"] = envs }.ToJsonString()));
        ex.Message.ShouldContain("levels deep");
    }

    [Fact]
    public async Task SavingABrokenChain_IsRefused()
    {
        var file = new DeployifyStateFile { Environments = { ["dev"] = new EnvironmentConfig { Extends = "ghost" } } };
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.SaveFileAsync(file, _dir.Path, Ct));
        ex.Message.ShouldContain("'ghost', which doesn't exist");
        File.Exists(StatePath).ShouldBeFalse();
    }
}
