---
title: "Production checklist"
description: "What to set up before Deployify deploys to production: a registry, remote state, digest pinning, health gates and locked plugins."
---

Deployify's defaults suit one machine: state on local disk, images in the local image store,
plugins started from wherever they're installed. Before an environment serves real traffic, go
through this list. Each item links to the page with the details.

- [ ] Images are pushed to a container registry, with a unique tag per release.
- [ ] Deployment state is in a remote backend that CI and people share.
- [ ] Deploys of images built elsewhere run with `--require-digest`.
- [ ] Health gates are on, and every service has a health check worth gating on.
- [ ] Plugin targets are installed at a fixed version and locked, and the lock file is committed.

## Push images to a registry

Set `ContainerRegistry` on the environment. Without one, images stay in the local Docker or
Podman store and are referenced by tag, which only works when the deploy runs on the machine that
built them. With a registry, every image Deployify pushes is recorded with its digest and
[deployed by that digest](/Deployify/guides/publishing-images/#pinning-images-by-digest).

```json
"prod": {
  "Target": "Kubernetes",
  "ContainerRegistry": "registry.example.com/shop"
}
```

- **Use a unique tag per release.** `ci` and `aspire deploy` replace `latest` with the commit SHA.
  Don't pass `--allow-mutable-tag` (or set `AllowMutableTag`) in a production pipeline: with
  `latest`, a rollback redeploys whatever `latest` points to now.
- **Give the platform pull access.** For example `imagePullSecrets` on
  [Kubernetes](/Deployify/targets/kubernetes/), or `registryUsername` and `registryPassword`
  (or `DEPLOYIFY_REGISTRY_PASSWORD`) on [Nomad](/Deployify/targets/nomad/).
- **Containers that name an image** (`redis:7`) aren't built by Deployify and are deployed as
  the AppHost names them. Give them a specific version tag in the AppHost, not `latest`.
- **A separate production registry** is fine: [`promote`](/Deployify/guides/publishing-images/#promoting-to-another-registry)
  copies images there by digest.

## Share state through a remote backend

Local state lives in `.deployify/state/` on whichever machine ran the deploy. A CI runner that
starts clean then has no inventory (so `apply --prune` and `destroy` don't know what is
deployed), no image digests, none of the generated parameter values the last deploy used, and no
lock shared with anyone else. Configure a [remote backend](/Deployify/concepts/state/#remote-state)
for every shared environment:

```json
"prod": {
  "Target": "AwsEcs",
  "Protected": true,
  "State": {
    "Backend": "S3",
    "Prefix": "shop",
    "S3": { "Bucket": "acme-deploy-state", "Region": "eu-west-1" }
  }
}
```

- **Move existing state once** with `deployify state push`. Deployments made before Deployify
  kept an inventory need `deployify state adopt` once.
- **Keep the backend private and encrypted at rest.** It holds generated passwords.
- **Run the same Deployify version everywhere** that shares the state. A file written by a newer
  version is refused by an older one. Pin the version in the pipeline, for example with the
  repository's tool manifest (`dotnet tool install deployify --version <version>` writes it into
  `.config/dotnet-tools.json`, and `dotnet tool restore` installs it).
- **Set `LockWaitSeconds`** when pipeline jobs can queue up behind each other, so a second job
  waits for the lock instead of failing.
- **Set `"Protected": true`** so `destroy` refuses to run against the environment.

## Deploy by digest only

When production deploys images that another job or environment built (`promote`,
`ci --no-publish`, or `aspire deploy` with `Publish=false`), add `--require-digest`
(`RequireDigest` for `aspire deploy`). Without it, an image that has no recorded digest for the
tag, for example because the promotion was skipped or used another tag, is deployed by tag
without a word.

```bash
deployify promote --from staging --to prod --image-tag "$GITHUB_SHA"
deployify ci --env prod --no-publish --image-tag "$GITHUB_SHA" --require-digest --auto-approve
```

See [requiring digests](/Deployify/guides/publishing-images/#requiring-digests).

## Keep health gates on

`apply`, `ci` and `aspire deploy` [gate each wave on health and roll back](/Deployify/guides/rollouts/)
by default. Keep it that way: don't pass `--no-health-gate` or `--no-rollback` (or set
`HealthGate` or `AutoRollback` to `false`) in a production pipeline.

A gate is only as good as the check behind it:

- **Map a health endpoint in production.** Aspire's `MapDefaultEndpoints()` only maps `/health`
  in the Development environment, so HTTP checks fail in production unless you map it yourself.
  Then use it: `healthCheckType: "http"` on Nomad and Kubernetes, `healthPath` on IIS, probes on
  Azure Container Apps (without them, Container Apps only reports whether containers start).
- **Compose** generates no health checks by default (`generateHealthChecks`), because the images
  `dotnet publish` builds contain neither `curl` nor `wget`. Without one, the gate only checks
  that containers run the new image and don't restart. Don't `docker image prune` between
  deploys: a rollback needs the old image.
- **Kubernetes** rollouts need `kubectl` on the runner. Without it, `apply` falls back to the API
  and doesn't gate on health.
- **Some targets don't gate.** Argo CD and Flux leave health to the controller (use their
  `wait` setting), DigitalOcean App Platform rolls back failed deployments itself, and a plugin
  target gates only when it declares the `rollout` capability (`deployify plugin list --check`).
- **Set `--health-timeout`** (default 600 seconds per service) to how long your slowest service
  takes to become healthy, plus a margin.
- **Alert on the exit code.** `2` means the deploy failed and was rolled back; `3` means the
  rollback didn't complete and someone needs to look. `deployify status --rollout <id>` shows
  each service.

## Lock plugin targets

Skip this if every environment uses a built-in target.

- **Install a fixed version** in the pipeline, for example
  `dotnet tool install -g Deployify.Target.DigitalOcean --version <version>`, rather than
  whatever is newest.
- **Lock it and commit the lock file.** `deployify plugin lock` writes
  `deployify-plugins.lock.json` with the plugin's version and the SHA-256 of its files. A
  self-contained plugin has a hash per platform: run `plugin lock` on each platform that deploys
  (for example on Windows and in Linux CI). See [lock plugins](/Deployify/extending/plugins/#lock-plugins).
- **Give a plugin with dependencies a folder of its own**, so the hash covers them too.
- **Never use `--allow-unverified`** (or `AllowUnverifiedPlugins`) in a pipeline. It's for trying
  a plugin out.
- **Upgrade on purpose:** install the new version, run `deployify plugin lock`, and review the
  change to the lock file like any other.

## Also worth checking

- **Secret backends.** With the default `Inline` backend, secret values end up in
  `deployify-output/` and `deployify-parameters.json`. Vault, Azure Key Vault or AWS SSM keep them
  out of both. See [secret backends](/Deployify/guides/parameters-and-secrets/#secret-backends-keeping-values-out-of-output-and-state).
- **No plain-text credentials** in `deployify-state.json`: `deployify state migrate-secrets --dry-run`
  lists any. Rotate values that were ever committed.
- **`--strict`** in the pipeline, so a manifest resource Deployify can't deploy fails the run
  instead of a warning. See [ignored resources](/Deployify/reference/state-file/#ignored-resources).
- **A drift check** after deploying:
  `deployify ci --skip-apply --fail-on-changes --image-tag "$GITHUB_SHA"`.
- **The [known limitations](/Deployify/reference/known-limitations/)**, in case one applies to
  your app.
