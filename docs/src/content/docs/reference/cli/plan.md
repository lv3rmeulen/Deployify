---
title: deployify plan
description: "Preview changes that would be applied to the target."
sidebar:
  label: plan
  order: 5
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Preview changes that would be applied to the target.

```
deployify plan [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| ovhcloud \| compose \| awsecs \| azurecontainerapps \| nomadconsul \| iis, or the ID of a plugin target. |  |
| `--allow-unverified` | Start target plugins that deployify-plugins.lock.json has no hash for, with a warning each time (a plugin whose files don't match its locked hash is still refused). Run 'deployify plugin lock' instead to verify them. | `false` |
| `--strict` | Fail when the manifest has resources Deployify won't deploy, even if nothing depends on them (list the ones you expect under ignoreResources). | `false` |
| `--output`, `-o` | Output format: text \| json. With json, stdout gets one JSON document (see the JSON output reference) and the human-readable output goes to stderr. | `Text` |

