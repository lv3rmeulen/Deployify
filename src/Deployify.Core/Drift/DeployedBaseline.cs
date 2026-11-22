using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Deployify.Core.State;

namespace Deployify.Core.Drift;

/// <summary>
/// What the last successful deploy of an environment deployed: the manifest, the environment's
/// settings and the images, by digest. A drift check regenerates the artifacts from this and asks
/// the target how the live state differs, so a change to the AppHost or deployify-state.json
/// that hasn't been deployed yet is never reported as drift.
/// </summary>
/// <remarks>
/// Stored at <c>{prefix}/{env}/deployed.json</c> and replaced by every successful deploy. This is
/// a stored format covered by the 1.x stability policy, like <see cref="ReleaseRecord"/>: add
/// optional properties freely; anything else needs a new <see cref="StateFormat.Deployed"/>.
/// </remarks>
public sealed record DeployedBaseline
{
    public required string Environment { get; init; }

    /// <summary>When the deploy finished, in UTC.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The target it was deployed to, as <see cref="DeploymentTargetType.Value"/> spells it.</summary>
    public required string Target { get; init; }

    /// <summary>The deploy's release record, when the history is on.</summary>
    public string? ReleaseId { get; init; }

    public string? Tag { get; init; }

    public string? Commit { get; init; }

    public string? Actor { get; init; }

    public string? RunUrl { get; init; }

    public IReadOnlyList<ReleaseImage> Images { get; init; } = [];

    /// <summary>The environment's effective settings, as deployify-state.json spells them.</summary>
    public required JsonObject Config { get; init; }

    /// <summary>The Aspire manifest that was deployed, after Deployify's additions (dashboard, Dapr, references).</summary>
    public required JsonObject Manifest { get; init; }

    public string? DeployifyVersion { get; init; }

    /// <summary>
    /// The baseline of a deploy of <paramref name="manifest"/> with <paramref name="config"/>,
    /// described by <paramref name="release"/>. <paramref name="releaseStored"/> is false when the
    /// history is off, so the record's id names nothing.
    /// </summary>
    public static DeployedBaseline From(ReleaseRecord release, AspireManifest manifest, EnvironmentConfig config, string target,
        bool releaseStored = true) => new()
    {
        Environment = release.Environment,
        At = release.At,
        Target = target,
        ReleaseId = releaseStored ? release.Id : null,
        Tag = release.Tag,
        Commit = release.Commit,
        Actor = release.Actor,
        RunUrl = release.RunUrl,
        Images = release.Images,
        Config = StateManager.SerializeEnvironment(config),
        Manifest = JsonSerializer.SerializeToNode(manifest)!.AsObject(),
        DeployifyVersion = release.DeployifyVersion
    };

    /// <summary>
    /// The deployed settings, ready to generate from: named after the environment, with the
    /// deployed images pinned to their digests. Parameter values are left to the caller.
    /// </summary>
    public EnvironmentConfig ToConfig()
    {
        var config = StateManager.DeserializeEnvironment((JsonObject)Config.DeepClone(), $"The deployed baseline of {Environment}");
        config.Name = Environment;
        config.Extends = null;
        config.Abstract = false;
        config.PublishedImages.Load(Images
            .Where(i => i.Digest is not null)
            .GroupBy(i => PublishedImages.Key(i.Repository, i.Tag))
            .ToDictionary(g => g.Key, g => new PublishedImage(g.First().Digest!, At)));
        return config;
    }

    /// <summary>The deployed manifest.</summary>
    public AspireManifest ToManifest()
    {
        try
        {
            return Manifest.Deserialize<AspireManifest>()
                   ?? throw new StateFormatException($"The deployed baseline of {Environment} has an empty manifest.");
        }
        catch (JsonException ex)
        {
            throw new StateFormatException($"The deployed baseline of {Environment} has a manifest this Deployify can't read: {ex.Message}", ex);
        }
    }
}

/// <summary>Reads and writes <see cref="DeployedBaseline"/> in an environment's deployment state.</summary>
public static class DeployedBaselineStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Key(StateSession state) => state.Root + "deployed.json";

    /// <summary>The baseline, or null when nothing has been deployed since drift checks arrived.</summary>
    public static async Task<DeployedBaseline?> ReadAsync(StateSession state, CancellationToken ct = default)
    {
        var key = Key(state);
        return await state.Backend.ReadAsync(key, ct) is { } blob
            ? Read(blob.Data, $"State '{key}' in {state.Backend.Description}")
            : null;
    }

    /// <summary>
    /// Replaces the baseline. Best effort, like the release history: a deploy that happened is never
    /// failed because this couldn't be written; <paramref name="warn"/> says so and returns false.
    /// </summary>
    public static async Task<bool> SaveAsync(StateSession state, DeployedBaseline baseline, Action<string>? warn, CancellationToken ct = default)
    {
        if (baseline.Environment != state.Environment)
        {
            throw new ArgumentException(
                $"The baseline is for environment '{baseline.Environment}' but the state session is for '{state.Environment}'.",
                nameof(baseline));
        }

        try
        {
            var data = Write(baseline);
            await state.PutAsync(Key(state), _ => data, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warn?.Invoke($"Could not record what was deployed for drift checks ({state.Backend.Description}): {ex.Message}");
            return false;
        }
    }

    /// <summary>Removes the baseline (after <c>destroy</c>: nothing is deployed, so nothing can drift).</summary>
    public static Task DeleteAsync(StateSession state, CancellationToken ct = default)
        => state.Backend.DeleteAsync(Key(state), expectedETag: null, ct);

    public static byte[] Write(DeployedBaseline baseline)
    {
        var node = JsonSerializer.SerializeToNode(baseline, s_json)!.AsObject();
        var root = new JsonObject { [StateFormat.VersionProperty] = StateFormat.Deployed };
        foreach (var (name, value) in node.ToList())
        {
            node.Remove(name);
            root[name] = value;
        }

        return JsonSerializer.SerializeToUtf8Bytes(root, s_json);
    }

    public static DeployedBaseline Read(byte[] data, string what)
    {
        var root = StateFormat.ParseObject(data, what);
        if (StateFormat.ReadVersion(root, StateFormat.VersionProperty, StateFormat.Deployed, what) < 1)
        {
            throw new StateFormatException($"{what} has no {StateFormat.VersionProperty}, so it is not a deployed baseline.");
        }

        try
        {
            return root.Deserialize<DeployedBaseline>(s_json)
                   ?? throw new StateFormatException($"{what} is empty.");
        }
        catch (JsonException ex)
        {
            throw new StateFormatException($"{what} is not a valid deployed baseline: {ex.Message}", ex);
        }
    }
}
