---
title: deployify logs
description: "Show a deployed service's console output (stdout and stderr), on any target."
sidebar:
  label: logs
  order: 17
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Show a deployed service's console output (stdout and stderr), on any target.

```
deployify logs <service> [options]
```

**Arguments**

| Argument | Description |
|---|---|
| `<service>` | The service, by its name in the AppHost. |

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| ovhcloud \| compose \| awsecs \| azurecontainerapps \| nomadconsul \| iis, or the ID of a plugin target. |  |
| `--allow-unverified` | Start target plugins that deployify-plugins.lock.json has no hash for, with a warning each time (a plugin whose files don't match its locked hash is still refused). Run 'deployify plugin lock' instead to verify them. | `false` |
| `--follow`, `-f` | Keep printing new lines as they are written, until Ctrl+C. | `false` |
| `--tail`, `-n` | How many recent lines to start with, per instance, or 'all' (default: 100). | `100` |
| `--since` | Only lines written since then: a duration such as 30s, 10m, 2h or 1d, or a time such as 2026-05-01T10:00:00Z. |  |
| `--timestamps` | Start each line with the time the platform recorded for it (UTC), where it records one. | `false` |
| `--no-prefix` | Don't start each line with the instance (pod, allocation, container, …) it came from. | `false` |
| `--output`, `-o` | Output format: text \| json. With json, every line is a JSON object on a line of its own (JSON Lines): service, instance, timestamp, stream and text. | `Text` |

