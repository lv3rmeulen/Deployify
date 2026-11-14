using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Deployify.Core.Models;

/// <summary>A pushed image's content digest and when it was published.</summary>
public sealed record PublishedImage(string Digest, DateTimeOffset PublishedAt);

/// <summary>
/// Digests of the images Deployify pushed, keyed by <c>repository:tag</c>. Generated artifacts
/// reference a built image as <c>repository@digest</c> when its tag has a digest here, so a deploy
/// runs exactly the image that was published and a rollback runs exactly the previous one, even
/// if the tag is pushed again. Kept in the environment's deployment state.
/// </summary>
public sealed partial class PublishedImages
{
    private readonly Dictionary<string, PublishedImage> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PublishedImage> _recorded = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, PublishedImage> All => _images;

    /// <summary>Entries <see cref="Record"/> added or changed in this run: the ones to save.</summary>
    public IReadOnlyDictionary<string, PublishedImage> Recorded => _recorded;

    /// <summary>True when <see cref="Record"/> added or changed an entry since the images were loaded.</summary>
    public bool HasChanges => _recorded.Count > 0;

    public static string Key(string repository, string tag) => $"{repository}:{tag}";

    /// <summary>True for a content digest such as <c>sha256:</c> followed by 64 hex characters.</summary>
    public static bool IsDigest(string? value) => value is not null && DigestPattern().IsMatch(value);

    /// <summary>Records the digest pushed for <paramref name="repository"/>:<paramref name="tag"/>. Ignores a value that isn't a digest.</summary>
    public void Record(string repository, string tag, string digest, DateTimeOffset publishedAt)
    {
        if (!IsDigest(digest) || string.IsNullOrEmpty(repository) || string.IsNullOrEmpty(tag))
        {
            return;
        }

        var key = Key(repository, tag);
        if (_images.TryGetValue(key, out var existing) && existing.Digest == digest)
        {
            return;
        }

        _images[key] = _recorded[key] = new PublishedImage(digest, publishedAt);
    }

    public bool TryGetDigest(string repository, string tag, out string digest)
    {
        if (_images.TryGetValue(Key(repository, tag), out var image))
        {
            digest = image.Digest;
            return true;
        }

        digest = string.Empty;
        return false;
    }

    /// <summary>
    /// Adds stored entries without marking a change. An entry recorded in this run wins over a
    /// stored one for the same tag: it is what was just pushed.
    /// </summary>
    public void Load(IReadOnlyDictionary<string, PublishedImage> stored)
    {
        foreach (var (key, image) in stored)
        {
            if (IsDigest(image.Digest))
            {
                _images.TryAdd(key, image);
            }
        }
    }

    /// <summary>
    /// <paramref name="entries"/> with at most <paramref name="perRepository"/> of the newest
    /// entries for each repository, so the record doesn't grow with every build.
    /// </summary>
    public static Dictionary<string, PublishedImage> Trim(
        IEnumerable<KeyValuePair<string, PublishedImage>> entries, int perRepository)
        => entries
            .GroupBy(e => e.Key[..e.Key.LastIndexOf(':')], StringComparer.Ordinal)
            .SelectMany(g => g.OrderByDescending(e => e.Value.PublishedAt).Take(perRepository))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    [GeneratedRegex("^sha(256:[0-9a-f]{64}|512:[0-9a-f]{128})$")]
    private static partial Regex DigestPattern();
}

/// <summary>Names the image each workload runs, the same way for every target.</summary>
public static class ServiceImage
{
    /// <summary>
    /// Where Deployify publishes a built image: <c>{containerRegistry}/{name}</c>, or just
    /// <c>{name}</c> (a local image) when no registry is configured.
    /// </summary>
    public static string Repository(string name, EnvironmentConfig config)
        => config.ContainerRegistry is { Length: > 0 } registry ? $"{registry.TrimEnd('/')}/{name}" : name;

    /// <summary>The service's image tag: its override, or the environment's tag.</summary>
    public static string Tag(string name, EnvironmentConfig config)
        => config.ServiceOverrides.TryGetValue(name, out var o) && o.ImageTag is not null ? o.ImageTag : config.ImageTag;

    /// <summary>
    /// The image a workload runs. For an image Deployify builds (a project or a Dockerfile
    /// build) that is its repository, pinned as <c>repository@digest</c> when publish recorded a
    /// digest for the service's tag; the tag is added later by <see cref="ImageReference.Format"/>
    /// when there is no digest. For any other container, the image from the manifest.
    /// </summary>
    public static string? For(string name, AspireResource resource, EnvironmentConfig config)
    {
        if (!resource.IsBuiltImage)
        {
            return resource.Image;
        }

        var repository = Repository(name, config);
        return config.PublishedImages.TryGetDigest(repository, Tag(name, config), out var digest)
            ? $"{repository}@{digest}"
            : repository;
    }
}
