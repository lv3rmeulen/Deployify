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
| `{prefix}/{env}/images.json` | digests of the images Deployify pushed, so artifacts [pin them](/Deployify/guides/publishing-images/#pinning-images-by-digest) |
| `{prefix}/{env}/parameters.json` | generated parameter values (remote backends only; locally they are `deployify-parameters.json`) |
| `{prefix}/{env}/rollouts/{id}.json` | rollout, prune and destroy journals (remote backends only; locally `.deployify/rollouts/`) |
| `{prefix}/{env}/releases/{time}-{id}.json` | the [release history](#release-history): one record per publish, promote, deploy and rollback |
| `{prefix}/{env}/deployed.json` | what the last successful deploy deployed (manifest, settings, image digests), which [drift checks](/Deployify/guides/drift-alerts/) compare the live state with |
| `{prefix}/{env}/drift.json` | the result of the last drift check and the last alert sent, so a scheduled check alerts about changes rather than on every run |

By default it all lives in `.deployify/state/` next to the state file, which is fine for one
machine. Keep `.deployify/` and `deployify-parameters.json` out of git.

## Format versions

`deployify-state.json` (`FormatVersion`), `inventory.json`, `images.json`, `parameters.json`,
`deployed.json`, `drift.json` and the release records (`formatVersion`) record the format they were written in. Files from the alphas have no version;
Deployify still reads them and writes them in the current format the next time it saves them.
`deployify-state.json` is written in version 2 only when an environment
[extends another](/Deployify/reference/state-file/#layered-environments); otherwise it stays at
version 1, which every 1.x release reads.
A file written by a newer Deployify is refused with a message naming the file, instead of being
read in part and overwritten. Update Deployify to work with it. Everyone sharing remote state
should run the same Deployify version: an alpha build can't read `images.json` or
`parameters.json` once a newer build has rewritten them.

## Release history

Every release adds a record to the environment's history:

| Kind | Written by | Images |
|---|---|---|
| `publish` | `publish`, and `ci` or `aspire deploy` when they build images | the images pushed, by digest |
| `promote` | `promote`, when it records new digests (`fromEnvironment` says where they came from) | the images the environment now deploys |
| `deploy` | `apply`, `ci` and `aspire deploy`, whether the deploy succeeded, failed or was rolled back by its health gate | the images deployed |
| `rollback` | `rollback` (`rolloutId` names the rollout it undid) | none: it restores earlier revisions |

A record also holds the image tag, the commit the images were built from, who ran it, a link to
the CI run, the rollout journal of a health-gated deploy, and, when the release didn't succeed,
why. `deployify history` lists them, newest first; `--output json` gives the same for scripts.

- **Who** comes from `DEPLOYIFY_ACTOR`, `GITHUB_ACTOR`, `BUILD_REQUESTEDFOR` (Azure Pipelines),
  `GITLAB_USER_LOGIN` or `BUILD_USER_ID` (Jenkins). Outside CI it is your account name; in a CI
  system Deployify doesn't recognize it is left empty rather than recording the runner's
  account. The run link is the same as [the lock's](#the-lock).
- **The commit** of a run that builds its images is the CI commit variable, or `git rev-parse
  HEAD` when the working tree is clean. A run that deploys or promotes images built elsewhere
  never reads the working tree, which can be checked out at another commit: the commit is the
  tag itself when it is a full commit SHA (what `ci` tags with), or else the commit of that tag's
  publish record.
- **Records are written once and never changed**, each under its own key, so concurrent CI jobs
  never conflict. Writing one never fails a release: if the backend refuses, Deployify warns
  and carries on.
- **The oldest records are deleted** once there are more than `State.History.Keep` (default
  200) in an environment.

| `State.History.Keep` | |
|---|---|
| `200` (default), or any positive number | Records are kept up to that number; the oldest go first. |
| `0` | Every record is kept. |
| `-1` | **The history is off**: nothing is recorded for the environment, and Deployify doesn't look up commits for it. Records written before stay where they are (delete their keys yourself if you want them gone), and `deployify history` lists them with a warning that the history is off. |
| any other negative number | Refused: every command for the environment stops with an error naming the setting, so a typo can't quietly turn the history off. |

```json
"State": {
  "Backend": "S3",
  "S3": { "Bucket": "acme-deploy-state" },
  "History": { "Keep": 1000 }
}
```

With local state, the history is only on the machine that made the releases. Use a remote
backend for a history that CI and your team share.

## What was deployed

Every successful deploy (`apply`, `ci`, `aspire deploy`) replaces `deployed.json` with what it
deployed: the Aspire manifest after Deployify's additions, the environment's effective settings
(bases merged in) and the images by digest, with the release record's id, tag, commit and actor.
It is written whether or not the release history is on, and writing it never fails a deploy.
`destroy` removes it. [Drift checks](/Deployify/guides/drift-alerts/) regenerate the artifacts
from it, so they compare the live state with the last deploy rather than with the working tree.

The settings are the ones in `deployify-state.json`, which is committed; parameter values aren't
in it (generated ones stay in `parameters.json` or your secret backend). A large manifest makes
a large object: Consul's default limit is 512 KB per value, and a Kubernetes Secret holds about
1 MiB.

## Ownership

Every resource Deployify creates carries an ownership marker:

| Target | Marker |
|---|---|
| Nomad, Nomad + Consul | job meta `deployify-managed = "true"` |
| Kubernetes | namespace label `app.kubernetes.io/managed-by=deployify` |
| Compose | the Compose project (`com.docker.compose.project` label) |
| AWS ECS | service tags `deployify-managed-by=deployify` and `deployify-env=<environment>` |
| Azure Container Apps | resource tags `deployify-managed-by=deployify` and `deployify-env=<environment>` |
| DigitalOcean App Platform | the environment's App Platform app (`AppName`); database clusters Deployify creates are tagged `deployify` and `deployify-app-<app>` |

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
failing (useful when CI jobs queue up). The CI run URL comes from GitHub Actions, GitLab CI,
Azure Pipelines or Jenkins variables, or from `DEPLOYIFY_RUN_URL`. Every state write is compare-and-swap,
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
