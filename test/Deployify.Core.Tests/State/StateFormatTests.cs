using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Deployify.Core.State;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.State;

/// <summary>
/// Format versions: files from the alphas (no version) still load and are upgraded; files from
/// a newer Deployify are refused with an explanation and left untouched.
/// </summary>
public sealed class StateFormatTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDir _local = new();
    private readonly TempDir _remoteDir = new();
    private readonly LocalStateBackend _remote;

    public StateFormatTests() => _remote = new LocalStateBackend(_remoteDir.Path);

    public void Dispose()
    {
        _local.Dispose();
        _remoteDir.Dispose();
    }

    private StateSession Remote()
        => new(_local.Path, "prod", new StateBackendConfig { Backend = StateBackendType.Consul }, _remote);

    private async Task PutRemoteAsync(string key, string json)
        => await _remote.WriteAsync(key, Encoding.UTF8.GetBytes(json), (await _remote.ReadAsync(key, Ct))?.ETag, Ct);

    private async Task<JsonObject> ReadRemoteAsync(string key)
        => JsonNode.Parse((await _remote.ReadAsync(key, Ct))!.Data)!.AsObject();

    private static void ShouldExplainNewerVersion(StateFormatException ex, int version)
    {
        ex.Message.ShouldContain($"format version {version}");
        ex.Message.ShouldContain("newer Deployify");
        ex.Message.ShouldContain("update Deployify");
    }

    // ── deployify-state.json ───────────────────────────────────────────────

    // As written by 1.0.0-alpha.16: no FormatVersion, target as a plain enum name.
    private const string AlphaStateFile = """
        {
          "ActiveEnvironment": "prod",
          "Environments": {
            "prod": {
              "Target": "Nomad",
              "ContainerRegistry": "registry.example.com/shop",
              "ImageTag": "1.4.2",
              "OutputPath": "deployify-output",
              "ServiceOverrides": { "api": { "Count": 2, "Env": { "LOG_LEVEL": "debug" } } },
              "ContainerRuntime": "Docker",
              "DeployDashboard": false,
              "Dashboard": { "ImageTag": "latest", "Port": 18888, "OtlpPort": 18889, "Cpu": "100m", "Memory": "128Mi" },
              "ContainerOs": "linux",
              "ContainerArch": "x64",
              "DeployDapr": false,
              "Dapr": { "DaprVersion": "latest", "DaprHttpPort": 3500, "DaprGrpcPort": 50001, "LogLevel": "info" },
              "TargetConfig": { "nomad": { "Address": "http://nomad:4646" } }
            }
          }
        }
        """;

    [Fact]
    public async Task AnAlphaStateFile_StillLoads_AndIsSavedInTheCurrentFormat()
    {
        var path = Path.Combine(_local.Path, "deployify-state.json");
        await File.WriteAllTextAsync(path, AlphaStateFile, Ct);
        var manager = new StateManager();

        var (config, env) = await manager.LoadActiveAsync(_local.Path, ct: Ct);

        env.ShouldBe("prod");
        config.Target.ShouldBe(DeploymentTargetType.Nomad);
        config.ContainerRegistry.ShouldBe("registry.example.com/shop");
        config.ServiceOverrides["api"].Count.ShouldBe(2);
        config.TargetConfig.ShouldContainKey("nomad");

        // Loading alone leaves the file as it was; the next save writes the version.
        (await File.ReadAllTextAsync(path, Ct)).ShouldBe(AlphaStateFile);
        var file = await manager.LoadFileAsync(_local.Path, Ct);
        file.FormatVersion.ShouldBe(StateFormat.StateFile);
        await manager.SaveFileAsync(file, _local.Path, Ct);

        var saved = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!.AsObject();
        ((int)saved["FormatVersion"]!).ShouldBe(StateFormat.StateFileWithoutLayers);
        (await manager.LoadFileAsync(_local.Path, Ct)).Environments["prod"].ImageTag.ShouldBe("1.4.2");
    }

    [Fact]
    public async Task AStateFileFromANewerVersion_IsRefused_AndLeftAlone()
    {
        var path = Path.Combine(_local.Path, "deployify-state.json");
        var root = JsonNode.Parse(AlphaStateFile)!.AsObject();
        root["FormatVersion"] = 99;
        var future = root.ToJsonString();
        await File.WriteAllTextAsync(path, future, Ct);

        var ex = await Should.ThrowAsync<StateFormatException>(() => new StateManager().LoadActiveAsync(_local.Path, ct: Ct));

        ShouldExplainNewerVersion(ex, 99);
        ex.Message.ShouldContain("deployify-state.json");
        (await File.ReadAllTextAsync(path, Ct)).ShouldBe(future);
    }

    [Fact]
    public void AnInvalidVersion_IsRefused()
        => Should.Throw<StateFormatException>(() =>
                StateManager.Read(Encoding.UTF8.GetBytes("""{ "FormatVersion": "two", "Environments": {} }"""), "deployify-state.json"))
            .Message.ShouldContain("invalid FormatVersion");

    // ── inventory.json ─────────────────────────────────────────────────────

    private const string InventoryKey = "deployify/prod/inventory.json";

    [Fact]
    public async Task AnAlphaInventory_StillLoads_AndTheNextUpdateWritesTheVersion()
    {
        await PutRemoteAsync(InventoryKey, """
            { "lineage": "abc123", "serial": 4, "environment": "prod", "target": "Nomad",
              "updatedAt": "2026-10-01T12:00:00+00:00",
              "resources": [ { "kind": "service", "name": "api", "dependsOn": ["db"],
                               "createdAt": "2026-09-01T12:00:00+00:00", "updatedAt": "2026-10-01T12:00:00+00:00" } ] }
            """);
        var store = new InventoryStore(_remote, InventoryKey, "prod");

        var inventory = await store.LoadAsync(Ct);
        inventory.Lineage.ShouldBe("abc123");
        inventory.Serial.ShouldBe(4);
        inventory.Find("api")!.DependsOn.ShouldBe(["db"]);

        await store.UpdateAsync(inv => inv.Upsert("web", [], DateTimeOffset.UtcNow), Ct);

        var saved = await ReadRemoteAsync(InventoryKey);
        ((int)saved[StateFormat.VersionProperty]!).ShouldBe(StateFormat.Inventory);
        ((string)saved["lineage"]!).ShouldBe("abc123");
        ((long)saved["serial"]!).ShouldBe(5);
    }

    [Fact]
    public async Task AnInventoryFromANewerVersion_IsRefused_AndNotOverwritten()
    {
        const string future = """{ "formatVersion": 7, "lineage": "abc123", "serial": 9, "resources": [] }""";
        await PutRemoteAsync(InventoryKey, future);
        var store = new InventoryStore(_remote, InventoryKey, "prod");

        ShouldExplainNewerVersion(await Should.ThrowAsync<StateFormatException>(() => store.LoadAsync(Ct)), 7);
        var ex = await Should.ThrowAsync<StateFormatException>(
            () => store.UpdateAsync(inv => inv.Upsert("web", [], DateTimeOffset.UtcNow), Ct));

        ex.Message.ShouldContain(InventoryKey);
        Encoding.UTF8.GetString((await _remote.ReadAsync(InventoryKey, Ct))!.Data).ShouldBe(future);
    }

    // ── images.json ────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAlphaImagesFile_StillLoads_AndIsRewrittenInTheCurrentFormat()
    {
        await using var session = Remote();
        var key = PublishedImageStore.Key(session);
        var t0 = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var alpha = new PublishedImages();
        alpha.Record("reg/api", "v1", PublishedImagesTests.Digest, t0);
        // The alpha format: the map of images on its own.
        await _remote.WriteAsync(key, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            alpha.Recorded, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }), null, Ct);

        var loaded = new PublishedImages();
        await PublishedImageStore.LoadIntoAsync(session, loaded, Ct);
        loaded.TryGetDigest("reg/api", "v1", out var digest).ShouldBeTrue();
        digest.ShouldBe(PublishedImagesTests.Digest);

        loaded.Record("reg/web", "v1", PublishedImagesTests.OtherDigest, t0.AddMinutes(1));
        await PublishedImageStore.SaveAsync(session, loaded, Ct);

        var saved = await ReadRemoteAsync(key);
        ((int)saved[StateFormat.VersionProperty]!).ShouldBe(StateFormat.Images);
        saved["images"]!.AsObject().Count.ShouldBe(2);

        var reloaded = new PublishedImages();
        await PublishedImageStore.LoadIntoAsync(session, reloaded, Ct);
        reloaded.TryGetDigest("reg/api", "v1", out _).ShouldBeTrue();
        reloaded.TryGetDigest("reg/web", "v1", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task AnImagesFileFromANewerVersion_IsRefused_AndNotOverwritten()
    {
        await using var session = Remote();
        var key = PublishedImageStore.Key(session);
        const string future = """{ "formatVersion": 3, "images": {} }""";
        await PutRemoteAsync(key, future);

        ShouldExplainNewerVersion(await Should.ThrowAsync<StateFormatException>(
            () => PublishedImageStore.LoadIntoAsync(session, new PublishedImages(), Ct)), 3);

        var mine = new PublishedImages();
        mine.Record("reg/api", "v2", PublishedImagesTests.Digest, DateTimeOffset.UtcNow);
        await Should.ThrowAsync<StateFormatException>(() => PublishedImageStore.SaveAsync(session, mine, Ct));
        Encoding.UTF8.GetString((await _remote.ReadAsync(key, Ct))!.Data).ShouldBe(future);
    }

    // ── parameters.json ────────────────────────────────────────────────────

    private const string ParametersKey = "deployify/prod/parameters.json";

    [Fact]
    public async Task AnAlphaParametersFile_StillLoads_AndThePushWritesTheCurrentFormat()
    {
        await PutRemoteAsync(ParametersKey, """{ "pg.value": "shared" }""");
        ParameterStore.WriteSection(_local.Path, "prod", new Dictionary<string, string> { ["redis.value"] = "new" });

        await using var session = Remote();
        await session.PullAsync(Ct);
        ParameterStore.ReadSection(_local.Path, "prod").ShouldBe(new Dictionary<string, string> { ["pg.value"] = "shared" });

        ParameterStore.WriteSection(_local.Path, "prod",
            new Dictionary<string, string> { ["pg.value"] = "shared", ["redis.value"] = "new" });
        await session.PushParametersAsync(Ct);

        var saved = await ReadRemoteAsync(ParametersKey);
        ((int)saved[StateFormat.VersionProperty]!).ShouldBe(StateFormat.Parameters);
        StateFormat.ReadParameters((await _remote.ReadAsync(ParametersKey, Ct))!.Data, ParametersKey)
            .ShouldBe(new Dictionary<string, string> { ["pg.value"] = "shared", ["redis.value"] = "new" });
    }

    [Fact]
    public void AnAlphaParameterNamedFormatVersion_IsStillAValue()
        => StateFormat.ReadParameters(Encoding.UTF8.GetBytes("""{ "formatVersion": "s3cret" }"""), ParametersKey)
            .ShouldBe(new Dictionary<string, string> { ["formatVersion"] = "s3cret" });

    [Fact]
    public async Task AParametersFileFromANewerVersion_IsRefused_AndTheLocalValuesAreKept()
    {
        const string future = """{ "formatVersion": 2, "parameters": { "pg.value": "x" } }""";
        await PutRemoteAsync(ParametersKey, future);
        ParameterStore.WriteSection(_local.Path, "prod", new Dictionary<string, string> { ["pg.value"] = "local" });

        await using var session = Remote();
        var ex = await Should.ThrowAsync<StateFormatException>(() => session.PullAsync(Ct));

        ShouldExplainNewerVersion(ex, 2);
        ex.Message.ShouldContain(ParametersKey);
        ParameterStore.ReadSection(_local.Path, "prod").ShouldBe(new Dictionary<string, string> { ["pg.value"] = "local" });
        Encoding.UTF8.GetString((await _remote.ReadAsync(ParametersKey, Ct))!.Data).ShouldBe(future);
    }
}
