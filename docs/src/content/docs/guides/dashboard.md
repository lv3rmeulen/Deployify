---
title: "Aspire Dashboard"
description: "Deploying the Aspire Dashboard alongside your services."
---

Deployify can optionally inject the [Aspire Dashboard](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/overview) container into every deployment so you can monitor your services' telemetry in production.

## Enable

```bash
# During init
deployify init --non-interactive --target Nomad --deploy-dashboard

# Or update an existing environment
deployify env set local --deploy-dashboard true

# With a browser auth token, read from DEPLOYIFY_DASHBOARD_TOKEN at deploy time
deployify init --non-interactive --target Nomad \
    --deploy-dashboard \
    --dashboard-port 18888 \
    --dashboard-otlp-port 18889 \
    --dashboard-token env:DEPLOYIFY_DASHBOARD_TOKEN
```

`--dashboard-token` takes an `env:NAME` reference, so the token stays out of
`deployify-state.json`. A literal token is refused unless you pass `--allow-plaintext-secrets`.

## How it works

When `deployDashboard: true`, every `generate`/`plan`/`apply`/`ci` run injects a synthetic `aspire-dashboard` container resource into the manifest before passing it to the target generator. The resource is treated like any other container — it gets a job file, a compose service, a Kubernetes deployment, etc. depending on the active target.

The dashboard is configured with:
- `DASHBOARD__OTLP__ENDPOINT_URL` — where services should send OTLP telemetry
- `DASHBOARD__FRONTEND__ENDPOINT_URL` — the UI port
- `DASHBOARD__FRONTEND__AUTHMODE` + `DASHBOARD__FRONTEND__BROWSERTOKEN` — when a token is set

## State file

```json
{
  "environments": {
    "prod": {
      "deployDashboard": true,
      "dashboard": {
        "imageTag": "latest",
        "port": 18888,
        "otlpPort": 18889,
        "browserToken": "env:DEPLOYIFY_DASHBOARD_TOKEN"
      }
    }
  }
}
```

## Options

| Option | Default | Description |
|---|---|---|
| `deployDashboard` | `false` | Inject the dashboard into every deployment |
| `dashboard.imageTag` | `latest` | Tag for `mcr.microsoft.com/dotnet/aspire-dashboard` |
| `dashboard.port` | `18888` | Host port for the dashboard UI |
| `dashboard.otlpPort` | `18889` | Host port for the OTLP gRPC endpoint |
| `dashboard.browserToken` | `null` | Auth token as `"env:NAME"` (omit for unauthenticated) |
