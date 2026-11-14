using System;
using System.Collections.Generic;
using System.Linq;
using Deployify.Core.Models;

namespace Deployify.Core.Deployments;

/// <summary>
/// One built image to promote: the image the source environment published and the reference
/// the target environment will run it as. <see cref="Problem"/> is set when it can't be promoted.
/// </summary>
public sealed record ImagePromotionEntry(
    string Service,
    string SourceRepository,
    string SourceTag,
    string TargetRepository,
    string TargetTag,
    string? Digest,
    string? Problem)
{
    public bool CanPromote => Problem is null && Digest is not null;

    /// <summary>True when the target already pins this tag to the same digest.</summary>
    public bool AlreadyPromoted { get; init; }
}

/// <summary>
/// Build once, deploy many times: makes another environment deploy exactly the images an
/// environment already published, by digest, without building them again. Only images in the
/// same registry are promoted for now; both environments must name the image the same way.
/// </summary>
public static class ImagePromotion
{
    /// <summary>
    /// What promoting the manifest's built images from <paramref name="source"/> to
    /// <paramref name="target"/> would record. Both configs must have their published images
    /// loaded, and the image tag to promote already applied.
    /// </summary>
    public static IReadOnlyList<ImagePromotionEntry> Plan(
        AspireManifest manifest, EnvironmentConfig source, string sourceName, EnvironmentConfig target)
    {
        var entries = new List<ImagePromotionEntry>();
        foreach (var (name, resource) in manifest.Resources.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            if (!resource.IsBuiltImage)
            {
                continue;
            }

            var sourceRepository = ServiceImage.Repository(name, source);
            var sourceTag = ServiceImage.Tag(name, source);
            var targetRepository = ServiceImage.Repository(name, target);
            var targetTag = ServiceImage.Tag(name, target);

            source.PublishedImages.TryGetDigest(sourceRepository, sourceTag, out var digest);
            string? problem = null;
            if (string.IsNullOrEmpty(digest))
            {
                digest = null;
                problem = string.IsNullOrEmpty(source.ContainerRegistry)
                    ? $"{sourceName} has no container registry, so its images aren't recorded by digest"
                    : $"{sourceName} has no published digest for {sourceRepository}:{sourceTag}; publish it there first";
            }
            else if (!string.Equals(sourceRepository, targetRepository, StringComparison.Ordinal))
            {
                problem = $"the target runs it from {targetRepository}, a different registry than {sourceRepository}; " +
                          "copying images between registries isn't supported yet";
            }

            var already = digest is not null &&
                          target.PublishedImages.TryGetDigest(targetRepository, targetTag, out var existing) &&
                          existing == digest;

            entries.Add(new ImagePromotionEntry(name, sourceRepository, sourceTag, targetRepository, targetTag, digest, problem)
            {
                AlreadyPromoted = already
            });
        }

        return entries;
    }

    /// <summary>
    /// Records every promotable entry in the target's published images, so the target's
    /// artifacts reference those digests. Returns how many entries changed.
    /// </summary>
    public static int Record(IEnumerable<ImagePromotionEntry> entries, EnvironmentConfig target, DateTimeOffset now)
    {
        var count = 0;
        foreach (var entry in entries)
        {
            if (entry is { CanPromote: true, AlreadyPromoted: false })
            {
                target.PublishedImages.Record(entry.TargetRepository, entry.TargetTag, entry.Digest!, now);
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The built images that the environment would deploy by tag rather than by digest, as
    /// <c>repository:tag</c> per service. Empty when every built image is pinned.
    /// </summary>
    public static IReadOnlyList<(string Service, string Reference)> Unpinned(AspireManifest manifest, EnvironmentConfig config)
        => [.. manifest.Resources
            .Where(r => r.Value.IsBuiltImage)
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => (Service: r.Key, Repository: ServiceImage.Repository(r.Key, config), Tag: ServiceImage.Tag(r.Key, config)))
            .Where(r => !config.PublishedImages.TryGetDigest(r.Repository, r.Tag, out _))
            .Select(r => (r.Service, $"{r.Repository}:{r.Tag}"))];
}
