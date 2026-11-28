---
title: deployify publish
description: "Build and push service images or artifacts."
sidebar:
  label: publish
  order: 7
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Build and push service images or artifacts.

```
deployify publish [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| ovhcloud \| compose \| awsecs \| azurecontainerapps \| nomadconsul \| iis \| cloudrun \| host, or the ID of a plugin target. |  |
| `--allow-unverified` | Start target plugins that deployify-plugins.lock.json has no hash for, with a warning each time (a plugin whose files don't match its locked hash is still refused). Run 'deployify plugin lock' instead to verify them. | `false` |
| `--dry-run` | Simulate the operation without making changes. | `false` |
| `--image-tag`, `-i` | Override image tag for this publish run. |  |
| `--container-runtime` | Override container runtime for this run: docker \| podman. |  |

