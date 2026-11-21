using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// <c>--require-signature</c> on <c>apply</c> and <c>ci</c>: before deploying, every image the
/// environment builds must be pinned by digest and carry a cosign signature that the environment's
/// <c>signing</c> settings trust. Images Deployify doesn't build (redis, postgres and other
/// containers from the manifest) aren't checked; an admission policy in the cluster covers those.
/// </summary>
internal static class SignatureCheck
{
    public const string Flag = "--require-signature";

    /// <summary>
    /// True (after printing why) when the deploy must stop: an image isn't pinned, the signing
    /// settings can't verify anything, cosign is missing, or a signature is missing or untrusted.
    /// </summary>
    public static async Task<bool> BlocksAsync(AspireManifest manifest, EnvironmentConfig config, string environment,
        string appHostDirectory, CancellationToken ct, ImageSigner? signer = null)
    {
        // A signature is checked for a digest; a tag could move to another image before the deploy.
        if (PlanOutput.UnpinnedBlocks(manifest, config, requireDigest: true, Flag))
        {
            return true;
        }

        var images = ImageSigner.BuiltImages(manifest, config);
        if (images.Count == 0)
        {
            return false;
        }

        SignaturePolicy policy;
        try
        {
            policy = ImageSigner.ResolvePolicy(config.Signing, appHostDirectory, environment);
        }
        catch (InvalidOperationException ex)
        {
            ConsoleDeploymentReporter.Instance.Error($"{Flag} is set, but {ex.Message}");
            return true;
        }

        signer ??= new ImageSigner();
        if (!await signer.IsAvailableAsync(ct))
        {
            ConsoleDeploymentReporter.Instance.Error(
                $"{Flag} is set, but cosign isn't installed. Install cosign 3.0.3 or later.");
            return true;
        }

        var failures = await VerifyAsync(signer, images, policy, ct);
        if (failures > 0)
        {
            ConsoleDeploymentReporter.Instance.Error(
                $"{failures} of {images.Count} image(s) have no signature {environment} trusts ({policy.Description}), so nothing was deployed.");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Verifies each image (<c>repo@digest</c>, labeled by service) and prints a line per image.
    /// Returns how many failed.
    /// </summary>
    public static async Task<int> VerifyAsync(ImageSigner signer, IReadOnlyList<(string Service, string Image)> images,
        SignaturePolicy policy, CancellationToken ct)
    {
        AnsiConsole.MarkupLineInterpolated($"[dim]Verifying signatures ({policy.Description})...[/]");
        var failures = 0;
        foreach (var (service, image) in images)
        {
            var problem = await ConsoleStatus.RunAsync($"Verifying {service}...", () => signer.VerifyAsync(image, policy, ct));
            if (problem is null)
            {
                AnsiConsole.MarkupLineInterpolated($"  [green]✓[/] {service}: {image}");
            }
            else
            {
                failures++;
                AnsiConsole.MarkupLineInterpolated($"  [red]✗[/] {service}: {image}: {problem}");
            }
        }

        return failures;
    }
}
