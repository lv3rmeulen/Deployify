using System;

namespace Deployify.Target.DigitalOcean;

/// <summary>
/// Turns an image reference into an App Platform image source. App Platform pulls from three
/// registries only: DigitalOcean Container Registry, Docker Hub and GitHub Container Registry.
/// </summary>
public static class DoImage
{
    public const string DocrHost = "registry.digitalocean.com";

    /// <summary>
    /// The image source for <paramref name="image"/> (which may carry a tag or a digest), or
    /// null with an explanation in <paramref name="error"/> when App Platform can't pull from
    /// its registry. <paramref name="defaultTag"/> applies when the reference has neither.
    /// </summary>
    public static ImageSourceSpec? ToSource(string image, string? defaultTag, out string? error)
    {
        error = null;
        var (name, tag, digest) = Split(image);
        if (tag is null && digest is null)
        {
            tag = string.IsNullOrEmpty(defaultTag) ? "latest" : defaultTag;
        }

        var segments = name.Split('/');
        var hasHost = segments.Length > 1 && (segments[0].Contains('.') || segments[0].Contains(':') || segments[0] == "localhost");
        var host = hasHost ? segments[0].ToLowerInvariant() : "docker.io";
        var path = hasHost ? string.Join('/', segments[1..]) : name;

        switch (host)
        {
            case DocrHost:
            {
                // registry.digitalocean.com/<registry>/<repository>: the registry is implied by the account.
                var slash = path.IndexOf('/');
                if (slash < 0)
                {
                    error = $"'{image}' names a DOCR registry but no repository; use {DocrHost}/<registry>/<repository>.";
                    return null;
                }

                return new ImageSourceSpec { RegistryType = "DOCR", Repository = path[(slash + 1)..], Tag = tag, Digest = digest };
            }

            case "docker.io" or "index.docker.io" or "registry-1.docker.io":
            {
                var slash = path.LastIndexOf('/');
                var registry = slash < 0 ? "library" : path[..slash];
                return new ImageSourceSpec
                {
                    RegistryType = "DOCKER_HUB", Registry = registry, Repository = path[(slash + 1)..], Tag = tag, Digest = digest
                };
            }

            case "ghcr.io":
            {
                var slash = path.LastIndexOf('/');
                if (slash < 0)
                {
                    error = $"'{image}' has no owner; use ghcr.io/<owner>/<repository>.";
                    return null;
                }

                return new ImageSourceSpec
                {
                    RegistryType = "GHCR", Registry = path[..slash], Repository = path[(slash + 1)..], Tag = tag, Digest = digest
                };
            }

            default:
                error = $"'{image}' is in registry '{host}'. App Platform only pulls from DigitalOcean Container Registry " +
                        $"({DocrHost}), Docker Hub and GitHub Container Registry (ghcr.io); copy the image to one of them.";
                return null;
        }
    }

    /// <summary>Splits <c>name[:tag][@digest]</c>. A ':' before the last '/' is a registry port, not a tag.</summary>
    internal static (string Name, string? Tag, string? Digest) Split(string image)
    {
        string? digest = null;
        var at = image.IndexOf('@');
        if (at >= 0)
        {
            digest = image[(at + 1)..];
            image = image[..at];
        }

        string? tag = null;
        var colon = image.LastIndexOf(':');
        if (colon > image.LastIndexOf('/'))
        {
            tag = image[(colon + 1)..];
            image = image[..colon];
        }

        // A digest pins the image; the tag next to it is only a label, and App Platform takes one or the other.
        return (image, digest is null ? tag : null, digest);
    }

    /// <summary>The repository name without registry or namespace: <c>postgres</c> for <c>docker.io/library/postgres:17</c>.</summary>
    public static string ShortName(string image)
    {
        var (name, _, _) = Split(image);
        return name[(name.LastIndexOf('/') + 1)..].ToLowerInvariant();
    }

    /// <summary>The leading number of the tag (<c>17</c> for <c>postgres:17.6-alpine</c>), or null.</summary>
    public static string? MajorVersion(string image)
    {
        var (_, tag, _) = Split(image);
        if (tag is null)
        {
            return null;
        }

        var end = 0;
        while (end < tag.Length && char.IsAsciiDigit(tag[end]))
        {
            end++;
        }

        return end == 0 ? null : tag[..end];
    }

    public static bool IsDocr(string? registry)
        => registry is not null && registry.StartsWith(DocrHost + "/", StringComparison.OrdinalIgnoreCase);
}
