using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deployify.Core.Services;

/// <summary>What <see cref="AttestationCopier.CopyAsync"/> did: how many attestations the source has, and how many it copied.</summary>
public readonly record struct AttestationCopyResult(int Found, int Copied);

/// <summary>
/// Copies the cosign attestations attached to an image (such as the SBOM publish attests) to the
/// same image in another repository, so they survive a promotion to another registry. Handles
/// both ways cosign stores them: Sigstore bundles as OCI referrers (cosign 3's default), which
/// are re-attached with <c>cosign attach bundle</c>, and the older <c>sha256-….att</c> tag, which
/// is copied as is with the image copy tool. Signatures stay valid: they cover the image digest,
/// which a promotion keeps. Attestations the target already has are skipped, so running it again
/// copies nothing twice.
/// </summary>
public sealed class AttestationCopier(IProcessRunner? runner = null, ILogger? logger = null)
{
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(5);

    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => _runner.IsAvailableAsync("cosign", ["version"], s_probeTimeout, ct);

    /// <summary>
    /// Copies the attestations of <paramref name="sourceRepository"/>@<paramref name="digest"/> to
    /// <paramref name="targetRepository"/>@<paramref name="digest"/>, which must already exist, then
    /// checks the target has every one of them. Throws when a step fails or one is missing after.
    /// </summary>
    public async Task<AttestationCopyResult> CopyAsync(
        ImageCopier copier, ImageCopyTool tool, string sourceRepository, string digest, string targetRepository,
        CancellationToken ct = default)
    {
        var source = $"{sourceRepository}@{digest}";
        var target = $"{targetRepository}@{digest}";

        var found = await DownloadAsync(source, ct);
        if (found.Count == 0)
        {
            return new AttestationCopyResult(0, 0);
        }

        var existing = (await DownloadAsync(target, ct)).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        var missing = found.Where(a => !existing.Contains(a.Key)).ToList();

        var bundles = missing.Where(a => a.IsBundle).ToList();
        if (bundles.Count > 0)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"deployify-attestations-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var args = new List<string> { "attach", "bundle" };
                for (var i = 0; i < bundles.Count; i++)
                {
                    var file = Path.Combine(directory, $"{i}.sigstore.json");
                    await File.WriteAllTextAsync(file, bundles[i].Json, ct);
                    args.Add("--bundle");
                    args.Add(file);
                }

                args.Add(target);
                _logger.LogDebug("Attaching {Count} attestation bundle(s) to {Target}.", bundles.Count, target);
                (await _runner.RunAsync(new ProcessSpec("cosign", args) { Log = _logger, Timeout = s_timeout }, ct))
                    .EnsureSuccess($"cosign attaching attestations to {target}");
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                    // A temp folder left behind is harmless.
                }
            }
        }

        if (missing.Any(a => !a.IsBundle))
        {
            var tag = AttestationTag(digest);
            _logger.LogDebug("Copying {Source}:{Tag} to {Target}.", sourceRepository, tag, targetRepository);
            await copier.CopyTagAsync(tool, sourceRepository, tag, targetRepository, ct);
        }

        if (missing.Count > 0)
        {
            var after = (await DownloadAsync(target, ct)).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
            var lost = found.Count(a => !after.Contains(a.Key));
            if (lost > 0)
            {
                throw new InvalidOperationException(
                    $"Copied the attestations of {source} to {target}, but {lost} of {found.Count} aren't there afterward. " +
                    "Check that the target registry supports OCI referrers (cosign 3 stores attestations as referrers).");
            }
        }

        return new AttestationCopyResult(found.Count, missing.Count);
    }

    /// <summary>The tag cosign's older format keeps attestations under: <c>sha256-&lt;hex&gt;.att</c>.</summary>
    public static string AttestationTag(string digest) => digest.Replace(':', '-') + ".att";

    /// <summary>
    /// One attestation <c>cosign download attestation</c> printed: a Sigstore bundle (referrer
    /// storage) or a DSSE envelope (tag storage). <see cref="Key"/> identifies it in either form:
    /// its payload and signature, which a copy doesn't change.
    /// </summary>
    internal sealed record Attestation(string Json, bool IsBundle, string Key);

    /// <summary>The attestations attached to <paramref name="image"/>, one per line of <c>cosign download attestation</c>.</summary>
    private async Task<IReadOnlyList<Attestation>> DownloadAsync(string image, CancellationToken ct)
    {
        var result = (await _runner.RunAsync(new ProcessSpec("cosign", ["download", "attestation", image])
            {
                Timeout = s_timeout
            }, ct))
            .EnsureSuccess($"cosign reading the attestations of {image}");
        return Parse(result.Stdout);
    }

    /// <summary>Parses <c>cosign download attestation</c> output. Lines that aren't attestations are skipped.</summary>
    internal static IReadOnlyList<Attestation> Parse(string output)
    {
        var attestations = new List<Attestation>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var isBundle = root.TryGetProperty("mediaType", out var mediaType) &&
                               mediaType.GetString()?.StartsWith("application/vnd.dev.sigstore.bundle", StringComparison.Ordinal) == true;
                var envelope = isBundle && root.TryGetProperty("dsseEnvelope", out var dsse) ? dsse : root;
                if (!envelope.TryGetProperty("payload", out var payload) ||
                    !envelope.TryGetProperty("signatures", out var signatures) ||
                    signatures.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var sig = signatures.EnumerateArray()
                    .Select(s => s.TryGetProperty("sig", out var v) ? v.GetString() : null)
                    .FirstOrDefault(s => s is not null);
                attestations.Add(new Attestation(line, isBundle, $"{payload.GetString()}|{sig}"));
            }
            catch (JsonException)
            {
                // Not JSON after all: cosign prints nothing else on stdout, but don't fail on it.
            }
        }

        return attestations;
    }
}
