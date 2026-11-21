---
title: "Reading logs"
description: "deployify logs: one command to read a deployed service's console output, on every target."
---

`deployify logs` prints what a deployed service writes to stdout and stderr. It works the same
way on every target: you name the service as it's called in the AppHost, and Deployify finds
its pods, allocations, containers, tasks or replicas in the environment and reads their output
from the platform. You don't need to know the namespace, the job ID or the log group.

```bash
# The last 100 lines of each instance of "api" in the active environment
deployify logs api

# Keep following new lines until Ctrl+C
deployify logs api -f

# The last 20 lines of each instance, then follow
deployify logs api --tail 20 --follow

# Everything from the last half hour, with timestamps
deployify logs worker --since 30m --tail all --timestamps

# Another environment
deployify logs api --env prod
```

Each line starts with the instance it came from, such as `[api-7d9f8c6b5-x2x9z]` or
`[a1b2c3d4]`, so the output of several replicas can be told apart. `--no-prefix` leaves it out,
which helps when the app writes JSON lines. Without `--follow`, the lines of all instances
are put in time order where the platform records times.

stdout gets the log lines and nothing else. Deployify's own messages, warnings and errors go
to stderr, so the output can be piped:

```bash
deployify logs api --since 1h --no-prefix | grep -i error
```

## Options

| Option | Meaning |
|---|---|
| `--follow`, `-f` | Keep printing new lines as they are written, until Ctrl+C. |
| `--tail`, `-n` | How many recent lines to start with, per instance (default 100), or `all`. `--tail 0 -f` prints only new lines. |
| `--since` | Only lines written since then: a duration (`30s`, `10m`, `2h`, `1d`, `1h30m`) or a time (`2026-05-01T10:00:00Z`, `2026-05-01`). |
| `--timestamps` | Start each line with the time the platform recorded for it, in UTC. |
| `--no-prefix` | Don't start lines with the instance. |
| `--output json`, `-o json` | One JSON object per line ([JSON Lines](https://jsonlines.org/)): `service`, `instance`, `timestamp`, `stream` (`stdout` or `stderr`) and `text`. |

The usual `--project-path`, `--env`, `--target` and `--allow-unverified` options choose the
environment, as for every other command. `logs` only reads: it doesn't take the state lock and
doesn't build the AppHost.

## What each target reads

Each target reads the logs where its platform keeps them, with the tools it already uses to
deploy. What the platform keeps decides how far back you can go and whether lines have times.

| Target | Where the lines come from | Needs | Notes |
|---|---|---|---|
| Kubernetes, Helm, Argo CD, Flux, OVHcloud | `kubectl logs` of every pod labelled `app=<service>` in the service's namespace, all containers | `kubectl`, with the environment's kubeconfig and context | Sidecars and init containers show as `[pod/container]`. OVHcloud fetches the cluster's kubeconfig first. |
| Docker Compose | `docker logs` of the service's containers in the Compose project | `docker` | When no container is running, the most recent one is read, so a crashed service's output is still there. stdout and stderr are kept apart. |
| Nomad, Nomad + Consul | The job's main task in each running allocation, through Nomad's client filesystem API | Nomad's address and token | Nomad stores no times with log lines, so `--since` is refused and `--timestamps` has nothing to show. Without a running allocation, the newest one is read. |
| AWS ECS | The CloudWatch Logs streams the tasks write (`<service>/<service>/<task>` in the environment's log group) | `aws` | The tail comes from each running task's stream, at most 10,000 lines per task. Following uses `aws logs tail --follow` on all of the service's streams, so tasks that start later are followed too. |
| Azure Container Apps | The console log stream of each replica of the app's latest ready revision | `az` | The platform keeps at most the last 300 lines per replica, so `--tail` above 300 and `--tail all` give 300, and `--since` can only filter within those. |
| DigitalOcean App Platform (plugin) | The component's run logs, through the URLs the App Platform API hands out | the API token | Following streams the live log. |
| IIS | The ASP.NET Core Module's stdout files for the site, and its entries in the server's Application event log | PowerShell, as for deploying | The app's console output is only captured with [`iis.captureStdout`](/Deployify/targets/iis/#logs); without it, only the module's event log entries (start-up failures, crashes) are shown. Following asks the server again every few seconds. |
| Plugin targets | Whatever the plugin reads | | Needs a plugin that declares the `logs` capability; see [What a plugin target supports](/Deployify/extending/plugins/#what-a-plugin-target-supports). |

A service that isn't deployed in the environment gets a message that says so, and an exit code
of 1. A service that is deployed but has written nothing yet prints nothing, and exits with 0.

## Tips

- Logs show what a service printed, not its traces or metrics. For those, point your services
  at an OpenTelemetry collector, or use the [Aspire dashboard](/Deployify/guides/dashboard/) or
  the [resource server](/Deployify/guides/resource-server/), which also shows console logs on
  Kubernetes and Nomad.
- In CI, `--output json` with `--since` gives a log excerpt a later step can attach to a failed
  deployment's report, for example after a rollout was rolled back.
- The lines are never copied into Deployify's own log: they can hold anything the app prints.
