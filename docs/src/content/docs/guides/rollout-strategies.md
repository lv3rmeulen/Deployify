---
title: "Canary and blue-green rollouts"
description: "Rolling, canary and blue-green rollout strategies, per target."
---

By default every service is deployed with its platform's rolling update: old instances are
replaced a few at a time, gated on health as described in
[health-gated rollouts](/Deployify/guides/rollouts/). Two other strategies run the new version
next to the old one first:

- **Canary**: the new version gets a growing share of the traffic, step by step, and has to stay
  healthy at each step. Any failure sends all traffic back to the old version.
- **Blue-green**: the new version starts at full size with no traffic. Once it's healthy (and,
  optionally, after a preview period), it gets all the traffic at once. The old version is kept
  for a while, so going back is instant.

## Configuring a strategy

Set `rollout` on an environment in `deployify-state.json`, or on one service in
`serviceOverrides` (a service's `rollout` replaces the environment's as a whole):

```json
"rollout": {
  "strategy": "Canary",
  "canary": {
    "steps": [
      { "weight": 10, "pause": "5m" },
      { "weight": 50, "pause": "10m" }
    ]
  }
},
"serviceOverrides": {
  "web": {
    "rollout": { "strategy": "BlueGreen", "blueGreen": { "previewPause": "10m", "scaleDownDelay": "5m" } }
  },
  "worker": { "rollout": { "strategy": "Rolling" } }
}
```

| Setting | Default | Meaning |
|---|---|---|
| `strategy` | `Rolling` | `Rolling`, `Canary` or `BlueGreen` |
| `canary.steps[].weight` | | Percentage of the traffic for the new version in this step, 1 to 99, rising from step to step. After the last step it gets 100%. |
| `canary.steps[].pause` | `5m` | How long to stay at that weight while watching health: `30s`, `5m`, `1h30m` |
| `canary.steps` | one step: 10% for 5m | |
| `blueGreen.previewPause` | `0` | How long the new version runs without traffic before the switch, so it can be tried at its preview address |
| `blueGreen.scaleDownDelay` | `1m` | How long the old version is kept after the switch while the new one is watched |

`plan` prints each service's strategy and fails when a strategy can't run (see the table below),
so CI catches it before anything is published. A strategy that can't run never falls back to a
rolling update silently.

A service deployed for the first time is created directly; its strategy applies from its next
deploy.

## What each target does

| Target | Canary | Blue-green | Who moves the traffic | Traffic precision |
|---|---|---|---|---|
| Kubernetes, OVHcloud | ✓ | ✓ | Deployify | by number of pods |
| Helm | ✓ | — | Deployify | by number of pods |
| Nomad, Nomad + Consul | ✓ | ✓ | Deployify (Nomad canaries) | set once, by number of canaries |
| Azure Container Apps | ✓ | ✓ | Deployify | exact |
| Google Cloud Run | ✓ | ✓ | Deployify | exact |
| AWS ECS | ✓ (CANARY, LINEAR) | ✓ | ECS | exact |
| Argo CD | ✓ | ✓ | Argo Rollouts | by number of pods |
| Flux | ✓ (needs a mesh or ingress provider) | ✓ | Flagger | exact with a provider |
| Docker Compose, IIS, Host, DigitalOcean, plugins | — | — | | |

Docker Compose and IIS run on one host with no traffic splitting, and the host target leaves
traffic between its hosts to your load balancer; DigitalOcean App Platform has none either. Plugin targets can't declare strategies yet.

### Strategies Deployify runs

On Kubernetes, OVHcloud, Helm, Nomad, Container Apps and Cloud Run, Deployify runs the steps
itself, as part of the [health-gated rollout](/Deployify/guides/rollouts/):

1. It starts the new version (the **candidate**) next to the running one and waits for it to be
   healthy.
2. Canary: for each step it moves the step's share of the traffic to the candidate, waits for it
   to be healthy again, then watches it for the step's pause. Blue-green: it waits for
   `previewPause`, moves all traffic to the candidate, and watches it for `scaleDownDelay`.
3. It **promotes** the candidate, leaving the service exactly as a plain apply would, waits for
   that to be healthy, and removes what's left of the old version.

A failed or unhealthy observation at any point, a timeout or Ctrl+C restores every service the
rollout touched, and for the service in progress that means: all traffic back to the old version
and the candidate removed. During a pause, two unhealthy observations in a row count as a
failure. `deployify rollback` does the same from the journal later. These strategies need the
health gate, so they can't be combined with `--no-health-gate`.

The engine reports each step:

```text
api: canary 10% 5m → 50% 10m → 100%: starting the new version next to 41
api: candidate api-canary started
api: step 1/2: 25% of the traffic (asked for 10%) for 5m
api: step 2/2: 50% of the traffic for 10m
api: promoting the candidate
✓ api: healthy (canary complete)
```

`deployify status --rollout <id>` shows each service's strategy and the share its candidate got
last, and `--output json` adds `strategy`, `candidate`, `trafficPercent` and `previewUrl` to each
service.

