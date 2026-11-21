using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;

namespace Deployify.Plugins;

/// <summary>
/// Checks a file's Sigstore bundle with <c>cosign verify-blob</c>: that it was signed by the
/// expected signer and that the signature covers exactly these bytes. Keyless signatures are
/// also checked against the Rekor transparency log entry in the bundle.
/// </summary>
public sealed class BlobSignatureVerifier(IProcessRunner? runner = null)
{
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);

    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;

    /// <summary>Throws, naming <paramref name="why"/>, when cosign isn't installed.</summary>
    public async Task EnsureAvailableAsync(string why, CancellationToken ct = default)
    {
        if (!await _runner.IsAvailableAsync("cosign", ["version"], s_probeTimeout, ct))
        {
            throw new PluginException(
                $"{why}, which needs cosign to check signatures, but cosign isn't installed " +
                "(https://docs.sigstore.dev/cosign/system_config/installation/). Install cosign 3.0.3 or later.");
        }
    }

    /// <summary>The <c>cosign verify-blob</c> arguments; <paramref name="keyFile"/> stands in for an inline public key.</summary>
    public static IReadOnlyList<string> VerifyArgs(string file, string bundle, PluginSigner signer, string? keyFile)
    {
        List<string> args = ["verify-blob", "--bundle", bundle];
        if (!string.IsNullOrWhiteSpace(signer.PublicKey))
        {
            args.AddRange(["--key", keyFile ?? throw new ArgumentNullException(nameof(keyFile))]);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(signer.Identity))
            {
                args.AddRange(["--certificate-identity", signer.Identity.Trim()]);
            }
            else
            {
                args.AddRange(["--certificate-identity-regexp", signer.IdentityRegexp!.Trim()]);
            }

            args.AddRange(["--certificate-oidc-issuer", signer.Issuer!.Trim()]);
        }

        args.Add(file);
        return args;
    }

    /// <summary>
    /// Checks that <paramref name="bundle"/> signs <paramref name="file"/> and was made by
    /// <paramref name="signer"/>. Returns null when it does, else cosign's reason.
    /// </summary>
    public async Task<string?> VerifyAsync(string file, string bundle, PluginSigner signer, CancellationToken ct = default)
    {
        var problems = new List<string>(signer.Validate());
        if (problems.Count > 0)
        {
            // Never verify against a signer that would accept anyone.
            return "the expected signer is incomplete: " + string.Join(" ", problems);
        }

        string? keyFile = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(signer.PublicKey))
            {
                keyFile = Path.Combine(Path.GetTempPath(), $"deployify-key-{Guid.NewGuid():N}.pub");
                await File.WriteAllTextAsync(keyFile, signer.PublicKey.Trim() + "\n", ct);
            }

            var result = await _runner.RunAsync(
                new ProcessSpec("cosign", VerifyArgs(file, bundle, signer, keyFile)) { Timeout = s_timeout }, ct);
            if (result.Ok)
            {
                return null;
            }

            // cosign prints the reason on its last line; the rest is progress.
            var lines = result.ErrorOrOutput.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length > 0 ? lines[^1] : $"cosign verify-blob exited with {result.ExitCode}";
        }
        finally
        {
            if (keyFile is not null)
            {
                File.Delete(keyFile);
            }
        }
    }
}
