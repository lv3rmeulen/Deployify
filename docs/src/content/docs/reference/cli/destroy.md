---
title: deployify destroy
description: "Remove everything Deployify deployed in an environment."
sidebar:
  label: destroy
  order: 16
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Remove everything Deployify deployed in an environment.

```
deployify destroy [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| ovhcloud \| compose \| awsecs \| azurecontainerapps \| nomadconsul \| iis, or the ID of a plugin target. |  |
| `--dry-run` | Simulate the operation without making changes. | `false` |
| `--service`, `-s` | Only destroy these services (comma-separated or repeated). Default: all of them. |  |
| `--keep-secrets` | Keep the services' platform-side secrets (Nomad variables, SSM parameters). | `false` |
| `--volumes` | Also remove data volumes (Compose). They are kept by default. | `false` |
| `--auto-approve` | Destroy without typing the environment name to confirm. Required when there is no interactive terminal. | `false` |

