using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Deployify.Core.Models;

namespace Deployify.Core.Services;

/// <summary>The <c>docker build</c> / <c>podman build</c> command for a Dockerfile build.</summary>
public static class DockerfileBuildCommands
{
    /// <summary>
    /// Throws when Deployify can't build the image: build secrets, build arguments whose value
    /// refers to another resource (such as a parameter), or a missing Dockerfile or context.
    /// </summary>
    public static void EnsureBuildable(string name, DockerfileBuild build)
    {
        if (build.Secrets.Count > 0)
        {
            throw new InvalidOperationException(
                $"'{name}' uses build secrets ({string.Join(", ", build.Secrets)}), which Deployify can't pass to the " +
                "build yet. Build and push this image yourself, or remove the secrets.");
        }

        var expressions = build.Args.Where(a => a.Value.Contains('{') && a.Value.Contains('}')).Select(a => a.Key).ToList();
        if (expressions.Count > 0)
        {
            throw new InvalidOperationException(
                $"'{name}' has build arguments that refer to other resources ({string.Join(", ", expressions)}), " +
                "which Deployify can't resolve at build time yet. Use literal values, or build and push this image yourself.");
        }

        if (!Directory.Exists(build.Context))
        {
            throw new InvalidOperationException($"'{name}': build context '{build.Context}' does not exist.");
        }

        if (!File.Exists(build.Dockerfile))
        {
            throw new InvalidOperationException($"'{name}': Dockerfile '{build.Dockerfile}' does not exist.");
        }
    }

    /// <summary>
    /// <c>build --platform os/arch -f Dockerfile -t reference [--build-arg K=V]... context</c>, one
    /// list item per argument.
    /// </summary>
    public static IReadOnlyList<string> BuildArgs(DockerfileBuild build, string reference, string os, string arch)
    {
        var args = new List<string> { "build", "--platform", Platform(os, arch), "-f", build.Dockerfile, "-t", reference };
        foreach (var (key, value) in build.Args.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            args.Add("--build-arg");
            args.Add($"{key}={value}");
        }

        args.Add(build.Context);
        return args;
    }

    /// <summary>
    /// The container platform for the .NET-style OS and architecture Deployify is configured with
    /// (<c>linux</c> + <c>x64</c> → <c>linux/amd64</c>), so built images match the projects' images.
    /// </summary>
    public static string Platform(string os, string arch)
        => $"{os.Trim().ToLowerInvariant()}/" + arch.Trim().ToLowerInvariant() switch
        {
            "x64" or "amd64" => "amd64",
            "arm64" or "aarch64" => "arm64",
            "arm" => "arm/v7",
            "x86" => "386",
            var other => other
        };
}
