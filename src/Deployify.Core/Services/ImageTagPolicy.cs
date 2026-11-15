using System;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.Rollouts;

namespace Deployify.Core.Services;

/// <summary>How <see cref="ImageTagPolicy"/> settled the image tag for a run.</summary>
public enum ImageTagOutcome
{
    /// <summary>The run doesn't publish, so the configured tag (or the override) is used as is.</summary>
    NotPublishing,

    /// <summary>The configured tag, or the override, is unique to a build and is used as is.</summary>
    Immutable,

    /// <summary>The tag was "latest" or empty, so the commit SHA replaced it.</summary>
    CommitSha,

    /// <summary>A mutable tag is kept because mutable tags are allowed. The run goes on with a warning.</summary>
    MutableAllowed,

    /// <summary>A mutable tag was set explicitly and mutable tags aren't allowed. The run stops.</summary>
    MutableOverrideRefused,

    /// <summary>The tag was "latest" or empty, no commit SHA is available and mutable tags aren't allowed. The run stops.</summary>
    NoCommitSha
}

/// <summary>The tag a run uses, and why. <see cref="Tag"/> is also written to the config.</summary>
/// <param name="Outcome">How the tag was settled.</param>
/// <param name="Tag">The tag the run uses; for a refused run, the mutable tag it would have used ("" when none).</param>
/// <param name="CommitSource">For <see cref="ImageTagOutcome.CommitSha"/>: the CI variable or "git" that supplied the SHA.</param>
/// <param name="Reason">Why no commit SHA was available, when one was looked for and not found.</param>
/// <param name="FromOverride">True when the tag came from an explicit override, not the configuration.</param>
public sealed record ImageTagDecision(
    ImageTagOutcome Outcome, string Tag, string? CommitSource = null, string? Reason = null, bool FromOverride = false)
{
    /// <summary>False when the run must stop.</summary>
    public bool Succeeded => Outcome is not (ImageTagOutcome.MutableOverrideRefused or ImageTagOutcome.NoCommitSha);
}

/// <summary>
/// Settles the image tag for a deploy that may publish images, the same way for <c>deployify ci</c>
/// and <c>aspire deploy</c>. An explicit tag wins. When the run publishes and the tag would be
/// "latest" (or empty), the commit SHA is used instead, so every build pushes a new image and a
/// rollback can return to the previous one. With no SHA available the run stops unless mutable
/// tags are allowed. A run that doesn't publish keeps the configured tag, so it deploys (and
/// compares against) what was published before.
/// </summary>
public static class ImageTagPolicy
{
    public static async Task<ImageTagDecision> ApplyAsync(
        EnvironmentConfig config,
        string? tagOverride,
        bool publishes,
        bool allowMutableTag,
        string directory,
        Func<string, string?>? getEnvironmentVariable = null,
        IProcessRunner? runner = null,
        CancellationToken ct = default)
    {
        var fromOverride = !string.IsNullOrWhiteSpace(tagOverride);
        if (fromOverride)
        {
            config.ImageTag = tagOverride!.Trim();
        }

        if (!publishes)
        {
            return new ImageTagDecision(ImageTagOutcome.NotPublishing, config.ImageTag, FromOverride: fromOverride);
        }

        if (!ImageTagCheck.IsMutableTag(config.ImageTag))
        {
            return new ImageTagDecision(ImageTagOutcome.Immutable, config.ImageTag, FromOverride: fromOverride);
        }

        var mutable = config.ImageTag?.Trim() ?? "";
        if (fromOverride)
        {
            // Asking for "latest" explicitly is a choice, not a default to replace.
            return new ImageTagDecision(
                allowMutableTag ? ImageTagOutcome.MutableAllowed : ImageTagOutcome.MutableOverrideRefused,
                mutable, FromOverride: true);
        }

        var (commit, reason) = await CommitImageTag.ResolveAsync(directory, getEnvironmentVariable, runner, ct);
        if (commit is not null)
        {
            config.ImageTag = commit.Tag;
            return new ImageTagDecision(ImageTagOutcome.CommitSha, commit.Tag, CommitSource: commit.Source);
        }

        return new ImageTagDecision(
            allowMutableTag ? ImageTagOutcome.MutableAllowed : ImageTagOutcome.NoCommitSha, mutable, Reason: reason);
    }
}
