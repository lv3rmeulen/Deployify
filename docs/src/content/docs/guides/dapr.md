---
title: "Dapr"
description: "Injecting Dapr sidecars into every service."
---

Deployify can inject a [Dapr](https://dapr.io) sidecar alongside every service at deployment time — no changes to your application code or Aspire AppHost required.

## Enable

```bash
# During init
deployify init --non-interactive --target Nomad --deploy-dapr

# Or update an existing environment
deployify env set local --deploy-dapr true

# With custom version and components path
deployify init --non-interactive --target Nomad \
    --deploy-dapr \
    --dapr-version 1.13 \
    --dapr-components-path /etc/dapr/components \
    --dapr-config-path /etc/dapr/config.yaml
```

## How it works

When `deployDapr: true`, every `generate`/`plan`/`apply`/`ci` run injects Dapr sidecar annotations (`dapr/enabled`, `dapr/app-id`, `dapr/app-port`, `dapr/http-port`, `dapr/grpc-port`) into resources that don't already have them. Resources that already carry Dapr annotations from the Aspire Dapr integration are left untouched.

Each target then handles the sidecar in the platform-native way:

| Target | Dapr implementation |
|---|---|
| **Nomad** | Extra `daprd` task in the same task group with `lifecycle { hook = "prestart"; sidecar = true }` |
| **NomadConsul** | Same as Nomad, shares the bridge network with the app and Connect sidecar |
| **Kubernetes** | `dapr.io/*` annotations on the pod template — Dapr control plane injects the sidecar automatically |
| **Compose** | `<service>-dapr` container with `network_mode: service:<name>` so it shares the app's network namespace |
| **ACA** | Native `dapr:` property on the Container App spec |
| **ECS** | `<service>-dapr` essential=false sidecar container in the task definition |

## Aspire Dapr integration

If you use the Aspire Dapr integration (`builder.AddDapr()`), your manifest will already contain `dapr/enabled` annotations. Deployify reads these automatically — you do not need to set `deployDapr: true`. The annotations from the manifest take precedence over any defaults from `DaprConfig`.

## State file

```json
{
  "environments": {
    "prod": {
      "deployDapr": true,
      "dapr": {
        "daprVersion": "1.13",
        "daprHttpPort": 3500,
        "daprGrpcPort": 50001,
        "componentsPath": "/etc/dapr/components",
        "configPath": "/etc/dapr/config.yaml",
        "logLevel": "info"
      }
    }
  }
}
```

## Options

| Option | Default | Description |
|---|---|---|
| `deployDapr` | `false` | Inject Dapr sidecars into all services |
| `dapr.daprVersion` | `latest` | Tag for `daprio/daprd` |
| `dapr.daprHttpPort` | `3500` | Base Dapr HTTP port (incremented per service) |
| `dapr.daprGrpcPort` | `50001` | Base Dapr gRPC port (incremented per service) |
| `dapr.componentsPath` | `null` | Path to Dapr components directory |
| `dapr.configPath` | `null` | Path to Dapr config file |
| `dapr.logLevel` | `info` | Sidecar log level |
