using System.Text.Json.Serialization;

namespace Deployify.Core.Models;

/// <summary>The SBOM document format: what syft writes and the predicate type cosign attests.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SbomFormat>))]
public enum SbomFormat
{
    /// <summary>SPDX 2.3 JSON (<c>syft -o spdx-json</c>, attested as <c>--type spdxjson</c>).</summary>
    Spdx,

    /// <summary>CycloneDX JSON (<c>syft -o cyclonedx-json</c>, attested as <c>--type cyclonedx</c>).</summary>
    CycloneDx
}

/// <summary>
/// Software bill of materials for the images Deployify publishes. When enabled, publish
/// generates an SBOM for every image it built with syft, writes it under
/// <c>{outputPath}/sbom/</c>, and attaches it to the pushed image as a signed in-toto
/// attestation with <c>cosign attest</c>.
/// </summary>
public sealed class SbomConfig
{
    /// <summary>Generate an SBOM for every image publish builds. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>The document format. Defaults to <see cref="SbomFormat.Spdx"/>.</summary>
    public SbomFormat Format { get; set; } = SbomFormat.Spdx;

    /// <summary>
    /// Attach the SBOM to each pushed image as a cosign attestation. Defaults to true. Images
    /// that stay in the local store (no <c>containerRegistry</c>) only get the SBOM file.
    /// </summary>
    public bool Attest { get; set; } = true;

    /// <summary>
    /// The cosign signing key: a key file (relative to the AppHost directory), a KMS URI such as
    /// <c>awskms://…</c>, <c>azurekms://…</c>, <c>gcpkms://…</c> or <c>hashivault://…</c>, or
    /// <c>env:NAME</c> for a PEM key held in environment variable NAME. An encrypted key's
    /// password is read by cosign from <c>COSIGN_PASSWORD</c>. Null signs keyless with
    /// Sigstore (an OIDC identity, such as the CI job's, recorded in the Rekor transparency log).
    /// </summary>
    public string? Key { get; set; }
}
