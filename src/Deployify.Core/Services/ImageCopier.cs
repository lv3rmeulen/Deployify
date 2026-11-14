using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deployify.Core.Services;

/// <summary>The program that copies an image between registries.</summary>
public enum ImageCopyTool
{
    /// <summary><c>crane copy</c> (go-containerregistry).</summary>
    Crane,

    /// <summary><c>skopeo copy --all --preserve-digests</c>.</summary>
    Skopeo,

    /// <summary><c>docker buildx imagetools create</c>.</summary>
    Docker
}

/// <summary>
/// Copies a published image from one registry to another by digest, without pulling it into
/// a local image store and without changing it: the target registry ends up with a manifest of
/// the same digest. Uses crane, skopeo or docker buildx, which sign in to the registries with
/// the credentials <c>docker login</c> stored (including credential helpers).
/// </summary>
public sealed class ImageCopier(IProcessRunner? runner = null, ILogger? logger = null)
{
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>The order tools are tried in: the ones that always keep the digest come first.</summary>
    public static IReadOnlyList<ImageCopyTool> PreferenceOrder { get; } =
        [ImageCopyTool.Crane, ImageCopyTool.Skopeo, ImageCopyTool.Docker];

    /// <summary>The first installed tool in <see cref="PreferenceOrder"/>, or null when there is none.</summary>
    public async Task<ImageCopyTool?> DetectAsync(CancellationToken ct = default)
    {
        foreach (var tool in PreferenceOrder)
        {
            if (await IsAvailableAsync(tool, ct))
            {
                return tool;
            }
        }

        return null;
    }

    public Task<bool> IsAvailableAsync(ImageCopyTool tool, CancellationToken ct = default)
        => tool switch
        {
            ImageCopyTool.Crane => _runner.IsAvailableAsync("crane", ["version"], s_probeTimeout, ct),
            ImageCopyTool.Skopeo => _runner.IsAvailableAsync("skopeo", ["--version"], s_probeTimeout, ct),
            _ => _runner.IsAvailableAsync("docker", ["buildx", "version"], s_probeTimeout, ct)
        };

    /// <summary>True when <paramref name="repository"/> has a manifest with <paramref name="digest"/>.</summary>
    public async Task<bool> ExistsAsync(ImageCopyTool tool, string repository, string digest, CancellationToken ct = default)
    {
        var reference = $"{repository}@{digest}";
        var spec = tool switch
        {
            ImageCopyTool.Crane => new ProcessSpec("crane", ["manifest", reference]),
            ImageCopyTool.Skopeo => new ProcessSpec("skopeo", ["inspect", "--raw", $"docker://{reference}"]),
            _ => new ProcessSpec("docker", ["buildx", "imagetools", "inspect", "--raw", reference])
        };

        // A missing manifest and a registry that can't be reached look the same here; the
        // copy that follows reports the real error.
        return (await _runner.RunAsync(spec with { Timeout = TimeSpan.FromMinutes(1) }, ct)).Ok;
    }

    /// <summary>
    /// Copies <paramref name="sourceRepository"/>@<paramref name="digest"/> to
    /// <paramref name="targetRepository"/>:<paramref name="targetTag"/>, then checks the target
    /// has a manifest with that same digest. Throws when the copy fails or changed the digest.
    /// </summary>
    public async Task CopyAsync(
        ImageCopyTool tool, string sourceRepository, string digest, string targetRepository, string targetTag,
        CancellationToken ct = default)
    {
        var source = $"{sourceRepository}@{digest}";
        var target = $"{targetRepository}:{targetTag}";
        var spec = tool switch
        {
            ImageCopyTool.Crane => new ProcessSpec("crane", ["copy", source, target]),
            ImageCopyTool.Skopeo => new ProcessSpec("skopeo",
                ["copy", "--all", "--preserve-digests", $"docker://{source}", $"docker://{target}"]),
            _ => new ProcessSpec("docker", ["buildx", "imagetools", "create", "--tag", target, source])
        };

        _logger.LogDebug("Copying {Source} to {Target}.", source, target);
        (await _runner.RunAsync(spec with { Log = _logger }, ct)).EnsureSuccess($"{spec.FileName} copying {source} to {target}");

        if (!await ExistsAsync(tool, targetRepository, digest, ct))
        {
            throw new InvalidOperationException(
                $"Copied {source} to {target}, but {targetRepository} has no manifest with digest {digest}: " +
                $"{spec.FileName} changed the image. Install crane or skopeo, which keep the digest, and promote again.");
        }
    }

    /// <summary>Parses a tool name as used on the command line: crane, skopeo or docker.</summary>
    public static bool TryParse(string? value, out ImageCopyTool tool)
        => Enum.TryParse(value?.Trim(), ignoreCase: true, out tool) && Enum.IsDefined(tool);
}
