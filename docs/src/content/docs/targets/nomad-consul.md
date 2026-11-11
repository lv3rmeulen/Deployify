---
title: "Nomad + Consul Connect"
description: "Deploying an Aspire app to Nomad + Consul Connect with Deployify."
---

Target ID: `NomadConsul`

Generates Nomad HCL jobs with Consul Connect service mesh. Every service uses `network { mode = "bridge" }` and a `sidecar_service` block. Inter-service dependencies become `upstreams` bound to loopback ports (starting at `upstreamBasePort`), with `NOMAD_UPSTREAM_ADDR_<SERVICE>` env vars injected automatically so applications discover peers at `localhost:<port>` without hardcoded addresses.

```json
"targetConfig": {
  "nomadconsul": {
    "nomadAddress": "http://localhost:4646",
    "nomadToken": "optional-acl-token",
    "nomadNamespace": "default",
    "datacenters": ["dc1"],
    "consulAddress": "http://localhost:8500",
    "consulToken": "optional-acl-token",
    "upstreamBasePort": 20000,
    "enableIngressGateway": false,
    "ingressGatewayName": "ingress-gateway",
    "defaultCpu": 256,
    "defaultMemory": 256
  }
}
```

Init flags: `--nc-nomad-address`, `--nc-nomad-token`, `--nc-nomad-namespace`, `--nc-datacenter`, `--nc-consul-address`, `--nc-consul-token`, `--nc-ingress-gateway`

With `enableIngressGateway`, one extra job (named `ingressGatewayName`) runs a Consul ingress
gateway with a TCP listener for every HTTP endpoint marked external in the AppHost.
`healthCheckType` works as for the `Nomad` target (`tcp` by default).
