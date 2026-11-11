---
title: deployify state
description: "Inspect and manage deployment state (inventory, lock, remote backend)."
sidebar:
  label: state
  order: 17
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Inspect and manage deployment state (inventory, lock, remote backend).

```
deployify state <command>
```

## `state show`

Show where state is kept, the lock, and what is recorded as deployed.

```
deployify state show [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| compose \| awsecs \| azurecontainerapps \| nomadconsul, or the ID of a plugin target. |  |

## `state unlock`

Remove a lock left behind by a run that is no longer going.

```
deployify state unlock <lock-id> [options]
```

**Arguments**

| Argument | Description |
|---|---|
| `<lock-id>` | The id of the lock to remove, as 'state show' or the lock error prints it. |

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| compose \| awsecs \| azurecontainerapps \| nomadconsul, or the ID of a plugin target. |  |

## `state adopt`

Record services the target marks as Deployify's but state doesn't list yet (deployed before Deployify kept an inventory), so destroy and prune can remove them.

```
deployify state adopt [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| compose \| awsecs \| azurecontainerapps \| nomadconsul, or the ID of a plugin target. |  |
| `--service`, `-s` | Only adopt these services (comma-separated or repeated). Default: all of them. |  |

## `state push`

Copy local state (inventory, generated parameters, rollout journals) into the remote backend configured for the environment. Use it once, after switching an existing environment to remote state.

```
deployify state push [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| compose \| awsecs \| azurecontainerapps \| nomadconsul, or the ID of a plugin target. |  |
| `--force` | Replace remote state even when it belongs to another lineage or is newer. | `false` |

