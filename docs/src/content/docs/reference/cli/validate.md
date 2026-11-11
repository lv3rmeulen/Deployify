---
title: deployify validate
description: "Validate generated artifacts against the target."
sidebar:
  label: validate
  order: 4
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Validate generated artifacts against the target.

```
deployify validate [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| compose \| awsecs \| azurecontainerapps \| nomadconsul, or the ID of a plugin target. |  |

