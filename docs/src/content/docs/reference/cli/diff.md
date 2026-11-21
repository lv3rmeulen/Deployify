---
title: deployify diff
description: "Show differences between local manifest and the running target."
sidebar:
  label: diff
  order: 14
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Show differences between local manifest and the running target.

```
deployify diff [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| ovhcloud \| compose \| awsecs \| azurecontainerapps \| nomadconsul \| iis, or the ID of a plugin target. |  |
| `--allow-unverified` | Start target plugins that deployify-plugins.lock.json has no hash for, with a warning each time (a plugin whose files don't match its locked hash is still refused). Run 'deployify plugin lock' instead to verify them. | `false` |
| `--summary` | Print only the table of services and the counts, without the changed values (for Kubernetes and Helm, the kubectl diff / helm diff output). --output json includes the values either way. | `false` |
| `--output`, `-o` | Output format: text \| json. With json, stdout gets one JSON document (see the JSON output reference) and the human-readable output goes to stderr. | `Text` |