**Kubernetes and OVHcloud.** No service mesh is needed. A canary is a second Deployment,
`<service>-canary`, with the same `app` label, so the service's Service spreads requests over the
old and new pods. Its share follows from the number of pods: with N pods running, c canary pods
get c / (N + c) of the requests, so weights are rounded, and the first canary pod gets traffic as
soon as it is ready. The output says what share each step really got. Blue-green starts a full-size
`<service>-preview` Deployment and a `<service>-preview` Service to try it at
(`http://<service>-preview.<namespace>.svc.cluster.local:<port>`). The switch points the service's
Service selector at the preview's pods. Promotion applies the generated manifests, so the
service's own Deployment rolls out the new version (for blue-green while the Service still points
at the preview, so no request reaches a half-updated Deployment), then the candidate is deleted.
The candidate reads a copy of the new ConfigMap (`<service>-config-candidate`), so the running pods
keep their values until promotion.

**Helm.** Canary works as on Kubernetes: the canary Deployment is applied with kubectl next to
the release, and promotion is a normal `helm upgrade`. Blue-green isn't offered, because
`helm upgrade` puts the Service selector back on the release's pods while they are still being
replaced.

**Nomad and Nomad + Consul.** The job's update block asks for canaries (`canary`,
`auto_promote = false`), and Deployify promotes them. Nomad starts the canaries next to the
running allocations, and they register with your services like any other allocation, so their
share of the traffic is set by their number: enough canaries for the first step's weight, or a
full second set for blue-green. Nomad can't change that number during the deployment, so later
canary steps only wait, and with blue-green both sets serve until promotion. To keep traffic off
the canaries (a real blue-green), have your router leave out instances tagged `deployify-canary`:
unpromoted canaries register with that tag only. Canaries need room: with static ports, each one
needs a node where the port is free. Rolling back fails the canary deployment, then reverts the
job. If that version also uses canaries, Deployify promotes them as soon as they are healthy.

**Azure Container Apps.** Apps with a canary or blue-green strategy run in multiple-revision mode.
The candidate is deployed with all traffic pinned to the running revision and labeled
`candidate`, which makes it reachable at `https://<app>---candidate.<environment domain>`. Weights
are exact (`az containerapp ingress traffic set`). Promotion applies the generated spec, which
sends all traffic to the latest revision, and the old revision is then deactivated. A rollback
during the rollout sends all traffic back to the old revision, which still runs. The app needs an
HTTP ingress. An app that goes back to `Rolling` is set to single-revision mode again.

**Google Cloud Run.** Every deploy creates a revision, and traffic is split between revisions by
exact percentages. The candidate is deployed with all traffic left on the running revision and
tagged `candidate` (`https://candidate---<service host>`). Promotion applies the generated spec,
which sends all traffic to the latest revision. Old revisions stay, with the secret versions they
were deployed with, so a rollback only moves traffic. See [Google Cloud Run](/Deployify/targets/cloud-run/).

### Strategies the platform runs

**AWS ECS.** Deployify sets the service's deployment strategy, and ECS shifts the traffic and
rolls back on its own:

- blue-green → `BLUE_GREEN`, with `bakeTimeInMinutes` from `scaleDownDelay`
- one canary step → `CANARY`, with `canaryPercent` and `canaryBakeTimeInMinutes` from the step
- equal steps that go all the way up (20%, 40%, 60%, 80%, all with the same pause) → `LINEAR`

Bake times are whole minutes. ECS shifts canary traffic through Service Connect
(`aws.serviceConnectNamespace`), the way Deployify connects ECS services. Blue-green also works
without Service Connect, but then ECS doesn't move any traffic itself. `previewPause` isn't
supported (ECS only waits before the switch through lifecycle hooks). The circuit breaker only
works with rolling updates, so it isn't sent for these strategies, and a service that goes back to
`Rolling` gets `strategy=ROLLING` explicitly. The health gate waits for ECS to finish, with the
strategy's pauses added to `--health-timeout`.

**Argo CD (Argo Rollouts).** For a service with a strategy, Deployify commits an Argo Rollouts
`Rollout` (`rollout.yaml`) next to its manifests. The Rollout takes over the Deployment through
`workloadRef` and scales it down as it goes. Canary steps become `setWeight` and `pause` steps.
Without a traffic router, Argo Rollouts meets weights by the number of pods. Blue-green uses the
service's Service as `activeService` and a committed `<service>-preview` Service as
`previewService`, with `autoPromotionSeconds` from `previewPause` and `scaleDownDelaySeconds` from
`scaleDownDelay`. The service's Argo CD Application gets `ignoreDifferences` for the Deployment's
replicas and the Services' selectors (with `RespectIgnoreDifferences=true`), so self-heal doesn't
undo what Argo Rollouts changes. Argo Rollouts must be installed in the cluster.

**Flux (Flagger).** For a service with a strategy, Deployify commits a Flagger `Canary`
(`canary.yaml`). The service's own Service is left out of the tree, because Flagger creates
`<service>`, `<service>-primary` and `<service>-canary` itself. The Deployment's replica count
moves to a HorizontalPodAutoscaler pinned at that count, which Flagger copies for the primary.
Canary steps become `stepWeights`; Flagger waits the same `interval` after every step, so all
steps need the same pause. Canary weights need a provider that routes traffic: set
`targetConfig.flux.flagger.provider` (`nginx`, `istio`, `linkerd`, `gatewayapi:v1`, …). The
default, `kubernetes`, only does blue-green, which runs `previewPause` worth of checks at
`flagger.interval` (default `1m`) before promoting. Flagger has no scale-down delay. Flagger must
be installed in the cluster. Switching an existing service to Flagger recreates its Service,
briefly.

With either controller, `gitops.wait` waits the strategy's pauses on top of `waitTimeoutSeconds`.
