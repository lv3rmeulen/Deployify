---
title: "Drift alerts"
description: "deployify drift: scheduled checks that tell you when an environment no longer matches what was deployed to it."
---

Someone scales a deployment by hand during an incident, edits an environment variable in the
cloud console, or deletes a service to "try something". The environment keeps running, but it
no longer matches what Deployify deployed, and the next deploy will quietly undo the change or
fail on it. `deployify drift` finds these changes and tells you about them.

```bash
# Check the active environment
deployify drift

# Check every environment in deployify-state.json
deployify drift --all

# Check without sending alerts (for a look by hand)
deployify drift --env prod --no-notify
```

| Exit code | Meaning |
|---|---|
| `0` | Nothing drifted (environments that were never deployed count as fine). |
| `2` | At least one environment drifted. |
| `1` | A check couldn't run (for example, the target is unreachable), some services couldn't be compared, or an alert couldn't be delivered to any of its channels. |

A drift takes precedence: a run where one environment drifted and another couldn't be checked
exits with `2`. `--output json` gives every environment's result for scripts; see
[JSON output](/Deployify/reference/json-output/#drift).

## What it compares

Every successful deploy (`apply`, `ci` and `aspire deploy`) records what it deployed in the
environment's deployment state: the Aspire manifest, the environment's settings and the images,
pinned by digest. A drift check regenerates the artifacts from that record and asks the target
how the live state differs, the same way `deployify diff` does.

That means a drift is a change made **outside Deployify since the last deploy**. A change to
the AppHost or `deployify-state.json` that hasn't been deployed yet is not drift; `plan` and
`diff` show those.

| In the output | Meaning |
|---|---|
| `- missing` | Deployed, but no longer on the target. |
| `+ extra` | On the target, but not part of what was deployed. |
| `~ changed` | On the target, with settings that differ from what was deployed. |
| `? unknown` | The target couldn't compare this service; the reason is shown. |

Each target compares what it can, exactly as `diff` does: Kubernetes and Helm show a rendered
`kubectl diff` or `helm diff`, Nomad uses Nomad's job plan, and the cloud targets compare the
fields their APIs return. For Argo CD and Flux, Deployify compares the Git repository the
controller deploys from; drift in the cluster itself is the controller's to report.

A few things are worth knowing:

- **Nothing is changed**: not the target, not the working tree, not the parameter or secret
  stores. The regenerated artifacts go into a scratch folder under the output path that is
  removed afterward.
- **The deploy's own settings are used**, including its parameter values, so the check needs
  the same secrets in its environment as the deploy did (`env:` parameters, registry and
  target credentials).
- **A deploy in progress is skipped.** While a deploy holds the environment's lock, the check
  reports `skipped` instead of calling the half-finished rollout drift.
- **A failed deploy doesn't move the baseline.** After a failed deploy, the environment is
  compared with the last deploy that succeeded, so whatever the failed one left behind shows up
  as drift until a deploy succeeds. A deploy rolled back by its health gate leaves the
  environment as the previous deploy left it, which is what the check compares against.
- **`destroy` clears the baseline**: there is nothing to compare against until the next
  deploy.
- **Remote state** is needed when the check runs anywhere other than the machine that
  deployed, such as a scheduled CI job. See [remote state](/Deployify/concepts/state/#remote-state).

Environments deployed before your Deployify version had drift checks report `notDeployed` until
their next deploy records a baseline.

## Alerts

Add a `drift` section to an environment, or to a base environment that others
[extend](/Deployify/reference/state-file/#layered-environments), to send alerts:

```json
"prod": {
  "target": "Kubernetes",
  "drift": {
    "notify": [
      { "type": "Slack", "url": "env:SLACK_DRIFT_WEBHOOK" },
      { "type": "Teams", "url": "env:TEAMS_DRIFT_WEBHOOK", "name": "ops channel" },
      {
        "type": "Webhook",
        "url": "https://events.example.com/deployify",
        "headers": { "Authorization": "env:DRIFT_WEBHOOK_TOKEN" }
      }
    ],
    "remindAfterHours": 24,
    "ignore": [ "*:desired_count", "batch-worker" ]
  }
}
```

| Setting | Default | |
|---|---|---|
| `enabled` | `true` | `false` leaves the environment out of `drift --all` (a dev environment people change by hand). `--env` still checks it. |
| `notify` | none | Where alerts go. Without channels, the check still reports and sets the exit code. |
| `remindAfterHours` | none | Alert again about a drift that hasn't changed after this many hours. Without it, each drift is alerted once. |
| `notifyOnResolve` | `true` | Alert when an environment you were alerted about matches its deploy again. |
| `notifyOnError` | `true` | Alert when the check can't tell: the target is unreachable, or services couldn't be compared. |
| `ignore` | none | Differences that are expected. See [ignoring expected differences](#ignoring-expected-differences). |

Webhook URLs are secrets: anyone who has one can post to your channel. `deployify-state.json` is
committed, so write `"env:NAME"` to read a URL (or a header value) from an environment variable.

### When an alert is sent

Checks run on a schedule, so the same drift is found many times. Deployify alerts about
**changes**, not about every check:

| Event | When |
|---|---|
| `drifted` | An environment that matched its deploy (or was never checked) has drifted. |
| `changed` | It was already drifted, and what drifted is different now. |
| `reminder` | It is still drifted, or the check is still failing, after `remindAfterHours`. |
| `resolved` | It matches its deploy again after an alert about it, for example after a redeploy. |
| `failing` | The check can't tell (unreachable target, services that couldn't be compared). |

The result of the last check and the last alert are kept in the environment's deployment state
(`{prefix}/{env}/drift.json`), so this works whichever machine runs the check. If no channel
accepts an alert, nothing is recorded and the next check sends it again; the run exits with `1`
so the failure is noticed.

`--no-notify` checks without sending alerts **and** without recording the result, so a check
by hand doesn't use up the alert the scheduled job would send.

### Channels

- **Slack**: an [incoming webhook](https://api.slack.com/messaging/webhooks). The message lists
  up to ten differences, the deploy it was compared with, and a link to the CI run.
- **Teams**: a workflow built from the "Send webhook alerts to a channel" template (or any
  workflow with the "When a Teams webhook request is received" trigger). The message is an
  Adaptive Card.
- **Webhook**: any URL. Deployify posts the whole alert as JSON, for PagerDuty, Opsgenie or your
  own handler:

```json
{
  "source": "deployify",
  "kind": "drift",
  "event": "drifted",
  "title": "prod drifted from its last deploy: 1 changed, 1 missing",
  "environment": "prod",
  "target": { "id": "Kubernetes", "name": "Kubernetes" },
  "status": "drifted",
  "previousStatus": "inSync",
  "since": "2026-11-22T09:00:00+00:00",
  "checkedAt": "2026-11-22T09:00:00+00:00",
  "fingerprint": "3f9a0c1d2e4b5a69",
  "reason": null,
  "ignored": 0,
  "runUrl": "https://github.com/acme/shop/actions/runs/42",
  "deployed": {
    "releaseId": "a1b2c3d4e5f6",
    "at": "2026-11-20T14:02:11+00:00",
    "tag": "4f2c9e1a7b3d5e6f7a8b9c0d1e2f3a4b5c6d7e8f",
    "commit": "4f2c9e1a7b3d5e6f7a8b9c0d1e2f3a4b5c6d7e8f",
    "actor": "lv3rmeulen",
    "runUrl": "https://github.com/acme/shop/actions/runs/40"
  },
  "entries": [
    { "service": "redis", "kind": "missing", "field": null, "live": null, "deployed": null, "reason": null },
    { "service": "worker", "kind": "changed", "field": "desired_count", "live": "5", "deployed": "2", "reason": null }
  ]
}
```

`fingerprint` is the same for the same differences, so a handler can deduplicate on it.
Secret values Deployify resolved are masked as `****` in `live` and `deployed`. A request that
doesn't answer within 20 seconds, or answers with a status other than 2xx, counts as not
delivered.

### Ignoring expected differences

Some differences are on purpose: a count an autoscaler manages, or a service an operator tunes
by hand. List them under `ignore` so they never count as drift:

| Entry | Ignores |
|---|---|
| `worker` | every difference in `worker` |
| `worker:desired_count` | that field of `worker` (fields are named as `deployify diff` names them) |
| `*:desired_count` | that field in every service |

Ignored differences are counted in the output. A service that couldn't be compared is never
ignored, since there is no telling what changed.

## Running it on a schedule

### GitHub Actions

The [Deployify action](/Deployify/guides/github-action/#drift-checks) checks every environment
when it runs on a `schedule`, opens an issue for each environment that drifted, and closes it
when the environment matches its deploy again:

```yaml
# .github/workflows/drift.yml
name: Drift
on:
  schedule:
    - cron: '17 * * * *' # every hour
  workflow_dispatch:

permissions:
  contents: read
  issues: write

jobs:
  drift:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6
      - uses: lv3rmeulen/Deployify@v1
        env:
          SLACK_DRIFT_WEBHOOK: ${{ secrets.SLACK_DRIFT_WEBHOOK }}
          # …and the same target, remote-state and parameter credentials as the deploy job.
```

### Other CI systems and cron

Run `deployify drift --all` from a scheduled pipeline with the same credentials as the deploy
job. The exit code fails the run when something drifted; the alerts go to the channels in
`drift.notify`.

### A long-running container

`--every` keeps the command running and checks again at an interval, for a container or a
service on a machine that can reach the targets:

```bash
deployify drift --all --every 30m
```

Intervals are written as `90s`, `15m`, `1h` or `1d` (at least one minute, since each check
asks the target for its whole state). It stops on Ctrl+C or SIGTERM. `--every` can't be combined
with `--output json`, which writes one document when the command ends.
