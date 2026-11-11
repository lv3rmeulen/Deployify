---
title: "Health-gated rollouts and rollback"
description: "How apply deploys in waves, gates on health, and rolls back."
---

On every target (**AWS ECS**, **Nomad**, **Nomad + Consul**, **Kubernetes**, **Azure Container Apps** and **Docker Compose**), `apply` and `ci` deploy services in
dependency order, one wave at a time, and wait for each wave to become healthy before moving on.
If a service fails, doesn't become healthy within the timeout, or the run is canceled with
Ctrl+C, every service the rollout touched is put back on the revision it ran before, in reverse
order, and each restored service is checked for health again.

```bash
deployify apply --auto-approve --health-timeout 300   # seconds per service (default 600)
deployify apply --auto-approve --no-rollback          # gate on health, leave a failure in place
deployify apply --auto-approve --no-health-gate       # old behavior: deploy and return
deployify apply --auto-approve --rollback-timeout 600 # budget for the whole rollback (default 900)
```

Exit codes: `0` healthy, `1` failed (nothing rolled back), `2` failed and rolled back,
`3` failed and the rollback did not complete — check the services listed in the output.

Every rollout writes a journal to `.deployify/rollouts/<id>.json` next to `deployify-state.json`
with what ran before, what was deployed and what happened; a lock file there stops two rollouts
to the same environment from running at once. Add `.deployify/` to your `.gitignore`.

Use a unique image tag per release: with `latest`, a rollback re-deploys the same tag, which may
already point at the new image. The rollout warns when it sees a mutable tag.

On ECS the deployment circuit breaker with rollback is turned on by default
(`"deploymentCircuitBreaker": false` under `targetConfig.ecs` turns it off). When ECS reverts a
service itself, Deployify waits for the old task definition to be healthy instead of rolling back
a second time. Rolling back also puts the service's SSM parameters back: the journal records each
parameter's version (never its value), and the rollback writes that version's value back as the
newest version before the tasks restart. SSM keeps a parameter's last 100 versions.

On Nomad, jobs get `auto_revert = true` in their update block by default (`"autoRevert": false`
under `targetConfig.nomad` or `targetConfig.nomadconsul` turns it off). A revision is a job
version; rolling back reverts the job, which Nomad registers as a new version, and Deployify waits
for that one. Job secrets in Nomad Variables are put back first. Nomad keeps no history of a
variable, so before a rollout changes a job, Deployify copies its secrets to
`deployify/rollback/<job>/v<version>` (a path the job's own tasks can't read; the newest 5 copies
are kept) and the journal records only that path, which lets `deployify rollback` restore them
later too. The Deployify token needs write access to `deployify/*` variables; without it, secrets
are only restored by a rollback in the same run. With Nomad + Consul, a service that registers a Consul check must also have a
passing instance in Consul before it counts as healthy.

On Kubernetes, rollouts need `kubectl` (without it, `apply` uses the API fallback as before, with no
health gate). A service is healthy when its Deployment has caught up with the change and every pod
is updated and available, the same test `kubectl rollout status` uses; a Deployment that exceeds its
progress deadline fails. Rolling back puts the service's ConfigMap and Secret back first, then runs
`kubectl rollout undo` to the captured revision and restores the replica count; a Deployment that
the rollout created is scaled to zero. So that a later `deployify rollback` can restore them too,
the rollout keeps copies of the ConfigMap and Secret in the service's namespace
(`<service>-config-rollback-g<generation>`, `<service>-secrets-rollback-g<generation>`, labeled
`deployify/rollback-of=<service>`; the newest 5 are kept), and the journal records only their
names. Key Vault–backed Secrets are managed by the CSI driver and aren't copied. Kubernetes keeps 10 old ReplicaSets by default (`revisionHistoryLimit`); older revisions can't be
rolled back to.

On Azure Container Apps, a revision is the Container Apps revision name. A revision is healthy once
it is provisioned, active and running (scaled to zero counts), isn't reported unhealthy, and the
app has made it its latest ready revision. Rolling back runs `az containerapp revision copy` from
the captured revision, which creates a new revision with the old template, and waits for that one;
an app the rollout created is deleted. With the Key Vault secret backend, the app's secrets are put
back first: the journal records each secret's version (never its value), and the rollback writes
that version's value back as the newest version, since apps reference secrets without a version.
Other app-level settings outside the revision template (ingress, secret definitions) are not
rolled back. Without probes, Container Apps can only report whether
containers start, so add probes to get a meaningful gate.

On Docker Compose, a revision is the image ID (`sha256:…`) the service's containers run, so a
rollback works even with a `latest` tag as long as the old image is still in the local image store
(don't `docker image prune` between deploys). Services are brought up one at a time with
`up -d --no-deps`, and once all are healthy a final whole-project `up -d --remove-orphans` removes
services no longer in the manifest, as a plain apply does. A service is healthy when its containers
run the new image, aren't restarting, and pass their healthcheck if they have one
(`generateHealthChecks`); a container that exits, restarts twice or turns unhealthy fails the
rollout. Rolling back brings the service back up from the same compose files with a small
`rollback.<service>.override.yml` next to them that pins the old image ID; a service the rollout
created has its containers removed. The environment comes back too: before changing a service,
the rollout saves its container's environment to `.deployify-rollback/<service>.<image>.json` next
to the compose files (owner-readable only, like the generated `.env` files, since it holds the same
secrets; the newest 5 per service are kept), and the override replaces the service's environment
with it. That uses Compose's `!override` tag, which needs Docker Compose 2.24.4 or later.

```bash
deployify status                        # recent rollouts in this environment
deployify status --rollout <id>         # per service: before, deployed, restored, detail
deployify rollback --auto-approve       # undo the latest rollout from its journal
deployify rollback --rollout <id>       # undo a specific one
```

`rollback` restores every service the rollout touched that isn't already back, in reverse order,
and waits for each one to be healthy. It exits `0` when everything is restored and `3` when a
service could not be.
