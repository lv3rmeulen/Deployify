---
title: deployify explain
description: "Display a human-readable summary of all services."
sidebar:
  label: explain
  order: 12
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Display a human-readable summary of all services.

```
deployify explain [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| ovhcloud \| compose \| awsecs \| azurecontainerapps \| nomadconsul \| iis \| cloudrun, or the ID of a plugin target. |  |
| `--allow-unverified` | Start target plugins that deployify-plugins.lock.json has no hash for, with a warning each time (a plugin whose files don't match its locked hash is still refused). Run 'deployify plugin lock' instead to verify them. | `false` |
| `--service`, `-s` | Filter to a specific service. |  |
| `--output`, `-o` | Output format: text \| json. With json, stdout gets one JSON document (see the JSON output reference) and the human-readable output goes to stderr. | `Text` |

