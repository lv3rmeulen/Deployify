---
title: deployify ci
description: "Run the full generate → validate → plan → apply pipeline for CI/CD."
sidebar:
  label: ci
  order: 9
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Run the full generate → validate → plan → apply pipeline for CI/CD.

```
deployify ci [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| ovhcloud \| compose \| awsecs \| azurecontainerapps \| nomadconsul \| iis, or the ID of a plugin target. |  |
| `--skip-validate` | Skip the validate step (useful when the cluster is not reachable during CI). | `false` |
| `--skip-apply` | Stop after plan — generate and validate but do not deploy. | `false` |
| `--fail-on-changes` | Exit non-zero if the plan detects any create/update/delete changes (drift gating). | `false` |
| `--auto-approve` | Deploy without asking for confirmation. Required when there is no interactive terminal. | `false` |
| `--strict` | Fail when the manifest has resources Deployify won't deploy, even if nothing depends on them (list the ones you expect under ignoreResources). | `false` |
| `--no-health-gate` | Deploy without waiting for services to become healthy and without automatic rollback. | `false` |
| `--no-rollback` | Wait for health, but leave a failed deployment in place instead of rolling it back. | `false` |
| `--health-timeout` | Seconds each service may take to become healthy before the rollout fails (default: 600). | `600` |
| `--rollback-timeout` | Seconds the whole rollback may take (default: 900). | `900` |
| `--prune` | After a successful deploy, remove services that are no longer in the manifest (only ones Deployify deployed and still marks as its own). | `false` |
| `--force-prune` | Prune even when it would remove more than half of the services, or all of them. | `false` |

