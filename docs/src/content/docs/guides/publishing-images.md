---
title: "Publishing images"
description: "How Deployify builds and pushes project and Dockerfile images, pins them by digest, and attests their SBOMs."
---

## `publish`

Builds the images for your own code and pushes them to `containerRegistry`: .NET projects, and
resources built from a Dockerfile. Containers that already name an image (`redis:7`) are pulled
as they are.

```bash
deployify publish
deployify publish --image-tag v1.2.3
deployify publish --container-runtime podman
deployify publish --dry-run   # lists what would be built
```

Each image is named after its resource under the registry: with `containerRegistry` set to
`registry.example.com/myapp`, the `api` project becomes `registry.example.com/myapp/api`. Without
a registry, images stay in the local Docker or Podman store.

## Dockerfile builds

Resources added with `AddDockerfile`, or a container with `WithDockerfile`, are built with
`docker build` (or `podman build`) and pushed like projects. Deployify builds for the platform
set by `containerOs` and `containerArch` (for example `linux/amd64`) and passes the resource's
build arguments as `--build-arg`.

Two things it can't build yet, and stops on before building anything:

- Build secrets (`WithBuildSecret`).
- Build arguments whose value comes from another resource, such as a parameter
  (`WithBuildArg("VERSION", versionParameter)`). Literal values work.

