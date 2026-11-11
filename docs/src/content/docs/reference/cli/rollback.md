---
title: deployify rollback
description: "Undo a rollout: restore the revisions that ran before it."
sidebar:
  label: rollback
  order: 15
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Undo a rollout: restore the revisions that ran before it.

```
deployify rollback [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| compose \| awsecs \| azurecontainerapps \| nomadconsul, or the ID of a plugin target. |  |
| `--rollout` | Id of the rollout to undo (see 'deployify status'). Defaults to the latest one. |  |
| `--auto-approve` | Roll back without asking for confirmation. Required when there is no interactive terminal. | `false` |
| `--health-timeout` | Seconds each service may take to become healthy before the rollout fails (default: 600). | `600` |
| `--rollback-timeout` | Seconds the whole rollback may take (default: 900). | `900` |

