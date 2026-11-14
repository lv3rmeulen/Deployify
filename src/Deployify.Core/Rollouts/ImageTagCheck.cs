using System;
using System.Collections.Generic;
using System.Linq;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;

namespace Deployify.Core.Rollouts;

/// <summary>
/// Finds services whose image reference can point at different images over time. Rolling
/// such a service back re-deploys the same reference, which by then may already resolve to
/// the new, broken image.
/// </summary>
public static class ImageTagCheck
{
    public static IReadOnlyList<string> MutableImages(IEnumerable<ServiceExplanation> services)
        => [.. services.Where(s => IsMutable(s.Image, s.ImageTag)).Select(s => s.Name)];

    public static bool IsMutable(string? image, string? tag)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            // No image (e.g. a raw executable): nothing to say about tags.
            return false;
        }

        var reference = ImageReference.Format(image, tag);
        if (reference.Contains('@'))
        {
            return false; // pinned by digest
        }

        var lastSegment = reference[(reference.LastIndexOf('/') + 1)..];
        var colon = lastSegment.LastIndexOf(':');
        return colon < 0 || IsMutableTag(lastSegment[(colon + 1)..]);
    }

    /// <summary>True for a missing tag or <c>latest</c>: tags that every build overwrites.</summary>
    public static bool IsMutableTag(string? tag)
        => string.IsNullOrWhiteSpace(tag) || string.Equals(tag.Trim(), "latest", StringComparison.OrdinalIgnoreCase);
}
