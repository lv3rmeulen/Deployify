using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Deployify.Core.State;

namespace Deployify.Core.Deployments;

/// <summary>
/// Builds release records from a run and adds them to the environment's
/// <see cref="ReleaseLog"/>. Recording is best effort: a release that happened is never turned
/// into a failed run because its record couldn't be written, so failures are only warnings.
/// </summary>
public static partial class ReleaseRecorder
{
    /// <summary>How many of the newest records a commit lookup reads before giving up.</summary>
    internal const int CommitLookupDepth = 25;

    /// <summary>The manifest's built images as <paramref name="config"/> runs them, with their digests when pinned.</summary>
    public static IReadOnlyList<ReleaseImage> Images(AspireManifest manifest, EnvironmentConfig config)
        => [.. manifest.Resources
            .Where(r => r.Value.IsBuiltImage)
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r =>
            {
                var repository = ServiceImage.Repository(r.Key, config);
                var tag = ServiceImage.Tag(r.Key, config);
                return new ReleaseImage(r.Key, repository, tag,
                    config.PublishedImages.TryGetDigest(repository, tag, out var digest) ? digest : null);
            })];

    /// <summary>
    /// The commit the images tagged <paramref name="tag"/> were built from, without looking at the
    /// working tree: the tag itself when it is a full commit SHA (what <c>ci</c> tags with), else the
    /// commit of the newest publish of that tag in <paramref name="history"/>. Null when neither
    /// knows. Use this for runs that deploy or promote images built elsewhere, where the checked-out
    /// commit says nothing about the images.
    /// </summary>
    public static async Task<string?> CommitOfTagAsync(StateSession history, string? tag, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return null;
        }

        if (FullSha().IsMatch(tag))
        {
            return tag.ToLowerInvariant();
        }

        try
        {
            foreach (var record in await ReleaseLog.ListAsync(history, CommitLookupDepth, ct))
            {
                if (record is { Kind: ReleaseKind.Publish, Commit: { Length: > 0 } commit } && record.Tag == tag)
                {
                    return commit;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only enriches the record; the release itself doesn't depend on it.
        }

        return null;
    }

    /// <summary>
    /// The commit being built in <paramref name="directory"/>: a CI commit variable, else
    /// <c>git rev-parse HEAD</c> when the working tree is clean. Null otherwise.
    /// </summary>
    public static async Task<string?> CommitBeingBuiltAsync(string directory, CancellationToken ct = default)
    {
        try
        {
            return (await CommitImageTag.ResolveAsync(directory, ct: ct)).Tag?.Tag;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// A record for a release in <paramref name="environment"/>, stamped with the current time,
    /// a new id, who ran it (<see cref="ReleaseContext"/>) and the Deployify version.
    /// </summary>
    public static ReleaseRecord Create(ReleaseKind kind, string environment, ReleaseOutcome outcome,
        TimeProvider? time = null, ReleaseContext? context = null)
    {
        context ??= ReleaseContext.Resolve();
        return new ReleaseRecord
        {
            Id = ReleaseLog.NewId(),
            Kind = kind,
            Environment = environment,
            At = (time ?? TimeProvider.System).GetUtcNow(),
            Outcome = outcome,
            Actor = context.Actor,
            ActorSource = context.ActorSource,
            RunUrl = context.RunUrl,
            DeployifyVersion = StateFormat.ToolVersion
        };
    }

    /// <summary>The environment's <c>State.History.Keep</c>.</summary>
    public static int Keep(StateSession state) => state.Config.History?.Keep ?? ReleaseLog.DefaultKeep;

    /// <summary>True when the environment's <c>State.History.Keep</c> turns the history off.</summary>
    public static bool IsDisabled(StateSession state) => ReleaseLog.IsDisabled(Keep(state));

    /// <summary>
    /// Appends <paramref name="record"/> to the history in <paramref name="state"/>, keeping as many
    /// records as the environment's <c>State.History.Keep</c> says. Returns false when the history
    /// is off, or, after warning through <paramref name="warn"/>, when it couldn't be written.
    /// </summary>
    public static async Task<bool> AppendAsync(StateSession state, ReleaseRecord record, Action<string>? warn, CancellationToken ct = default)
    {
        try
        {
            return await ReleaseLog.AppendAsync(state, record, Keep(state), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warn?.Invoke($"Could not record the {record.Kind.ToString().ToLowerInvariant()} in the release history " +
                         $"({state.Backend.Description}): {ex.Message}");
            return false;
        }
    }

    /// <summary>The release outcome of a rollout that ended in <paramref name="outcome"/>.</summary>
    public static ReleaseOutcome Outcome(Rollouts.RolloutOutcome outcome) => outcome switch
    {
        Rollouts.RolloutOutcome.Succeeded => ReleaseOutcome.Succeeded,
        Rollouts.RolloutOutcome.RolledBack => ReleaseOutcome.RolledBack,
        _ => ReleaseOutcome.Failed
    };

    [GeneratedRegex("^[0-9a-fA-F]{40}([0-9a-fA-F]{24})?$")]
    private static partial Regex FullSha();
}
