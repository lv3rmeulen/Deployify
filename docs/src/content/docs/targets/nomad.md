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
  (starting at `basePort`, default 8080). Services find each other at `localhost:<port>`, which
  assumes a single Nomad client node. For multi-node clusters use the `NomadConsul` target.
- **Health checks.** `healthCheckType` is `tcp` (default), `http` (uses `healthCheckPath`,
  default `/health`) or `none`. Aspire's `MapDefaultEndpoints()` only maps `/health` in the
  Development environment, so `http` checks fail in production unless you map it yourself.
- **Private registries.** `registryUsername` / `registryPassword` add an `auth` block for
  images that aren't on a public registry. The password is never written to the generated
  job file — the file holds a placeholder and the real value is filled in, in memory, when
  the job is sent to Nomad. If `registryPassword` is empty, `DEPLOYIFY_REGISTRY_PASSWORD` is used.
- **Ownership.** Generated jobs carry `meta { deployify-managed = "true" }`; `plan` only ever
  proposes deleting jobs with that marker.
