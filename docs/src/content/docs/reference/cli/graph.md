---
title: deployify graph
description: "Display a dependency graph of all services."
sidebar:
  label: graph
  order: 12
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Display a dependency graph of all services.

```
deployify graph [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| ovhcloud \| compose \| awsecs \| azurecontainerapps \| nomadconsul, or the ID of a plugin target. |  |

