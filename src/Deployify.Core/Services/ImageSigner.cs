using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deployify.Core.Services;

/// <summary>
/// Which signatures <c>--require-signature</c> accepts, resolved from a <see cref="SigningConfig"/>:
/// a public key (or KMS key) to verify with, or for keyless signatures the certificate identity
/// and OIDC issuer that must have signed. <see cref="Arguments"/> are the <c>cosign verify</c>
/// flags, without the image.
/// </summary>
public sealed record SignaturePolicy(IReadOnlyList<string> Arguments, string Description);

/// <summary>
/// Signs pushed images with <c>cosign sign</c> and verifies their signatures with
/// <c>cosign verify</c>, always by digest: a tag can be moved to another image between the check
/// and the deploy. cosign signs in to the registry with the credentials <c>docker login</c> stored.
/// </summary>
public sealed class ImageSigner(IProcessRunner? runner = null, ILogger? logger = null)
{
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(5);

    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => _runner.IsAvailableAsync("cosign", ["version"], s_probeTimeout, ct);

    /// <summary>Throws, naming <paramref name="why"/>, when cosign isn't installed.</summary>
    public async Task EnsureAvailableAsync(string why, CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct))
        {
            throw new InvalidOperationException(
                $"{why}, but cosign isn't installed (https://docs.sigstore.dev/cosign/system_config/installation/). " +
                "Install cosign 3.0.3 or later.");
        }
    }

    /// <summary>True when the config says how to verify: a key, a public key, or a keyless identity or issuer.</summary>
    public static bool HasVerificationSettings(SigningConfig config) =>
        !string.IsNullOrWhiteSpace(config.Key) || !string.IsNullOrWhiteSpace(config.PublicKey) ||
        !string.IsNullOrWhiteSpace(config.Identity) || !string.IsNullOrWhiteSpace(config.IdentityRegexp) ||
        !string.IsNullOrWhiteSpace(config.Issuer) || !string.IsNullOrWhiteSpace(config.IssuerRegexp);

    /// <summary>
    /// The images the environment builds that are pinned by digest, as <c>repo@digest</c> per
    /// service: what <c>--require-signature</c> verifies. Check that none is unpinned first
    /// (<see cref="Deployments.ImagePromotion.Unpinned"/>); containers Deployify doesn't build aren't included.
    /// </summary>
    public static IReadOnlyList<(string Service, string Image)> BuiltImages(AspireManifest manifest, EnvironmentConfig config)
        => [.. manifest.Resources
            .Where(r => r.Value.IsBuiltImage)
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => (Service: r.Key, Image: ServiceImage.For(r.Key, r.Value, config)!))
            .Where(r => r.Image.Contains('@', StringComparison.Ordinal))];

    /// <summary>The <c>cosign sign</c> arguments that sign <paramref name="image"/> (<c>repo@digest</c>).</summary>
    public static IReadOnlyList<string> SignArgs(string image, string? key)
    {
        List<string> args = ["sign", "--yes"];
        if (key is { Length: > 0 })
        {
            args.Add("--key");
            args.Add(key);
        }

        args.Add(image);
        return args;
    }

    /// <summary>The <c>cosign verify</c> arguments that check <paramref name="image"/> against <paramref name="policy"/>.</summary>
    public static IReadOnlyList<string> VerifyArgs(string image, SignaturePolicy policy)
        => ["verify", .. policy.Arguments, image];

    /// <summary>
    /// The signatures <paramref name="config"/> trusts. A public key wins; a KMS signing key
    /// verifies as itself; otherwise keyless, which needs both an identity and an issuer, because
    /// without them any Sigstore signature would pass. Throws, saying what to set, when the config
    /// can't verify anything. Key files are resolved against <paramref name="baseDirectory"/>.
    /// </summary>
    public static SignaturePolicy ResolvePolicy(SigningConfig config, string baseDirectory, string environment)
    {
        if (!string.IsNullOrWhiteSpace(config.PublicKey))
        {
            var key = SbomAttester.ResolveKey(config.PublicKey, baseDirectory)!;
            return new SignaturePolicy(["--key", key], $"key {config.PublicKey.Trim()}");
        }

        if (config.Key is { } signingKey && !string.IsNullOrWhiteSpace(signingKey))
        {
            signingKey = signingKey.Trim();
            if (signingKey.Contains("://", StringComparison.Ordinal) && !signingKey.StartsWith("env:", StringComparison.Ordinal))
            {
                // A KMS key verifies with its own public half.
                return new SignaturePolicy(["--key", signingKey], $"key {signingKey}");
            }

            throw new InvalidOperationException(
                $"{environment} signs with signing.key {signingKey}, a private key, so signatures are verified with its " +
                "public key: set signing.publicKey (for a key from 'cosign generate-key-pair', the cosign.pub written next to it).");
        }

        var hasIdentity = !string.IsNullOrWhiteSpace(config.Identity) || !string.IsNullOrWhiteSpace(config.IdentityRegexp);
        var hasIssuer = !string.IsNullOrWhiteSpace(config.Issuer) || !string.IsNullOrWhiteSpace(config.IssuerRegexp);
        if (!hasIdentity || !hasIssuer)
        {
            throw new InvalidOperationException(
                $"{environment} has no settings to verify image signatures with. For keyless signatures set signing.identity " +
                "(or signing.identityRegexp) and signing.issuer (or signing.issuerRegexp) to who must have signed; " +
                "for signatures made with a key, set signing.publicKey.");
        }

        var args = new List<string>();
        var who = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.Identity))
        {
            args.AddRange(["--certificate-identity", config.Identity.Trim()]);
            who.Add(config.Identity.Trim());
        }
        else
        {
            args.AddRange(["--certificate-identity-regexp", config.IdentityRegexp!.Trim()]);
            who.Add($"/{config.IdentityRegexp.Trim()}/");
        }

        if (!string.IsNullOrWhiteSpace(config.Issuer))
        {
            args.AddRange(["--certificate-oidc-issuer", config.Issuer.Trim()]);
            who.Add($"issued by {config.Issuer.Trim()}");
        }
        else
        {
            args.AddRange(["--certificate-oidc-issuer-regexp", config.IssuerRegexp!.Trim()]);
            who.Add($"issued by /{config.IssuerRegexp.Trim()}/");
        }

        return new SignaturePolicy(args, $"keyless, {string.Join(", ", who)}");
    }

    /// <summary>Signs <paramref name="image"/> (<c>repo@digest</c>) with cosign.</summary>
    public async Task SignAsync(string name, string image, string? key, CancellationToken ct = default)
    {
        _logger.LogInformation("Signing '{Name}' ({Image}, {Signing})...", name, image, key is null ? "keyless" : "key");
        (await _runner.RunAsync(new ProcessSpec("cosign", SignArgs(image, key))
            {
                Log = _logger,
                Timeout = s_timeout
            }, ct))
            .EnsureSuccess($"cosign signing {image}");
    }

    /// <summary>
    /// Checks that <paramref name="image"/> (<c>repo@digest</c>) has a signature <paramref name="policy"/>
    /// accepts. Returns null when it does, else cosign's reason.
    /// </summary>
    public async Task<string?> VerifyAsync(string image, SignaturePolicy policy, CancellationToken ct = default)
    {
        _logger.LogDebug("Verifying the signature of {Image} ({Policy}).", image, policy.Description);
        var result = await _runner.RunAsync(new ProcessSpec("cosign", VerifyArgs(image, policy)) { Timeout = s_timeout }, ct);
        if (result.Ok)
        {
            return null;
        }

        var reason = result.ErrorOrOutput.Trim();
        // cosign prints the usage error on its last line; the rest is progress.
        var lines = reason.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : $"cosign verify exited with {result.ExitCode}";
    }
}
