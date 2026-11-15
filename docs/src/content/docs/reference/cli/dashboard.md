---
title: deployify dashboard
description: "Serve the environment's deployed resources to the Aspire dashboard (a standalone resource service)."
sidebar:
  label: dashboard
  order: 16
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Serve the environment's deployed resources to the Aspire dashboard (a standalone resource service).

```
deployify dashboard [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| ovhcloud \| compose \| awsecs \| azurecontainerapps \| nomadconsul \| iis, or the ID of a plugin target. |  |
| `--port` | Port the resource service listens on (default: 18886). | `18886` |
| `--bind` | Address to listen on: an IP address, localhost, or 0.0.0.0 for every interface (default: 127.0.0.1). | `127.0.0.1` |
| `--api-key` | API key the dashboard must send, as env:NAME. Default: $DEPLOYIFY_RESOURCE_SERVICE_API_KEY, or a random key printed at start. |  |
| `--unsecured` | Accept every caller without an API key. Only allowed when listening on a loopback address. | `false` |
| `--poll-interval` | Seconds between reads of the target (default: 3). | `3` |
| `--hide-env-values` | Show environment variable names without their values. Secret values are never shown. | `false` |
| `--launch` | Also start the Aspire dashboard container (Docker or Podman) pointed at the resource service. | `false` |
| `--dashboard-port` | With --launch: host port for the dashboard UI (default: dashboard.port, 18888). |  |

