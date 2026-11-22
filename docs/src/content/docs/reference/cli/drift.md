---
title: deployify drift
description: "Check whether environments still match what was last deployed, and send alerts (drift.notify) when they don't."
sidebar:
  label: drift
  order: 15
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Check whether environments still match what was last deployed, and send alerts (drift.notify) when they don't.

```
deployify drift [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--all` | Check every environment in deployify-state.json (except abstract ones and ones with drift.enabled: false) instead of only the active one or --env. | `false` |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| ovhcloud \| compose \| awsecs \| azurecontainerapps \| nomadconsul \| iis \| cloudrun, or the ID of a plugin target. |  |
| `--allow-unverified` | Start target plugins that deployify-plugins.lock.json has no hash for, with a warning each time (a plugin whose files don't match its locked hash is still refused). Run 'deployify plugin lock' instead to verify them. | `false` |
| `--no-notify` | Report only: send no alerts and leave the recorded result of the last check alone, so a check by hand doesn't use up the scheduled job's alerts. | `false` |
| `--every` | Keep running and check again at this interval (for example 15m, 1h, 1d) until stopped. For a long-running container; in CI, schedule the job instead. |  |
| `--summary` | Print only which services drifted, without the live and deployed values. | `false` |
| `--output`, `-o` | Output format: text \| json. With json, stdout gets one JSON document (see the JSON output reference) and the human-readable output goes to stderr. | `Text` |

