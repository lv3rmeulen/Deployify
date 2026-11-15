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

- Deployment targets: Nomad, Kubernetes, Helm, GitOps through Argo CD or Flux, OVHcloud Managed
  Kubernetes, Docker Compose, AWS ECS, Azure Container Apps, Nomad + Consul Connect, and IIS on
  Windows Server.
- Health-gated rollouts with automatic rollback, and the `rollback` command.
- `destroy` and `apply --prune`, working from the environment's inventory.
- Secret backends for parameters (Vault, Azure Key Vault, AWS SSM).
- Running Deployify from `aspire deploy` with `builder.AddDeployify()`.
- Out-of-process target plugins: plugin protocol 1.0, `Deployify.Plugin.Sdk`,
  `Deployify.Plugin.Testing`, and `deployify plugin list` / `plugin lock`.
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

### Fixed

- Console output when Deployify runs without a terminal.
- Escaping of parameter values and in console output.
- `init`, including how it handles secrets.
- `ci` publishing and image tag checks.
- Nomad services that referred to each other through `localhost`.

## 1.0.0-alpha.16 and earlier

Pre-releases, not listed individually.

[Unreleased]: https://github.com/lv3rmeulen/Deployify/compare/v1.0.0-alpha.16...HEAD
