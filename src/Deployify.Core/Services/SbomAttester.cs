using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deployify.Core.Services;

/// <summary>
/// Generates an SBOM for a published image with syft and attaches it to the image in its
/// registry as a signed attestation with <c>cosign attest</c>. Both tools sign in to the
/// registry with the credentials <c>docker login</c> stored, including credential helpers.
/// </summary>
public sealed class SbomAttester(IProcessRunner? runner = null, ILogger? logger = null)
{
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_scanTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_attestTimeout = TimeSpan.FromMinutes(5);

    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// Throws when a tool the configuration needs isn't installed, so publish stops before
    /// building anything rather than after pushing images without SBOMs.
    /// </summary>
    public async Task EnsureToolsAsync(SbomConfig config, bool attest, CancellationToken ct = default)
    {
        var missing = new List<string>();
        if (!await _runner.IsAvailableAsync("syft", ["version"], s_probeTimeout, ct))
        {
            missing.Add("syft (https://github.com/anchore/syft#installation)");
        }

        if (attest && config.Attest && !await _runner.IsAvailableAsync("cosign", ["version"], s_probeTimeout, ct))
        {
            missing.Add("cosign (https://docs.sigstore.dev/cosign/system_config/installation/)");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"sbom.enabled is set, but {string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} not installed. " +
                "Install it, or set sbom.enabled to false.");
        }
    }

    /// <summary>The file an image's SBOM is written to: <c>{directory}/{name}.spdx.json</c> or <c>.cdx.json</c>.</summary>
    public static string FilePath(string directory, string name, SbomFormat format)
        => Path.Combine(directory, $"{name}{(format == SbomFormat.CycloneDx ? ".cdx.json" : ".spdx.json")}");

    /// <summary>
    /// The syft arguments that scan <paramref name="source"/> (<c>registry:repo@digest</c>,
    /// <c>docker:repo:tag</c> or <c>podman:repo:tag</c>) for <paramref name="platform"/> and write
    /// the SBOM to <paramref name="file"/>.
    /// </summary>
    public static IReadOnlyList<string> ScanArgs(string source, string platform, SbomFormat format, string file)
        => [source, "--platform", platform, "-o", $"{(format == SbomFormat.CycloneDx ? "cyclonedx-json" : "spdx-json")}={file}"];

    /// <summary>The <c>cosign attest</c> arguments that attach <paramref name="file"/> to <paramref name="image"/>.</summary>
    public static IReadOnlyList<string> AttestArgs(string image, SbomFormat format, string file, string? key)
    {
        List<string> args = ["attest", "--yes", "--type", format == SbomFormat.CycloneDx ? "cyclonedx" : "spdxjson", "--predicate", file];
        if (key is { Length: > 0 })
        {
            args.Add("--key");
            args.Add(key);
        }

        args.Add(image);
        return args;
    }

    /// <summary>
    /// The key as cosign expects it: <c>env:NAME</c> (Deployify's convention) becomes cosign's
    /// <c>env://NAME</c>, a relative key file is resolved against <paramref name="baseDirectory"/>,
    /// and URIs (<c>awskms://</c>, <c>k8s://</c>, …) pass through. Null or empty means keyless.
    /// </summary>
    public static string? ResolveKey(string? key, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        key = key.Trim();
        if (key.StartsWith("env:", StringComparison.Ordinal) && !key.StartsWith("env://", StringComparison.Ordinal))
        {
            return $"env://{key["env:".Length..]}";
        }

        return key.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(key)
            ? key
            : Path.GetFullPath(Path.Combine(baseDirectory, key));
    }

    /// <summary>Runs syft and returns the SBOM file it wrote.</summary>
    public async Task<string> GenerateAsync(
        string name, string source, string platform, SbomFormat format, string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var file = FilePath(directory, name, format);
        _logger.LogInformation("Generating the SBOM for '{Name}' from {Source}...", name, source);
        (await _runner.RunAsync(new ProcessSpec("syft", ScanArgs(source, platform, format, file))
            {
                Log = _logger,
                Timeout = s_scanTimeout,
                // No update check or version banner in CI logs.
                Env = new Dictionary<string, string?> { ["SYFT_CHECK_FOR_APP_UPDATE"] = "false" }
            }, ct))
            .EnsureSuccess($"syft scanning {source}");
        return file;
    }

    /// <summary>Attaches <paramref name="file"/> to <paramref name="image"/> (<c>repo@digest</c>) with cosign.</summary>
    public async Task AttestAsync(string name, string image, SbomFormat format, string file, string? key, CancellationToken ct = default)
    {
        _logger.LogInformation("Attesting the SBOM for '{Name}' to {Image} ({Signing})...",
            name, image, key is null ? "keyless" : "key");
        (await _runner.RunAsync(new ProcessSpec("cosign", AttestArgs(image, format, file, key))
            {
                Log = _logger,
                Timeout = s_attestTimeout
            }, ct))
            .EnsureSuccess($"cosign attesting the SBOM to {image}");
    }
}
