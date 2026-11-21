# Changelog

All notable changes to Deployify are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Deployify follows
[Semantic Versioning](https://semver.org/) as described in the
[stability policy](https://lv3rmeulen.github.io/Deployify/reference/stability/).

Each release can have these sections: **Added**, **Changed**, **Deprecated**, **Removed**,
**Fixed** and **Security**. A release that raises a state file's format version says so under
**Changed**, because older releases can't read the file afterward.

## [Unreleased]

### Added

- Image signing: with `signing.enabled` in the environment, `publish`, `ci` and `aspire deploy`
  sign every image they push with `cosign sign`, by digest, keyless through Sigstore or with
  `signing.key`. SBOM attestations use `signing.key` unless `sbom.key` is set.
  `--require-signature` on `apply`, `ci` and `promote`, and `RequireSignature` in `AddDeployify()`
  (`Deployify:RequireSignature`) for `aspire deploy`, stop unless every built image is pinned
  by digest and has a signature the environment trusts: one made with `signing.publicKey` (or a
  KMS `signing.key`), or a keyless one from `signing.identity` and `signing.issuer` (or their
  `Regexp` forms). `promote` copies signatures along with images it moves to another registry,
  and with `--require-signature` verifies each image in the source registry before copying and
  in the target registry before recording. `promote --output json` reports `signaturesCopied`
  and `signatureVerified` per image.
- SBOMs at publish: with `sbom.enabled` in the environment, `publish`, `ci` and `aspire deploy`
  generate an SBOM (SPDX or CycloneDX JSON) for every image they build with syft, scanned from
  the registry by the pushed digest, write it to `{outputPath}/sbom/`, and attach it to the image
  as a signed attestation with `cosign attest`, keyless through Sigstore or with `sbom.key`.
  `promote` copies those attestations along with images it moves to another registry: both
  OCI-referrer bundles (cosign 3) and `.att` tags, skipping ones the target has, then checks
  they all arrived. `promote --output json` reports `attestationsCopied` per image.
- `--output json` (`-o json`) on `plan`, `diff`, `status`, `explain` and `promote`: stdout gets
  one JSON document (schema version 1, published as `schemas/deployify-output-v1.json` and
  described under [JSON output](https://lv3rmeulen.github.io/Deployify/reference/json-output/)),
  and the human-readable output and logs go to stderr. The document is written on failure too,
  with the error. The schema is covered by the stability policy.
- `diff` prints what changed below its table: the old and new value, or for Kubernetes and
  Helm the `kubectl diff` / `helm diff` output. Secret values resolved during the run are
  masked as `****`, there and in `--output json`. `diff --summary` prints only the table.
- Deployment targets: Nomad, Kubernetes, Helm, GitOps through Argo CD or Flux, OVHcloud Managed
  Kubernetes, Docker Compose, AWS ECS, Azure Container Apps, Nomad + Consul Connect, and IIS on
  Windows Server.
- Health-gated rollouts with automatic rollback, and the `rollback` command.
- `destroy` and `apply --prune`, working from the environment's inventory.
- Secret backends for parameters (Vault, Azure Key Vault, AWS SSM).
- Running Deployify from `aspire deploy` with `builder.AddDeployify()`. It tags published images
  with the commit SHA when the tag is `latest`, as `ci` does (`AllowMutableTag` keeps `latest`),
  and `RequireDigest` stops a run that would deploy an image by tag, like `--require-digest`.
- Out-of-process target plugins: plugin protocol 1.0, `Deployify.Plugin.Sdk`,
  `Deployify.Plugin.Testing`, and `deployify plugin list` / `plugin lock`. A plugin target only
  starts when `deployify-plugins.lock.json` has the SHA-256 of its files for the platform; use
  `--allow-unverified` (`AllowUnverifiedPlugins` for `aspire deploy`) to start one anyway, with a
  warning each time. `dotnet tool` installs are hashed by the tool behind the launcher, and
  self-contained plugins get a hash per platform.
- DigitalOcean App Platform target, shipped as the `Deployify.Target.DigitalOcean` plugin (target
  ID `digitalocean`). It deploys the app as one App Platform app, maps Postgres and Redis
  containers to Managed Databases (dev databases or clusters it can create), and prices the
  plan with App Platform's propose endpoint.
- `promote`: deploy the images another environment already published, by digest, copying them
  between registries when needed.
- `dashboard`: serve an environment's deployed resources to the Aspire dashboard.
- A manifest check that reports Aspire resources Deployify can't deploy, with
  `ignoreResources` and `--strict`.
- The documentation site and runnable samples.
- `CHANGELOG.md`, `SECURITY.md`, `CONTRIBUTING.md` and the stability policy.

### Changed

- `deployify-state.json`, `inventory.json`, `images.json` and `parameters.json` record a format
  version (1). Files from the alphas are read and upgraded the next time they're saved. A file
  from a newer Deployify is refused instead of being overwritten.

- Nomad and Nomad + Consul jobs are written as JSON (`<service>.nomad.json`, the body Nomad's
  job API takes) instead of HCL (`<service>.job.hcl`), and sent to Nomad without its HCL parser.
  `plan` removes a service's old `.job.hcl` file. To run a job by hand, use
  `nomad job run -json <service>.nomad.json`.
- Kubernetes manifests and Compose files are written from typed models. The objects are
  unchanged; a few values are quoted differently.

### Fixed

- Keys in `deployify-state.json` are case-insensitive, as the stability policy says. A file
  written in camelCase, like the examples in the docs, used to load as if it had no
  environments, and `TargetConfig` sections were only found in lower case. A file that sets the
  same key twice in different case is now refused with a message naming it, instead of one of
  them winning.
- Console output when Deployify runs without a terminal.
- Escaping of parameter values and in console output.
- `init`, including how it handles secrets.
- `ci` publishing and image tag checks.
- Nomad services that referred to each other through `localhost`.
- A parameter without a value no longer deploys its placeholder (`{name.inputs.value}`) as the
  value. When a deployed service uses it, `plan` and `generate` exit 1 and `apply`, `ci` and
  `aspire deploy` stop, on every target.

## 1.0.0-alpha.16 and earlier

Pre-releases, not listed individually.

[Unreleased]: https://github.com/lv3rmeulen/Deployify/compare/v1.0.0-alpha.16...HEAD
