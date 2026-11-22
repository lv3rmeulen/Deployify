using System;
using System.Collections.Generic;

namespace Deployify.Core.Models;

/// <summary>What a release record describes.</summary>
public enum ReleaseKind
{
    /// <summary>Images were built and pushed (<c>publish</c>, or the publish step of <c>ci</c>).</summary>
    Publish,

    /// <summary>Another environment's digests were recorded for this one (<c>promote</c>).</summary>
    Promote,

    /// <summary>Images were deployed (<c>apply</c>, <c>ci</c>, <c>aspire deploy</c>).</summary>
    Deploy,

    /// <summary>A failed deploy was rolled back, automatically or by <c>rollback</c>.</summary>
    Rollback
}

/// <summary>How the release ended.</summary>
public enum ReleaseOutcome
{
    Succeeded,

    /// <summary>The deploy failed its health gate and the previous images were restored.</summary>
    RolledBack,

    /// <summary>The release failed and nothing was restored.</summary>
    Failed
}

/// <summary>One image in a release, pinned by digest.</summary>
/// <param name="Service">The Aspire resource name.</param>
/// <param name="Repository">The repository the digest lives in, e.g. <c>ghcr.io/acme/api</c>.</param>
/// <param name="Tag">The tag it was pushed or promoted under.</param>
/// <param name="Digest">
/// The content digest, <c>sha256:</c> followed by 64 hex characters; null when the image isn't
/// pinned (an environment without a container registry deploys local images by tag).
/// </param>
public sealed record ReleaseImage(string Service, string Repository, string Tag, string? Digest);

/// <summary>
/// One entry in an environment's release history: who published, promoted or deployed which
/// digests, from which commit and environment, and when. Records are written once and never
/// changed, so they can serve as an audit trail; the control plane's approvals and audit log
/// build on them.
/// </summary>
/// <remarks>
/// This is a stored format covered by the 1.x stability policy: add optional properties
/// freely, but renaming or removing one, or changing what one means, needs a new
/// <c>StateFormat.Releases</c> version and an upgrade path in <c>ReleaseLog.Read</c>.
/// </remarks>
public sealed record ReleaseRecord
{
    /// <summary>Unique within the environment; also part of the record's key.</summary>
    public required string Id { get; init; }

    public required ReleaseKind Kind { get; init; }

    public required string Environment { get; init; }

    /// <summary>When the release finished, in UTC.</summary>
    public required DateTimeOffset At { get; init; }

    public ReleaseOutcome Outcome { get; init; } = ReleaseOutcome.Succeeded;

    /// <summary>
    /// The built images this release published, promoted or deployed. Empty for a
    /// <see cref="ReleaseKind.Rollback"/>, which restores earlier revisions (see <see cref="RolloutId"/>).
    /// </summary>
    public IReadOnlyList<ReleaseImage> Images { get; init; } = [];

    /// <summary>The image tag of the release, e.g. a commit SHA.</summary>
    public string? Tag { get; init; }

    /// <summary>The commit the images were built from, when known.</summary>
    public string? Commit { get; init; }

    /// <summary>For <see cref="ReleaseKind.Promote"/>: the environment the digests came from.</summary>
    public string? FromEnvironment { get; init; }

    /// <summary>Who ran it: a CI user name, or the local account name outside CI.</summary>
    public string? Actor { get; init; }

    /// <summary>Where <see cref="Actor"/> came from, e.g. <c>GITHUB_ACTOR</c> or <c>local</c>.</summary>
    public string? ActorSource { get; init; }

    /// <summary>A link to the CI run, when it ran in CI.</summary>
    public string? RunUrl { get; init; }

    /// <summary>The rollout journal of a health-gated deploy, or the rollout a rollback undid.</summary>
    public string? RolloutId { get; init; }

    /// <summary>One line on what happened, e.g. why a deploy failed.</summary>
    public string? Summary { get; init; }

    /// <summary>The Deployify version that wrote the record.</summary>
    public string? DeployifyVersion { get; init; }
}
