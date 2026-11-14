---
title: "Nomad"
description: "Deploying an Aspire app to Nomad with Deployify."
---

Target ID: `Nomad`

Generates one HCL job file per service using the Docker driver. Submits jobs via the Nomad HTTP API.

```json
"targetConfig": {
  "nomad": {
    "address": "http://localhost:4646",
    "token": "optional-acl-token",
    "namespace": "default",
    "datacenters": ["dc1"],
    "deploymentMode": "Docker",
    "healthCheckType": "tcp",
    "registryUsername": "myuser",
    "registryPassword": "env:REGISTRY_PASSWORD"
  }
}
```

Init flags: `--nomad-address`, `--nomad-token`, `--nomad-namespace`, `--nomad-datacenter`

Notes:

- **Networking.** Docker tasks use host networking and each service gets its own static port
  (starting at `basePort`, default 8080). Services find each other through the service catalog,
  so jobs can run on different nodes: a reference such as `{api.bindings.http.url}` is written
  into the task's `template` block, which looks up the node `api` is registered on
  (`nomadService`, or Consul's `service` when `serviceProvider` is `consul`) when the task
  starts. If a dependency moves, the task restarts with its new address. This needs
  `registerServices` (on by default) and Nomad 1.4 or later. With `registerServices: false`,
  services reach each other on `localhost:<port>`, which only works on a single node.
  Container `args` can't be templated, so a service address in `args` stays on `localhost`;
  pass it as an environment variable instead. For mTLS between services use the `NomadConsul` target.
- **Health checks.** `healthCheckType` is `tcp` (default), `http` (uses `healthCheckPath`,
  default `/health`) or `none`. Aspire's `MapDefaultEndpoints()` only maps `/health` in the
  Development environment, so `http` checks fail in production unless you map it yourself.
- **Private registries.** `registryUsername` / `registryPassword` add an `auth` block for
  images that aren't on a public registry. The password is never written to the generated
  job file — the file holds a placeholder and the real value is filled in, in memory, when
  the job is sent to Nomad. If `registryPassword` is empty, `DEPLOYIFY_REGISTRY_PASSWORD` is used.
- **Ownership.** Generated jobs carry `meta { deployify-managed = "true" }`; `plan` only ever
  proposes deleting jobs with that marker.
