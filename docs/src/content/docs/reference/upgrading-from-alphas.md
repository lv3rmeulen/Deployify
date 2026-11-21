---
title: "Upgrading from the alphas"
description: "What changes when you move a project from 1.0.0-alpha.16 or earlier to a current release, and what to do about it."
---

The 1.0.0 alphas were pre-releases, so changes since 1.0.0-alpha.16 didn't go through the
deprecation steps in the [stability policy](/Deployify/reference/stability/). Most of them add
something; the ones below can change what an existing project or pipeline does. Your
`deployify-state.json` keeps working as it is: no keys were renamed or removed.

## Before you upgrade

- **Upgrade everyone at once.** Deployify now writes a format version into
  `deployify-state.json`, `inventory.json`, `images.json` and `parameters.json`. It reads files
  from the alphas and upgrades them the next time it saves them; after that, an alpha build
  can't read `images.json` or `parameters.json`. Update the pipeline and every machine that
  deploys the same environment together. See [format versions](/Deployify/concepts/state/#format-versions).
- **Update `.gitignore`.** Keep `.deployify/` (local state, rollout journals) and
  `deployify-parameters.json` out of git.

## After you upgrade

Do these once per environment:

1. **Move plain-text credentials out of the state file.** `init` now refuses literal tokens and
   passwords unless you pass `--allow-plaintext-secrets`. To fix an existing file:

   ```bash
   deployify state migrate-secrets --dry-run   # shows what it would change
   deployify state migrate-secrets             # replaces each value with an env:NAME reference
   ```

   It prints the environment variables to set. If the file was ever committed, rotate the old
   values. See [credentials Deployify uses](/Deployify/guides/parameters-and-secrets/#credentials-deployify-uses).

2. **Record what is already deployed.** `destroy` and `apply --prune` work from the environment's
   inventory, which deployments made by the alphas aren't in. Run `deployify state adopt` to add
   everything the platform marks as Deployify's.

3. **Move shared environments to remote state** if CI and people deploy the same environment:
   add a `State` section and run `deployify state push`. See
   [remote state](/Deployify/concepts/state/#remote-state).

## What behaves differently

### `apply` and `ci` wait for health and roll back

Both now deploy in dependency order, wait for each wave to become healthy, and roll back when a
service fails. See [health-gated rollouts](/Deployify/guides/rollouts/).

- **New exit codes.** `2` means the deploy failed and was rolled back, `3` that the rollback
  didn't complete. A script that tests for `1` specifically should test for "not `0`".
- **Runs take longer**, up to `--health-timeout` (600 seconds) per service. Lower it, or pass
  `--no-health-gate` for the old behavior.
- **HTTP health checks** need a health endpoint in production: Aspire's `MapDefaultEndpoints()`
  only maps `/health` in Development.
- **On Nomad**, jobs now get `auto_revert = true` (`"autoRevert": false` turns it off), and the
  Nomad token needs write access to `deployify/*` variables so a rollback can restore secrets.

### Environments are locked during changes

`apply`, `ci`, `rollback`, `destroy`, `state adopt` and `state push` take the environment's lock,
so a second run against the same environment fails at once instead of deploying alongside. Set
`LockWaitSeconds` to wait instead. See [the lock](/Deployify/concepts/state/#the-lock).

### `ci` builds and pushes images

`ci` now runs generate → validate → plan → **publish** → apply.

- If your pipeline builds images in a separate step, add `--no-publish`.
- When the configured tag is `latest` (the default), `ci` tags the images with the commit SHA. With
  no SHA available it stops: pass `--image-tag`, or `--allow-mutable-tag` to keep `latest`.
- After publishing, generated artifacts reference images by digest (`…/api@sha256:…`) instead
  of by tag. See [pinning images by digest](/Deployify/guides/publishing-images/#pinning-images-by-digest).

### Dockerfile resources are built

`publish` and `ci` now build and push resources added with `AddDockerfile` or `WithDockerfile`.
Builds with build secrets, or with build arguments that come from parameters, stop the publish:
see [known limitations](/Deployify/reference/known-limitations/#dockerfile-build-secrets). If you
run such a resource some other way, list it under
[`ignoreResources`](/Deployify/reference/state-file/#ignored-resources).

### Missing parameter values stop the run

A parameter without a value used to be deployed as its placeholder (`{name.inputs.value}`). Now,
when a deployed service uses it, `plan` and `generate` exit 1 and `apply`, `ci` and
`aspire deploy` stop. Give it a value in the environment's `parameters`, a `Parameters__<name>`
environment variable or a secret backend. See [parameters and secrets](/Deployify/guides/parameters-and-secrets/).

### Unsupported resources are reported

Before planning, Deployify lists manifest resources it can't deploy (executables, Azure or AWS
infrastructure, unknown types) and stops the deploy when a service depends on one. List resources
you run another way under `ignoreResources`.

### Nomad jobs are JSON

Nomad and Nomad + Consul jobs are written as `<service>.nomad.json` (the body Nomad's job API
takes) instead of `<service>.job.hcl`. `plan` removes the old `.job.hcl` files. If you run jobs by
hand, use `nomad job run -json <service>.nomad.json`.

On Nomad, services now find each other through the service catalog, so they can run on different
nodes. That needs `registerServices` (on by default) and Nomad 1.4 or later. See
[Nomad networking](/Deployify/targets/nomad/).

### Kubernetes and Compose files are written differently

Kubernetes manifests and Compose files are written from typed models. The objects are the same,
but a few values are quoted differently, so a text diff of `deployify-output/` against an alpha's
output shows changes that the platform doesn't see.

## If you ran a build of `main` with plugins

Plugin targets came after alpha.16. If you used them from a build of `main`:

- `plugin lock --no-hash` is gone. Every plugin target needs a SHA-256 in
  `deployify-plugins.lock.json`; run `deployify plugin lock` again and commit the result. A
  `dotnet tool` install is now hashed by the tool's folder in the tool store, so it locks the
  same on every machine.
- A plugin that isn't locked is refused. `--allow-unverified` (`AllowUnverifiedPlugins` for
  `aspire deploy`) starts it anyway, with a warning each time.

See [lock plugins](/Deployify/extending/plugins/#lock-plugins).
