---
title: "Generate, plan and apply"
description: "The everyday loop: write artifacts, preview the change, deploy."
---

See [Health-gated rollouts](/Deployify/guides/rollouts/) for what `apply` does while it deploys, and the [CLI reference](/Deployify/reference/cli/) for every option.

## `generate`

Writes artifacts (HCL jobs, Kubernetes YAML, Helm charts, `docker-compose.yml`, ECS task definitions, ACA specs) to `deployify-output/` without contacting the cluster. Use this as a standalone step in CI or to inspect outputs before planning.

```bash
deployify generate
deployify generate --env prod --target Kubernetes
deployify generate --env prod --target Helm   # one Helm chart per service, from the same settings
```

## `plan`

Generates artifacts and compares them against the live cluster state. Exits 0 even when the cluster is unreachable — the diff is best-effort.

A service that exists on the target but couldn't be compared is listed under **Could not be checked**, with the reason, instead of being guessed as an update. Typical reasons: `kubectl diff` failed (on Windows it needs a `diff` program — add Git's `usr\bin` folder to `PATH` or set `KUBECTL_EXTERNAL_DIFF`), only the in-cluster API fallback is available, Nomad's plan API returned an error, or Docker Compose is older than 2.18 and has no `up --dry-run`. `plan` and `diff` use the same comparison, so they always agree.

```bash
deployify plan
deployify plan --env prod
```

## `apply`

```bash
deployify apply
deployify apply --auto-approve          # skip confirmation prompt
deployify apply --env prod --auto-approve
deployify apply --dry-run               # generate + plan only, no changes applied
```

## `diff`

```bash
deployify diff
deployify diff --env prod
```

## `validate`

```bash
deployify validate
deployify validate --env prod
```

## `explain` / `graph`

```bash
deployify explain
deployify graph
```

## `watch`

Watches `*.cs`, `*.csproj`, `*.json`, `*.yaml`, `*.yml`, and `*.toml` files for changes (debounced 500 ms) and re-runs generate + plan on every change. Does not apply — run `deployify apply` separately when ready.

```bash
deployify watch
deployify watch --debounce 1000         # wait 1 s after last change
deployify watch --no-generate           # re-run plan diff only
```

Press **Ctrl+C** to stop.
