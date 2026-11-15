---
title: "Live resources in the Aspire Dashboard"
description: "Run a standalone resource service so the Aspire Dashboard shows a deployed environment's services, logs and start/stop/restart."
---

Under `aspire run`, the Aspire Dashboard's **Resources** page lists every resource with its state, URLs,
environment and console logs, and lets you start, stop and restart them. That data comes from a gRPC
*resource service* hosted by the AppHost. A deployed environment has no AppHost, so a standalone
dashboard only shows telemetry.

`deployify dashboard` fills that gap. It runs a standalone resource service for a deployed
environment, reads what is actually running on the target, and serves it to the dashboard in the
format the dashboard expects. It currently supports **Kubernetes** and **Nomad**.

## Quick start

```bash
# Serve the active environment's resources and start the dashboard container next to it
deployify dashboard --launch

# Or a specific environment
deployify dashboard --env prod --launch
```

`--launch` starts `mcr.microsoft.com/dotnet/aspire-dashboard` with Docker or Podman (whichever
`containerRuntime` is set to), using `dashboard.imageTag`, and points it at the resource service. Open
`http://localhost:18888`. Ctrl+C stops both.

Without `--launch`, the command prints the settings to give a dashboard you run yourself:

```
Dashboard__ResourceServiceClient__Url=http://127.0.0.1:18886/
Dashboard__ResourceServiceClient__AuthMode=ApiKey
Dashboard__ResourceServiceClient__ApiKey=…
```

## What you see

| Target | Resources | Console logs | Commands |
|---|---|---|---|
| Kubernetes | A **Deployment** per service, with its **Pods** nested under it | `kubectl logs` (all containers, with timestamps; a Deployment shows all its pods, prefixed) | Deployment: **Start** / **Stop** (scale to the remembered count / to 0), **Restart** (`rollout restart`). Pod: **Restart** (delete the pod) |
| Nomad | A **NomadJob** per service, with its active **NomadAllocations** nested under it | The main task's stdout and stderr from the client fs API (a job shows all its allocations, prefixed) | Job: **Start** (register again), **Stop**, **Restart** (every allocation in place). Allocation: **Restart**, **Reschedule** |

For each resource the dashboard shows:

- **State and health.** Kubernetes: ready replicas, `CrashLoopBackOff` and similar, a missed progress deadline. Nomad: running and healthy allocations.
- **URLs.** Kubernetes: Ingress hosts, plus the cluster-internal Service addresses marked internal. Nomad: each allocation's host IP and port.
- **Environment variables.** Kubernetes: the container's `env` and `envFrom` ConfigMaps. Nomad: the task's `env`.
- **Properties.** Image, namespace, node, revision or job version, and restarts.

Only Deployify's resources are shown. On Kubernetes that means namespaces labelled
`app.kubernetes.io/managed-by=deployify` under the environment's `namespacePrefix`. On Nomad it means
jobs carrying the `deployify-managed` meta key in the environment's namespace.

:::caution[Secrets]
Values that come from Secrets (Kubernetes `secretRef` / `secretKeyRef`) are never read. Only the
Secret's name is shown. Pass `--hide-env-values` to hide every environment value.
:::

**Stop** is a temporary change. On Kubernetes it scales the Deployment to 0 and remembers the replica
count in the `deployify/stopped-replicas` annotation, so **Start** can restore it. On Nomad it stops the
job. Either way, the next `deployify apply` brings the service back to what the AppHost says.

## Security

The resource service listens on `127.0.0.1:18886` by default and requires an API key. The key is
taken from `--api-key env:NAME` or `$DEPLOYIFY_RESOURCE_SERVICE_API_KEY`. If neither is set, a random
key is generated and printed. `--launch` passes the key to the container through the environment,
never on the command line.

- `--bind 0.0.0.0` lets other machines connect (for example a dashboard in your cluster). The
  service speaks plain HTTP/2, so put it behind TLS when it leaves a trusted network.
- `--unsecured` drops the API key and is only accepted on a loopback address.
- The commands run with **your** kubeconfig or Nomad token. Anyone who can reach the service with the
  key can stop and restart services in the environment.

On Kubernetes, the credentials need to be able to list namespaces, Deployments, Services, Ingresses,
ConfigMaps and pods across namespaces, read pod logs, and patch and scale Deployments. On Nomad, the
token needs `read-job`, `read-logs`, `submit-job` and `alloc-lifecycle` in the namespace.

## Options

See [`deployify dashboard`](/Deployify/reference/cli/dashboard/) for every option. The ones you'll
use most are `--port`, `--bind`, `--api-key`, `--poll-interval` (default 3 seconds) and `--launch`.

## Adding a target

The server only talks to one interface, `IResourceSource` in `Deployify.Core.ResourceSources`. A
target that implements `IResourceSourceProvider` can be watched. Most sources poll, so they derive
from `PollingResourceSource` and only return the current resources. The server diffs consecutive
sets into the dashboard's add, change and delete updates. See
[Adding a target](/Deployify/contributing/adding-a-target/).
