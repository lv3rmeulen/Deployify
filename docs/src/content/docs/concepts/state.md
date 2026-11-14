---
title: "State, locking and remote backends"
description: "Inventory, ownership markers, the environment lock and shared state."
---

Next to the configuration in `deployify-state.json` (which you commit), Deployify keeps the
**deployment state** of each environment:

| Key | What |
|---|---|
| `{prefix}/{env}/inventory.json` | every service Deployify deployed, with its dependencies (what destroy and prune work from) |
| `{prefix}/{env}/lock.json` | who is changing the environment right now |
| `{prefix}/{env}/images.json` | digests of the images Deployify pushed, so artifacts [pin them](/guides/publishing-images/#pinning-images-by-digest) |
| `{prefix}/{env}/parameters.json` | generated parameter values (remote backends only; locally they are `deployify-parameters.json`) |
| `{prefix}/{env}/rollouts/{id}.json` | rollout, prune and destroy journals (remote backends only; locally `.deployify/rollouts/`) |

By default it all lives in `.deployify/state/` next to the state file, which is fine for one
machine. Keep `.deployify/` and `deployify-parameters.json` out of git.

## Ownership

Every resource Deployify creates carries an ownership marker:

| Target | Marker |
|---|---|
| Nomad, Nomad + Consul | job meta `deployify-managed = "true"` |
| Kubernetes | namespace label `app.kubernetes.io/managed-by=deployify` |
| Compose | the Compose project (`com.docker.compose.project` label) |
| AWS ECS | service tags `deployify-managed-by=deployify` and `deployify-env=<environment>` |
| Azure Container Apps | resource tags `deployify-managed-by=deployify` and `deployify-env=<environment>` |

ECS services and Container Apps are tagged when they are created and on every later deploy, so
services that existed before tagging are adopted on their next `apply`. Plan only lists a service
as "to delete" when it carries the marker (and, on ECS and ACA, this environment's tag).

Deployments made before Deployify kept an inventory aren't in it. Run `deployify state adopt` once
to record everything the platform marks as Deployify's in this environment.

## The lock

`apply`, `ci` (unless `--skip-apply`), `rollback`, `destroy`, `state adopt` and `state push` take
the environment's lock first; `plan`, `diff`, `status` and `state show` don't need it. A second run
fails at once with who holds it:

```
✗ Environment 'prod' is locked by ci@runner-7, apply, started 4 min ago (lock id 3f9a1c2e7b4d),
  run: https://github.com/acme/shop/actions/runs/123. If that run is no longer going, wait for the
  lock to expire (14:31:07) or run 'deployify state unlock 3f9a1c2e7b4d'.
```

The lock is a lease: its holder renews it every third of `LockTtlSeconds` (default 180), so a run
that crashes frees it within that time. Set `LockWaitSeconds` to wait for a busy lock instead of
failing (useful when CI jobs queue up). The CI run URL comes from GitHub Actions, GitLab CI or
Azure Pipelines variables, or from `DEPLOYIFY_RUN_URL`. Every state write is compare-and-swap,
so a run that lost its lock (it expired while the run hung) can't overwrite the next run's state.
Machines sharing a lock need roughly synchronized clocks (NTP is plenty).

## Remote state

Configure a backend per environment under `State`, so CI runners and colleagues share the lock,
the inventory, the generated parameter values and the rollout journals:

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

| Backend | Section | Lock and writes use | Needs |
|---|---|---|---|
| `Local` (default) | — | a content hash, guarded by a file lock | — |
| `AzureBlob` | `"AzureBlob": { "AccountName": "...", "Container": "deployify-state", "SasToken": "env:STATE_SAS" }` | blob ETags (`If-Match` / `If-None-Match: *`) | a SAS token, or `az login` with Storage Blob Data Contributor |
| `S3` | `"S3": { "Bucket": "...", "Region": "...", "Profile": "..." }` | S3 conditional writes | AWS CLI 2.22+ |
| `Consul` | `"Consul": { "Address": "http://consul:8500", "Token": "env:CONSUL_HTTP_TOKEN" }` | KV check-and-set | — |
| `Kubernetes` | `"Kubernetes": { "Namespace": "deployify-state", "Context": "..." }` | Secret `resourceVersion` | kubectl; the namespace must exist |

Commands work on local copies as before: when they start they pull this environment's parameter
values and journals from the backend, and they push them back as they change (each journal save,
and the parameter values before anything is deployed). Read-only commands fall back to the local
copy, with a warning, when the backend can't be reached; commands that take the lock fail instead.

Remote state holds generated passwords: keep the bucket, container, Consul path or namespace
private, and encrypted at rest.

To move an existing environment to remote state, add the `State` section and run
`deployify state push` once. It refuses to replace remote state of another lineage, or a newer one,
without `--force`.
