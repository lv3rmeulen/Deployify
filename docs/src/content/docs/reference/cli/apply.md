---
title: deployify apply
description: "Deploy to the target."
sidebar:
  label: apply
  order: 6
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Deploy to the target.

```
deployify apply [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| ovhcloud \| compose \| awsecs \| azurecontainerapps \| nomadconsul \| iis, or the ID of a plugin target. |  |
| `--dry-run` | Simulate the operation without making changes. | `false` |
| `--auto-approve` | Skip confirmation prompt. | `false` |
| `--no-health-gate` | Deploy without waiting for services to become healthy and without automatic rollback. | `false` |
| `--no-rollback` | Wait for health, but leave a failed deployment in place instead of rolling it back. | `false` |
| `--health-timeout` | Seconds each service may take to become healthy before the rollout fails (default: 600). | `600` |
| `--rollback-timeout` | Seconds the whole rollback may take (default: 900). | `900` |
| `--prune` | After a successful deploy, remove services that are no longer in the manifest (only ones Deployify deployed and still marks as its own). | `false` |
| `--force-prune` | Prune even when it would remove more than half of the services, or all of them. | `false` |

