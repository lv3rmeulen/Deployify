---
title: "JSON output (--output json)"
description: "The machine-readable output of plan, diff, drift, status, explain, promote and history, and its schema."
---

`plan`, `diff`, `drift`, `status`, `explain`, `promote` and `history` take `--output json` (or `-o json`). The
command then writes exactly one JSON document to stdout, and everything it would normally print
(the tables, warnings, progress and logs) to stderr. A pipeline can parse stdout and still
show the human-readable output in its log:

```bash
deployify plan --env prod --output json > plan.json
jq -r '.result.create[]' plan.json
```

The document is written whether the command succeeds or not, including when it fails before
it has anything to report, so a script can always parse stdout. The exit code is the same as
with text output, and is repeated in the document.

The schema is [JSON Schema 2020-12](https://json-schema.org/), published at
[`/Deployify/schemas/deployify-output-v1.json`](/Deployify/schemas/deployify-output-v1.json)
and kept in the repository at `docs/public/schemas/deployify-output-v1.json`.

:::note
[`deployify logs`](/Deployify/guides/logs/) also takes `--output json`, but writes a stream rather
than one document: a JSON object per log line ([JSON Lines](https://jsonlines.org/)) with
`service`, `instance`, `timestamp`, `stream` and `text`, and no envelope. It isn't covered by the
schema below.
:::

## Compatibility

Every document has `"schemaVersion": 1`. Within version 1, which lasts at least as long as
Deployify 1.x (see [Stability](/Deployify/reference/stability/#json-output)):

- Properties aren't renamed, removed or given another type, and a property that is always
  present stays present. Properties whose value can be missing are written as `null`, never
  left out.
- A minor release can **add properties**, anywhere. Ignore the ones you don't know, and don't
  validate with `additionalProperties: false`.
- The values of `command`, `severity`, diff `kind` and the plan lists are fixed. Rollout
  `operation` and `state` values, and release `kind` and `outcome` values, are open-ended: a new kind of rollout or state can appear in
  a minor release, so handle a value you don't know.
- `message`, `reason`, `error` and similar texts are for people. They can change in any
  release; don't match on them.
- `explain`'s `details` keys come from each target and aren't covered.

A change that breaks any of this would come with `"schemaVersion": 2`, in a major release.

## The envelope

Every command writes the same envelope; `result` holds the command's own document.

```json
{
  "schemaVersion": 1,
  "command": "plan",
  "success": true,
  "exitCode": 0,
  "error": null,
  "environment": "prod",
  "target": { "id": "Kubernetes", "name": "Kubernetes" },
  "diagnostics": [
    { "severity": "warning", "message": "'storage' is Azure infrastructure, which Deployify doesn't provision. …" }
  ],
  "result": { … }
}
```

| Property | Type | |
|---|---|---|
| `schemaVersion` | `1` | |
| `command` | string | `plan`, `diff`, `status`, `explain`, `promote` or `history`. |
| `success` | boolean | `true` exactly when `exitCode` is `0`. |
| `exitCode` | integer | The process exit code. |
| `error` | string \| null | Why the command failed: the first error in `diagnostics`. `null` on success. |
| `environment` | string \| null | The environment the command ran against; for `promote`, the `--to` environment. `null` when the command failed before resolving it. |
| `target` | object \| null | The environment's target: `id` as written in the state file (`Nomad`, `Kubernetes`, …, or a plugin target's ID) and its display `name`. |
| `diagnostics` | array | The info, warning and error messages the command reported, in order: `{ "severity": "info" \| "warning" \| "error", "message": "…" }`. Log lines (from targets talking to a platform) go to stderr only. |
| `result` | object \| null | The command's document, below. `null` when the command failed before producing one; a command that fails after producing one (a blocked plan, a promotion with a missing digest) still includes it. |

## plan

```json
"result": {
  "create": ["api"],
  "update": ["worker"],
  "delete": ["old-service"],
  "unchanged": ["redis"],
  "unknown": [{ "service": "web", "reason": "timed out after 30s" }],
  "skipped": [{ "name": "storage", "type": "azure.bicep.v0", "reason": "is Azure infrastructure, …" }],
  "upToDate": false,
  "deletesOnApply": false,
  "blocked": false,
  "blockedReason": null,
  "manifestFindings": [
    { "resource": "storage", "type": "azure.bicep.v0", "severity": "warning", "message": "…" }
  ]
}
```

| Property | |
|---|---|
| `create`, `update` | Services `apply` would create or update. |
| `delete` | Services on the target that are no longer in the manifest. Whether `apply` removes them is in `deletesOnApply`. |
| `unchanged` | Services known to match. |
| `unknown` | Services on the target that couldn't be compared, with the reason. `apply` may or may not change them. |
| `skipped` | Manifest resources Deployify doesn't deploy, with their manifest type. |
| `upToDate` | `true` only when `create`, `update`, `delete` and `unknown` are all empty. |
| `deletesOnApply` | `true` when `apply` removes the services under `delete` without `--prune`: the environment sets `Prune`, or the target is Compose. |
| `blocked` | `true` when `apply` would refuse because of `manifestFindings`: an error, or a warning with `--strict`. The exit code is then `1`, and `blockedReason` and `error` say why. |
| `manifestFindings` | What the manifest audit found: resources that won't be deployed and parameters without a value, each with `severity` `info` (listed under `ignoreResources`), `warning` (nothing deployed depends on it) or `error` (a deployed service does). |

To gate a pipeline on changes, test `upToDate`, or use `deployify ci --fail-on-changes`.

## diff

```json
"result": {
  "identical": false,
  "entries": [
    { "service": "api", "kind": "added", "field": null, "oldValue": null, "newValue": null, "reason": null },
    {
      "service": "worker",
      "kind": "changed",
      "field": "manifest",
      "oldValue": null,
      "newValue": "diff -u -N /tmp/LIVE-1234/apps.v1.Deployment.myapp-worker.worker /tmp/MERGED-5678/…\n…\n-        image: registry.example.com/myapp/worker:3f9c2e0\n+        image: registry.example.com/myapp/worker:3f9c2e1",
      "reason": null
    },
    { "service": "web", "kind": "unknown", "field": null, "oldValue": null, "newValue": null, "reason": "timed out after 30s" }
  ]
}
```

Entries are ordered `added`, `removed`, `changed`, `unchanged`, `unknown`. Most targets give
one entry per service; a target that compares field by field (DigitalOcean) gives one per
changed field. `field` says what changed when the target reports it, and `reason` why an
`unknown` service couldn't be compared. `identical` is `true` when every entry is `unchanged`.

`oldValue` and `newValue` are what the target reports, as text, and differ per target:

| Target | `field` | `oldValue` / `newValue` |
|---|---|---|
| Kubernetes, OVHcloud | `manifest` | `newValue` is the output of `kubectl diff`; `oldValue` is `null`. |
| Helm | `release` | `newValue` is the output of `helm diff upgrade`; `oldValue` is `null`. |
| GitOps (Argo CD, Flux) | `files` | `newValue` lists the changed files. |
| DigitalOcean | the component's changed setting, e.g. `instance_count` or `env.DB_URL` | The live and the desired value. Secret environment values read `(secret)`. |
| IIS | `site`, `release` | The live and the desired site or release. |
| Host | `release`, `hosts` | The live and the desired release; for `hosts`, which hosts don't run the service yet. |
| Plugin targets | as the plugin reports | as the plugin reports |

Other targets leave both `null`. Neither value is meant to be parsed: show it to a person, for
example in a pull request comment. The text output of `diff` prints the same values below its
table.

Secrets stay out. `kubectl diff` and helm-diff already hide the data of Kubernetes Secrets,
and on top of that Deployify replaces every secret parameter value it resolved during the run
with `****`, as is and base64-encoded, in `oldValue`, `newValue` and `reason`. A secret
Deployify never resolved (one a service reads from somewhere else) can't be recognized, so
treat the values like the rest of your CI log.

## drift

`environments` has one entry per environment checked: the active one, `--env`, or with `--all`
every environment in `deployify-state.json` that isn't abstract. With `--all` the envelope's
`environment` and `target` are `null`; each entry has its own.

```json
"result": {
  "drifted": true,
  "environments": [
    {
      "environment": "prod",
      "target": { "id": "AwsEcs", "name": "AWS ECS" },
      "status": "drifted",
      "checkedAt": "2026-11-22T09:00:04.118+00:00",
      "reason": null,
      "deployed": {
        "releaseId": "8c1f0a2b9d3e",
        "at": "2026-11-20T14:02:11.204+00:00",
        "tag": "4f2c9e1a7b3d4f2c9e1a7b3d4f2c9e1a7b3d4f2c",
        "commit": "4f2c9e1a7b3d4f2c9e1a7b3d4f2c9e1a7b3d4f2c",
        "actor": "lv3rmeulen",
        "runUrl": "https://github.com/acme/shop/actions/runs/123"
      },
      "entries": [
        { "service": "redis", "kind": "missing", "field": null, "live": null, "deployed": null, "reason": null },
        { "service": "worker", "kind": "changed", "field": "desired_count", "live": "5", "deployed": "2", "reason": null }
      ],
      "ignored": 0,
      "fingerprint": "3f9a0c1d2e4b5a69",
      "alert": {
        "event": "drifted",
        "deliveries": [ { "channel": "slack", "type": "slack", "ok": true, "error": null } ]
      }
    },
    {
      "environment": "staging",
      "target": { "id": "AwsEcs", "name": "AWS ECS" },
      "status": "inSync",
      "checkedAt": "2026-11-22T09:00:06.950+00:00",
      "reason": null,
      "deployed": { "releaseId": "77d0e4c1a2b3", "at": "2026-11-21T10:40:00.000+00:00", "tag": "…", "commit": "…", "actor": "lv3rmeulen", "runUrl": null },
      "entries": [],
      "ignored": 0,
      "fingerprint": "",
      "alert": null
    }
  ]
}
```

| Property | |
|---|---|
| `drifted` | `true` when any environment drifted; the exit code is then `2`. |
| `status` | `inSync`, `drifted`, `unknown` (nothing drifted that could be checked, but some services couldn't be compared), `error` (the check couldn't run; `reason` says why), `notDeployed` (no successful deploy recorded yet) or `skipped` (a deploy holds the lock, or `drift.enabled` is `false`). Can grow. |
| `deployed` | The deploy the live state was compared with. `releaseId` is `null` when the release history is off. |
| `entries[].kind` | `missing` (deployed, gone from the target), `extra` (on the target, not deployed), `changed` or `unknown`. Ordered in that order. |
| `entries[].live`, `entries[].deployed` | What `diff` calls `oldValue` and `newValue`, with the same per-target meaning and the same masking of secrets. |
| `ignored` | Differences left out by `drift.ignore`. |
| `fingerprint` | The same for the same differences; empty when nothing drifted. |
| `alert` | The alert the check called for and where it was delivered; `null` when none was due, and with `--no-notify`. `event` is `drifted`, `changed`, `reminder`, `resolved` or `failing`. `deliveries` is empty when `drift.notify` has no channels. |

## explain

```json
"result": {
  "services": [
    {
      "name": "worker",
      "image": "registry.example.com/myapp/worker",
      "imageTag": "3f9c2e1",
      "count": 2,
      "ports": ["http:8080/TCP"],
      "dependsOn": ["api", "redis"],
      "details": { "cpu": "req:100m limit:500m", "namespace": "myapp-worker" }
    }
  ]
}
```

`--service` filters the list; no match gives an empty list and exit code `0`. `details` holds
what the target adds (namespace, job name, CPU and memory, …); its keys differ per target.
Environment variables aren't included, because they can hold resolved secrets.

## status

Without `--rollout`, `rollouts` lists the most recent rollouts, newest first and up to
`--limit`, and `rollout` is `null`:

```json
"result": {
  "rollouts": [
    {
      "id": "20261121-104512-prod",
      "operation": "rollout",
      "target": "AWS ECS (Fargate)",
      "startedAt": "2026-11-21T10:45:12+00:00",
      "finishedAt": "2026-11-21T10:47:40+00:00",
      "state": "rolledBack",
      "reason": "api failed its health check"
    }
  ],
  "rollout": null
}
```

With `--rollout ID`, `rollout` is that rollout, with everything above plus its waves, services
and events, and `rollouts` is `null`:

```json
"result": {
  "rollouts": null,
  "rollout": {
    "id": "20261121-104512-prod",
    "operation": "rollout",
    "target": "AWS ECS (Fargate)",
    "startedAt": "2026-11-21T10:45:12+00:00",
    "finishedAt": "2026-11-21T10:47:40+00:00",
    "state": "rolledBack",
    "reason": "api failed its health check",
    "waves": [["redis"], ["api"]],
    "services": [
      {
        "name": "api",
        "state": "rolledBack",
        "before": { "existed": true, "revision": "arn:aws:ecs:…:task-definition/api:6" },
        "revision": "arn:aws:ecs:…:task-definition/api:7",
        "restoredRevision": "arn:aws:ecs:…:task-definition/api:6",
        "detail": "2 of 2 tasks unhealthy",
        "strategy": null,
        "candidate": null,
        "trafficPercent": null,
        "previewUrl": null
      }
    ],
    "events": [
      { "at": "2026-11-21T10:46:58+00:00", "service": null, "message": "Rolling back 1 service(s)" }
    ],
    "journal": "/home/runner/work/myapp/.deployify/rollouts/20261121-104512-prod.json"
  }
}
```

Timestamps are ISO 8601 with an offset. `operation` is `rollout`, `destroy` or `prune`. A
rollout's `state` is `running`, `succeeded`, `failed`, `rollingBack`, `rolledBack` or
`rollbackFailed`; a service's is `pending`, `deploying`, `verifying`, `healthy`, `failed`,
`revertedByPlatform`, `rolledBack`, `rollbackFailed`, `deleted` or `deleteFailed`. Both lists
can grow. `before` is what ran before the rollout (`null` when it wasn't recorded), and
`events` has every event, including the per-service ones the text output leaves out. An
unknown `--rollout` ID fails with exit code `1` and `result: null`.

## promote

```json
"result": {
  "from": "staging",
  "to": "prod",
  "imageTag": "3f9c2e1",
  "dryRun": false,
  "promoted": true,
  "recorded": 2,
  "copyTool": "crane",
  "images": [
    {
      "service": "api",
      "repository": "prod.azurecr.io/myapp/api",
      "tag": "3f9c2e1",
      "digest": "sha256:4b1d…",
      "sourceRepository": "staging.azurecr.io/myapp/api",
      "sourceTag": "3f9c2e1",
      "needsCopy": true,
      "alreadyPromoted": false,
      "copied": true,
      "problem": null,
      "attestationsCopied": 1,
      "signaturesCopied": 1,
      "signatureVerified": true
    }
  ]
}
```

| Property | |
|---|---|
| `promoted` | `true` when the digests are recorded in the `to` environment. Always `false` with `--dry-run`, and when anything failed. |
| `recorded` | How many digests this run newly recorded; `0` when they all were already. |
| `copyTool` | `crane`, `skopeo` or `docker` when images had to be copied to the target's registry, `null` when none had to be. |
| `images[].repository`, `tag` | Where the `to` environment pulls the image from. |
| `images[].needsCopy` | `true` when the target uses another repository, so the image is copied there first. |
| `images[].copied` | `true` when this run copied the image. An image that was already in the target registry isn't copied again. |
| `images[].attestationsCopied` | How many cosign attestations this run copied to the target registry with the image; `0` when it already had them all. `null` when attestations aren't copied: the image stays in the same repository, the source environment doesn't attest SBOMs, or with `--dry-run`. |
| `images[].signaturesCopied` | How many cosign signatures this run copied to the target registry with the image; `0` when it already had them all. `null` when signatures aren't copied: the image stays in the same repository, the source environment doesn't sign images and `--require-signature` isn't set, or with `--dry-run`. |
| `images[].signatureVerified` | With `--require-signature`: `true` when the image's signature verified where the target pulls it from (checked again after copying), `false` when that wasn't checked yet, such as with `--dry-run` for an image that is copied. `null` without `--require-signature`. |
| `images[].problem` | Why the image can't be promoted, such as a missing digest. Any problem means nothing is promoted and the exit code is `1`; the `images` list still shows which ones. |

## history

`releases` lists the environment's [release history](/Deployify/concepts/state/#release-history),
newest first and up to `--limit`:

```json
"result": {
  "releases": [
    {
      "id": "8c1f0a2b9d3e",
      "kind": "deploy",
      "at": "2026-11-22T09:14:03.512+00:00",
      "outcome": "rolledBack",
      "tag": "4f2c9e1a7b3d4f2c9e1a7b3d4f2c9e1a7b3d4f2c",
      "commit": "4f2c9e1a7b3d4f2c9e1a7b3d4f2c9e1a7b3d4f2c",
      "fromEnvironment": null,
      "actor": "lv3rmeulen",
      "actorSource": "GITHUB_ACTOR",
      "runUrl": "https://github.com/acme/shop/actions/runs/123",
      "rolloutId": "20261122-091240-prod",
      "summary": "Rollout failed and was rolled back. api failed its health check",
      "deployifyVersion": "1.1.0",
      "images": [
        {
          "service": "api",
          "repository": "acme.azurecr.io/shop/api",
          "tag": "4f2c9e1a7b3d4f2c9e1a7b3d4f2c9e1a7b3d4f2c",
          "digest": "sha256:4b1d…"
        }
      ]
    }
  ]
}
```

| Property | |
|---|---|
| `kind` | `publish`, `promote`, `deploy` or `rollback`. Can grow. |
| `outcome` | `succeeded`, `rolledBack` (a deploy whose health gate failed and was rolled back) or `failed`. Can grow. |
| `tag`, `commit` | The image tag and the commit the images were built from. `null` when unknown, and for a rollback. |
| `fromEnvironment` | For a promote, the environment the digests came from. |
| `actor`, `actorSource` | Who ran it, and the CI variable that said so (`local` outside CI). Both `null` in a CI system Deployify doesn't recognize; set `DEPLOYIFY_ACTOR`. |
| `rolloutId` | The rollout journal of a health-gated deploy (see `status --rollout`), or the rollout a rollback undid. |
| `summary` | Why a release failed, or what a rollback restored. For people. |
| `images[].digest` | `null` when the image isn't pinned, such as in an environment without a container registry. |

## Using it

In a GitHub Actions step, read the plan and act on it:

```yaml
- name: Plan
  id: plan
  run: |
    deployify plan --env prod --output json > plan.json
    echo "changes=$(jq '.result.upToDate | not' plan.json)" >> "$GITHUB_OUTPUT"

- name: Apply
  if: steps.plan.outputs.changes == 'true'
  run: deployify apply --env prod --auto-approve
```

`plan` exits with `1` when it's blocked; with `set -e` (the default in Actions), add
`|| true` and read `success` and `error` from the document instead if you want to handle that
yourself.