Build and push those images yourself and give the resource the resulting image instead; see
[known limitations](/Deployify/reference/known-limitations/#dockerfile-build-secrets) for an example.

## Pinning images by digest

Every image Deployify pushes is recorded with its content digest, and the artifacts generated
afterwards reference it as `registry.example.com/myapp/api@sha256:…` instead of by tag. A
deploy runs exactly the image that was published, and a rollback runs exactly the previous
one, even if the tag is pushed again later.

The digests are kept in the environment's deployment state (`images.json`, next to the
inventory), locally or in the [remote state backend](/Deployify/concepts/state/#remote-state), so a later
`apply`, a rollback or a drift check in another CI job finds them. A digest is used only for the
exact repository and tag it was published as: change the tag and the artifacts reference the new
tag until it is published. Images in the local store (no registry) are referenced by tag.

## SBOMs and attestations

With `sbom.enabled`, `publish` (and the publish step of `ci` and `aspire deploy`) generates a
software bill of materials for every image it built and attaches it to the pushed image as a
signed attestation:

```json
"sbom": {
  "enabled": true,
  "format": "Spdx",
  "attest": true,
  "key": null
}
```

For each project and Dockerfile build, after all images are pushed, Deployify:

1. Runs [syft](https://github.com/anchore/syft) on the pushed image, read from the registry by
   digest (`registry:registry.example.com/myapp/api@sha256:…`), so the SBOM describes exactly
   the image that was pushed. It is written to `{outputPath}/sbom/api.spdx.json`.
2. Runs [cosign](https://docs.sigstore.dev/cosign/) `attest` to attach the SBOM to that digest
   as an in-toto attestation (`--type spdxjson`, or `cyclonedx`), stored in the same repository
   next to the image.

| Setting | Default | |
|---|---|---|
| `enabled` | `false` | Generate SBOMs at publish. |
| `format` | `Spdx` | `Spdx` (SPDX JSON) or `CycloneDx` (CycloneDX JSON). |
| `attest` | `true` | Attach the SBOM to the pushed image. `false` only writes the file. |
| `key` | none | cosign signing key. Empty signs keyless. |

syft and cosign must be installed; `publish` checks for them before building anything and stops
if one is missing (cosign only when attesting to a registry). Both sign in to the registry with
the credentials `docker login` stored, including credential helpers. An image that stays in the
local store (no `containerRegistry`) is scanned from Docker or Podman and gets only the SBOM
file. If the pushed digest couldn't be read, the SBOM is written but not attested, with a
warning, rather than attached to whatever the tag points to. A failing `cosign attest` fails
the publish; the images are already pushed by then, so a second run rebuilds and attests again.

### Signing

Without a `key`, cosign signs keyless with Sigstore: it gets a short-lived certificate for an
OIDC identity and records the signature in the public [Rekor](https://docs.sigstore.dev/logging/overview/)
transparency log. On a CI runner that identity is the job's (on GitHub Actions, give the job
`id-token: write`); on a workstation cosign opens a browser to sign in. The Rekor entry is
public and names the image digest, so use a key for images whose names must stay private.

`key` takes anything `cosign attest --key` does:

- a key file, such as `cosign.key`, relative to the AppHost directory;
- a KMS key: `awskms://…`, `azurekms://…`, `gcpkms://…` or `hashivault://…`;
- `env:COSIGN_PRIVATE_KEY`, a PEM key in an environment variable (passed to cosign as
  `env://COSIGN_PRIVATE_KEY`).

cosign reads an encrypted key's password from `COSIGN_PASSWORD`.

### Verifying

```bash
# Keyless, signed by a GitHub Actions workflow
cosign verify-attestation --type spdxjson \
  --certificate-identity-regexp '^https://github.com/my-org/my-app/' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  registry.example.com/myapp/api@sha256:…

# Signed with a key
cosign verify-attestation --type spdxjson --key cosign.pub registry.example.com/myapp/api@sha256:…
```

When `promote` copies an image to another registry, it copies the attestations too; see
[attestations](#attestations).

## Signing images

With `signing.enabled`, `publish` (and the publish step of `ci` and `aspire deploy`) signs every
image it pushes with [cosign](https://docs.sigstore.dev/cosign/) `sign`, by digest. The same
settings say which signatures `--require-signature` trusts:

```json
"signing": {
  "enabled": true,
  "key": null,
  "identityRegexp": "^https://github.com/my-org/my-app/",
  "issuer": "https://token.actions.githubusercontent.com"
}
```

| Setting | Default | |
|---|---|---|
| `enabled` | `false` | Sign the images publish pushes. |
| `key` | none | cosign signing key, in any form [`sbom.key`](#signing) takes. Empty signs keyless. |
| `publicKey` | none | Key to verify signatures with: a public key file, a KMS key or `env:NAME`. Defaults to `key` when that is a KMS key. |
| `identity`, `identityRegexp` | none | Keyless: who must have signed, as the identity in the signing certificate. |
| `issuer`, `issuerRegexp` | none | Keyless: the OIDC issuer that vouched for that identity. |

After all images are pushed, Deployify runs `cosign sign --yes` on each one as
`registry.example.com/myapp/api@sha256:…`, then attests the SBOMs. cosign 3 stores the signature
as an OCI referrer next to the image. Keyless signing and keys work as they do for
[SBOM attestations](#signing), and an SBOM without its own `sbom.key` is signed with
`signing.key`.

cosign must be installed: `publish` checks for it, and that a key file exists, before building
anything. An image is only signed by digest. If its pushed digest couldn't be read, `publish`
signs the others and then fails rather than sign whatever the tag points to. An image that stays
in the local store (no `containerRegistry`) isn't signed, with a warning.

### Requiring signatures

`--require-signature` on `apply`, `ci` and `promote` (and `RequireSignature` for
[`aspire deploy`](/Deployify/guides/aspire-deploy/)) stops unless every image the environment
builds is pinned by digest and has a signature its `signing` settings trust. It implies
`--require-digest`. `apply` checks before planning, in a `--dry-run` too; `ci` checks after its
publish step and before applying. Each image is checked with `cosign verify`:

- With `publicKey`, a signature made with that key (`cosign verify --key`).
- With a KMS `key` and no `publicKey`, a signature made with that key.
- Without a key, a keyless signature whose certificate names the `identity` (or matches
  `identityRegexp`) and was issued by the `issuer` (or one matching `issuerRegexp`). Both are
  required: either alone would accept a signature from anyone the other allows.

A key file or `env:` key in `key` is private, so verifying needs its public half in `publicKey`.
The verification settings work without `enabled`, so an environment that only deploys promoted
images can require signatures it never makes:

```json
"signing": { "publicKey": "cosign.pub" }
```

Only the images Deployify builds are checked. Containers it deploys as they are, such as
`redis`, keep their own signatures; check those with an admission policy in the cluster, such as
the Sigstore policy-controller or Kyverno. To check an image by hand:

```bash
cosign verify --key cosign.pub registry.example.com/myapp/api@sha256:…
```

## Promoting images to another environment

Build once, deploy many times: `promote` makes another environment deploy the images an
environment already published, by digest, without building them again.

```bash
deployify promote --from staging --to prod --image-tag "$GITHUB_SHA"
deployify apply   --env prod --image-tag "$GITHUB_SHA" --require-digest
```

`promote` looks up the digest the source environment recorded for each project and Dockerfile
build at that tag, and records it in the target environment's deployment state. It deploys
nothing: the target's next `apply` (or `ci --no-publish`) references the images by those digests.
Without `--image-tag`, it promotes the source environment's configured tag.

It promotes all images or none. It stops without recording anything when the source
environment has no digest for an image at that tag (it never published that build), or when the
target has no `containerRegistry`.

`--dry-run` shows what would be promoted and copied. The target environment's state is locked
while `promote` runs.

### Promoting to another registry

When the target environment has a different `containerRegistry`, for example a production
registry in another account or region, `promote` copies each image there first. The copy goes
from registry to registry by digest, so the target ends up with exactly the same image:
nothing is rebuilt or pulled into a local image store. Images already in the target registry
are skipped.

The copy uses the first of these tools that is installed:

| Tool | Command it runs |
|---|---|
| [crane](https://github.com/google/go-containerregistry/tree/main/cmd/crane) | `crane copy` |
| [skopeo](https://github.com/containers/skopeo) | `skopeo copy --all --preserve-digests` |
| Docker with Buildx | `docker buildx imagetools create` |

`--copy-with crane|skopeo|docker` picks one. All three sign in to the registries with the
credentials `docker login` stored, including credential helpers such as the ones for ECR, ACR
and GCR, so sign in to both registries before promoting. On a CI runner the job needs pull
access to the source registry and push access to the target one.

After each copy, `promote` checks that the target registry has a manifest with the source
digest. crane and skopeo always keep it; if Docker ever rewrites a manifest, `promote` stops
and asks for crane or skopeo. Copies happen before anything is recorded: when one fails,
the target's state is unchanged and a second run skips the images already copied.

```bash
docker login registry.staging.example.com
docker login registry.prod.example.com
deployify promote --from staging --to prod --image-tag "$GITHUB_SHA"
```

### Attestations

When the source environment attests SBOMs (`sbom.enabled` and `sbom.attest`), `promote` also
copies the cosign attestations of every image it moves to another repository, so the SBOM can
still be verified in the target registry. The signatures stay valid because they cover the image
digest, which the copy keeps. This needs [cosign](https://docs.sigstore.dev/cosign/) 3.0.3 or
later, which `promote` checks for before copying anything.

cosign stores attestations in one of two ways, and `promote` handles both:

- **As OCI referrers** (Sigstore bundles, cosign 3's default). `cosign copy` doesn't copy these,
  so `promote` reads them with `cosign download attestation` and attaches them to the target
  digest with `cosign attach bundle`. The target registry must support OCI referrers.
- **Under a `sha256-<digest>.att` tag** (older cosign, or `--new-bundle-format=false`). That tag
  is copied with the same tool as the image.

Attestations the target already has are skipped, so running `promote` again copies nothing
twice. A second run also adds attestations to images that were promoted before this was
turned on. Afterward, `promote` checks that the target has every attestation the source has and
fails, recording nothing, if one is missing. An image published without attestations gets a
warning.

### Signatures

When the source environment signs its images (`signing.enabled`), or with `--require-signature`,
`promote` copies the cosign signatures of every image it moves to another repository the same
way: referrer bundles with `cosign download signature` and `cosign attach bundle`, the older
`sha256-<digest>.sig` tag with the image copy tool. Signatures the target already has are
skipped, and `promote` checks that every one arrived.

With `--require-signature`, `promote` verifies every image in the source registry before it
copies anything, and each copied image again in the target registry before it records anything,
so the target is only told to deploy images it can verify where it pulls them from. The trusted
signatures are the target environment's [`signing` settings](#requiring-signatures), or the
source's when the target has none.

```bash
deployify promote --from staging --to prod --image-tag "$GITHUB_SHA" --require-signature
deployify apply   --env prod --image-tag "$GITHUB_SHA" --require-signature
```

### Requiring digests

`--require-digest` on `apply` and `ci` stops the deploy when a built image would run by tag
because no digest is recorded for it, for example when the promotion was skipped or used a
different tag. Without it, such an image is deployed by tag, as before.

Promotion only reuses images when the environments don't need different builds. Configuration
and secrets that Aspire passes at deploy time are fine. A Dockerfile build whose build arguments
differ per environment needs its own build in each one.

## Container runtime

Deployify uses `dotnet publish -t:PublishContainer` to build project images, and `docker build`
or `podman build` for Dockerfiles. By default it tries Docker; if Docker is unavailable it automatically falls back to Podman.

### Auto-detection

When `containerRuntime` is `Docker` (the default), Deployify:

1. Runs `docker info` — if it succeeds, uses Docker.
2. If Docker is unreachable, runs `podman info`. If Podman is found, sets `DOCKER_HOST` to the Podman socket automatically — probing well-known paths like `/run/user/<uid>/podman/podman.sock` on Linux and `~/.local/share/containers/podman/machine/qemu/podman.sock` on macOS.
3. If neither runtime is available, logs a warning and proceeds (the publish step may fail with a clear error).

When Podman is used, the publish runs with `-p:LocalRegistry=podman` so the .NET SDK loads
the image into Podman's local store rather than Docker's.

### Explicit configuration

Set `containerRuntime` in the state file or pass `--container-runtime` per run:

```bash
deployify publish --container-runtime podman
deployify init --non-interactive --target Nomad --container-runtime Podman
```

State file:

```json
{
  "environments": {
    "local": {
      "containerRuntime": "Podman"
    }
  }
}
```
