---
title: "DigitalOcean App Platform"
description: "Deploying an Aspire app to DigitalOcean App Platform, with Postgres and Redis on Managed Databases."
---

Target ID: `digitalocean` (a [plugin target](/Deployify/targets/plugins/))

Deploys the whole Aspire app as one [App Platform](https://docs.digitalocean.com/products/app-platform/)
app. Postgres and Redis containers become DigitalOcean Managed Databases instead of containers,
because App Platform has no persistent storage.

## Install

The target ships as a plugin, `Deployify.Target.DigitalOcean`, which you install as a .NET tool:

```bash
dotnet tool install -g Deployify.Target.DigitalOcean
deployify plugin lock digitalocean     # commit deployify-plugins.lock.json
deployify plugin list --check
```

Each release also attaches a signed package for the [plugin index](/Deployify/extending/plugins/#install-from-the-plugin-index).
Once a version is listed there, install it from the index instead: Deployify checks the
package's signature first, and CI can install the locked version with a plain
`deployify plugin install`:

```bash
deployify plugin install digitalocean  # also locks it; commit deployify-plugins.lock.json
```

Deployify reads the API token from the environment and never stores it. Create a token with read
and write access to apps and databases at
<https://cloud.digitalocean.com/account/api/tokens>:

```bash
export DIGITALOCEAN_ACCESS_TOKEN=dop_v1_...
```

## Configure

```json
"prod": {
  "Target": "digitalocean",
  "ContainerRegistry": "registry.digitalocean.com/acme",
  "ImageTag": "v1.2.0",
  "TargetConfig": {
    "digitalocean": {
      "AppName": "shop-prod",
      "Region": "ams",
      "DefaultInstanceSize": "apps-s-1vcpu-0.5gb",
      "Services": {
        "api": { "InstanceCount": 2, "HealthCheckPath": "/health" },
        "admin": { "Route": "/admin", "InstanceSize": "apps-s-1vcpu-1gb" }
      },
      "Databases": {
        "pg": { "Mode": "Cluster", "ClusterName": "shop-pg" },
        "cache": { "Size": "db-s-1vcpu-2gb" }
      }
    }
  }
}
```

| Setting | Default | |
|---|---|---|
| `AppName` | `deployify-{environment}` | The App Platform app: 2–32 lowercase letters, digits and `-`. |
| `Region` | nearest | `nyc`, `ams`, `sfo`, `sgp`, `lon`, `fra`, `tor`, `blr`, `syd` or `atl`. Required when Deployify creates a database cluster. |
| `ProjectId` | default project | The DigitalOcean project a new app is created in. |
| `TokenVariable` | `DIGITALOCEAN_ACCESS_TOKEN` | The variable holding the API token. `DIGITALOCEAN_TOKEN` is read as a fallback. |
| `DefaultInstanceSize` | `apps-s-1vcpu-0.5gb` | Instance size of services and workers. |
| `DefaultInstanceCount` | `1` | Instance count. A `serviceOverrides` `Count` wins. |
| `Services.<name>` | | `InstanceSize`, `InstanceCount`, `Route` and `HealthCheckPath` for one resource. |
| `Databases.<name>` | see below | How a Postgres or Redis resource is deployed. |
| `RegistryCredentialsVariable` | | A variable holding `username:token`, for images Deployify built into a private GHCR or Docker Hub repository. Not needed for DOCR. |
| `DeployTimeoutMinutes` | `20` | How long `apply` waits for the deployment to go live. |
| `DatabaseTimeoutMinutes` | `20` | How long `apply` waits for a new database cluster to come online. |

## How the app is built

| In the AppHost | On App Platform |
|---|---|
| A project or container with an external HTTP endpoint | A **service** with a public route |
| A project or container with other endpoints | A **service** on internal ports only |
| A project or container without endpoints | A **worker** |
| A Postgres container | A **dev database** (default) or an attached **Managed Database cluster** |
| A Redis or Valkey container | An attached **Managed Database cluster** running Valkey |

The first public service gets the route `/`, and every other public service gets `/<name>`.
Set `Services.<name>.Route` to choose a route yourself. App Platform strips the route
before it forwards a request, so `/admin/users` reaches the admin service as `/users`.

Services call each other over the app's private network at `http://<name>:<port>`. Aspire's
`{api.bindings.http.url}` references resolve to those addresses.

Images Deployify builds are pushed to `ContainerRegistry`. App Platform can only pull from the
DigitalOcean Container Registry (`registry.digitalocean.com/<registry>`), Docker Hub and GitHub
Container Registry. Log in before publishing with `doctl registry login`. When `publish`
recorded a digest, the app runs that exact image.

Secret parameters become `SECRET` environment variables, which App Platform encrypts. The spec
file Deployify writes, `deployify-output/<app>.app.json`, masks them, so it can be kept and
reviewed. It's the same spec format `doctl apps create --spec` takes.

## Databases

A Postgres or Redis container isn't deployed as a container. Deployify adds a `databases`
component instead and points every reference to it at App Platform's
[bindable variables](https://docs.digitalocean.com/products/app-platform/how-to/use-environment-variables/):

```text
ConnectionStrings__appdb = Host=${pg.HOSTNAME};Port=${pg.PORT};Username=${pg.USERNAME};Password=${pg.PASSWORD};Database=appdb;SSL Mode=Require
ConnectionStrings__cache = ${cache.HOSTNAME}:${cache.PORT},user=${cache.USERNAME},password=${cache.PASSWORD},ssl=true
```

App Platform fills these in when the app starts, so the Postgres and Redis password parameters
from the AppHost aren't used. References to the server's host, port and password parameter
(`{pg.bindings.tcp.host}`, `{pg-password.value}`) resolve to the matching variables too, and so
do the connection properties Aspire adds for each reference:

```text
APPDB_USERNAME = ${pg.USERNAME}
APPDB_URI      = postgresql://${pg.USERNAME}:${pg.PASSWORD}@${pg.HOSTNAME}:${pg.PORT}/appdb
CACHE_URI      = rediss://:${cache.PASSWORD}@${cache.HOSTNAME}:${cache.PORT}
```

Managed Valkey only accepts TLS connections, so Redis URIs use `rediss://`.

| `Mode` | |
|---|---|
| `Dev` | A dev database. It's only available for Postgres, holds one database and suits test environments. The default for Postgres. |
| `Cluster` | A Managed Database cluster attached by `ClusterName` (default `{app}-{name}`). The default for Redis. |
| `Container` | Deploy the container as it is. Its data is lost on every deploy and restart. |

In `Cluster` mode:

- When the cluster doesn't exist, `apply` creates it in the region's datacenter, using `Size`
  (default `db-s-1vcpu-1gb`), `Nodes` (default 1) and `Version`. Set `"Create": false` to have
  `plan` and `apply` stop instead.
- `apply` creates the databases the AppHost names with `AddDatabase` when they're missing,
  and adds the app to the cluster's trusted sources.
- Redis maps to the `VALKEY` engine, DigitalOcean's Redis-compatible engine. Set `Engine` to
  `REDIS` to attach an older Redis cluster.

Deployify tags the clusters it creates `deployify` and `deployify-app-<app>`. `destroy` keeps
clusters and their data unless you pass `--volumes`, and even then only deletes the
clusters carrying the app's tag. A dev database is part of the app and goes when the app does.

## Plan and cost

`plan` compares the spec with the running app component by component, then asks App
Platform's `/v2/apps/propose` endpoint to validate it and price it:

```
App Platform estimate for 'shop-prod': $24.00 per month, not counting Managed Database clusters (billed separately).
Apply will create Managed Database cluster 'shop-prod-cache' (valkey, db-s-1vcpu-1gb, 1 node(s)) for cache.
```

A spec App Platform rejects fails the plan, with App Platform's reason. `validate` runs the same
check when a token is set, and runs only the local checks when there isn't one.

Secret values can't be compared with the running app, because App Platform returns them
encrypted. A secret counts as changed only when it's added or removed, but `apply` always sends
the current values.

## Not supported (yet)

- Health-gated rollouts and `rollback`. App Platform rolls back a deployment that fails its
  health checks on its own.
- Container `args`: App Platform runs the image's own command. Deployify warns about them.
- Volumes, Dapr sidecars and the Aspire dashboard container (its image is on `mcr.microsoft.com`,
  which App Platform can't pull from).
