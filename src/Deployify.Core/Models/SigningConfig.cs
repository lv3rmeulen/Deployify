namespace Deployify.Core.Models;

/// <summary>
/// Signing the images Deployify publishes with cosign, and which signatures to trust when a
/// command verifies them (<c>apply --require-signature</c>, <c>promote --require-signature</c>).
/// With <see cref="Enabled"/>, publish signs every image it pushes, by digest, with
/// <c>cosign sign</c>. The verification settings work without <see cref="Enabled"/>, so an
/// environment that only deploys promoted images can require signatures it never makes.
/// </summary>
public sealed class SigningConfig
{
    /// <summary>Sign every image publish pushes to a registry. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The cosign signing key: a key file (relative to the AppHost directory), a KMS URI such as
    /// <c>awskms://…</c>, <c>azurekms://…</c>, <c>gcpkms://…</c> or <c>hashivault://…</c>, or
    /// <c>env:NAME</c> for a PEM key held in environment variable NAME. An encrypted key's
    /// password is read by cosign from <c>COSIGN_PASSWORD</c>. Null signs keyless with Sigstore
    /// (an OIDC identity, such as the CI job's, recorded in the Rekor transparency log).
    /// SBOM attestations use this key too unless <see cref="SbomConfig.Key"/> is set.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// The key signatures are verified with: a public key file (relative to the AppHost
    /// directory), a KMS URI, or <c>env:NAME</c>. Defaults to <see cref="Key"/> when that is a
    /// KMS URI, which holds both halves. A key file or <c>env:</c> key needs its public key here.
    /// </summary>
    public string? PublicKey { get; set; }

    /// <summary>
    /// Keyless verification: the exact identity in the signing certificate, such as
    /// <c>https://github.com/my-org/my-app/.github/workflows/release.yml@refs/heads/main</c>.
    /// Keyless verification needs this or <see cref="IdentityRegexp"/>, and an issuer.
    /// </summary>
    public string? Identity { get; set; }

    /// <summary>Keyless verification: a regular expression the certificate identity must match.</summary>
    public string? IdentityRegexp { get; set; }

    /// <summary>
    /// Keyless verification: the OIDC issuer that vouched for the identity, such as
    /// <c>https://token.actions.githubusercontent.com</c>.
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>Keyless verification: a regular expression the OIDC issuer must match.</summary>
    public string? IssuerRegexp { get; set; }
}
