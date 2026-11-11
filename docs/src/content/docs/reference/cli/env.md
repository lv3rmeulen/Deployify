---
title: deployify env
description: "Manage deployment environments."
sidebar:
  label: env
  order: 2
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Manage deployment environments.

```
deployify env <command>
```

## `env list`

List all configured environments.

```
deployify env list [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |

## `env use`

Switch the active environment.

```
deployify env use <name> [options]
```

**Arguments**

| Argument | Description |
|---|---|
| `<name>` | Environment name to switch to. |

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |

## `env add`

Add a new environment.

```
deployify env add <name> [options]
```

**Arguments**

| Argument | Description |
|---|---|
| `<name>` | New environment name. |

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--non-interactive` | Disable interactive prompts; fail on missing required values. | `false` |
| `--target` | **Required.** Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| compose \| awsecs \| azurecontainerapps \| nomadconsul, or the ID of a plugin target. |  |
| `--activate` | Set as active after adding. | `false` |
| `--container-registry` | Container registry. |  |
| `--image-tag` | Default image tag. |  |
| `--container-os` | Target OS for container images (e.g. linux). |  |
| `--container-arch` | Target CPU architecture: x64 \| arm64 \| arm. |  |
| `--nomad-address` | Nomad address. |  |
| `--nomad-namespace` | Nomad namespace. |  |
| `--nomad-datacenter` | Nomad datacenter. |  |
| `--nomad-token` | Nomad ACL token. |  |
| `--kubernetes-context` | kubectl context. |  |
| `--kubernetes-namespace-prefix` | Namespace prefix. |  |
| `--kubernetes-ingress-domain` | Ingress base domain. |  |
| `--compose-project` | Docker Compose project name. |  |
| `--compose-restart` | Restart policy. |  |

## `env set`

Update settings on an existing environment.

```
deployify env set <name> [options]
```

**Arguments**

| Argument | Description |
|---|---|
| `<name>` | Environment to update. |

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--container-registry` | Update container registry. |  |
| `--image-tag` | Update default image tag. |  |
| `--container-os` | Target OS for container images (e.g. linux). |  |
| `--container-arch` | Target CPU architecture: x64 \| arm64 \| arm. |  |
| `--nomad-address` | Update Nomad address. |  |
| `--nomad-token` | Update Nomad token. |  |
| `--nomad-namespace` | Update Nomad namespace. |  |
| `--nomad-datacenter` | Update Nomad datacenter. |  |
| `--nomad-registry-username` | Docker registry username (empty string to remove). |  |
| `--nomad-registry-password` | Docker registry password or token (empty string to remove). |  |
| `--nomad-register-services` | Enable (true) or disable (false) service registration. |  |
| `--nomad-service-provider` | Service provider: nomad \| consul. |  |
| `--nomad-health-check-path` | HTTP path for service health checks. |  |
| `--deploy-dashboard` | Enable (true) or disable (false) the Aspire Dashboard. |  |
| `--dashboard-tag` | Image tag for the Aspire Dashboard. |  |
| `--dashboard-port` | Host port for the dashboard UI. |  |
| `--dashboard-otlp-port` | Host port for the OTLP gRPC endpoint. |  |
| `--dashboard-token` | Browser auth token (empty string to remove). |  |
| `--deploy-dapr` | Enable (true) or disable (false) Dapr sidecar injection. |  |
| `--dapr-version` | Dapr sidecar image tag. |  |
| `--dapr-components-path` | Path to Dapr components directory. |  |
| `--dapr-config-path` | Path to Dapr config file. |  |
| `--dapr-log-level` | Dapr sidecar log level. |  |

## `env remove`

Remove an environment.

```
deployify env remove <name> [options]
```

**Arguments**

| Argument | Description |
|---|---|
| `<name>` | Environment to remove. |

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--force` | Skip confirmation prompt. | `false` |

