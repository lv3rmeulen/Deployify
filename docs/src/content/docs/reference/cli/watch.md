---
title: deployify watch
description: "Watch for source changes and automatically re-run generate + plan."
sidebar:
  label: watch
  order: 10
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Watch for source changes and automatically re-run generate + plan.

```
deployify watch [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| compose \| awsecs \| azurecontainerapps \| nomadconsul, or the ID of a plugin target. |  |
| `--debounce` | Milliseconds to wait after the last file change before re-running (default: 500). | `500` |
| `--no-generate` | Skip re-generating artifacts; only re-run the plan diff against the cluster. | `false` |

