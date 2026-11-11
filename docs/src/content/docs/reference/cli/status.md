---
title: deployify status
description: "Show recent rollouts and how they ended."
sidebar:
  label: status
  order: 14
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Show recent rollouts and how they ended.

```
deployify status [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| ovhcloud \| compose \| awsecs \| azurecontainerapps \| nomadconsul, or the ID of a plugin target. |  |
| `--rollout` | Show the services and events of this rollout instead of the list. |  |
| `--limit` | How many rollouts to list (default: 10). | `10` |

