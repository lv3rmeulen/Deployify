---
title: deployify package
description: "Zip build output and upload to the artifact store (raw_exec Nomad deployments only)."
sidebar:
  label: package
  order: 8
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Zip build output and upload to the artifact store (raw_exec Nomad deployments only).

```
deployify package [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--env`, `-e` | Environment to use, overriding the active environment (e.g. local, prod). |  |
| `--target`, `-t` | Deployment target: nomad \| kubernetes \| helm \| argocd \| flux \| ovhcloud \| compose \| awsecs \| azurecontainerapps \| nomadconsul, or the ID of a plugin target. |  |
| `--dry-run` | Simulate the operation without making changes. | `false` |
| `--publish-dir` | Directory containing the published binaries. Defaults to the dotnet publish output directory (bin/Release/net*/publish) for each project found in the manifest. |  |
| `--image-tag`, `-i` | Version used in the artifact file names (defaults to the environment's image tag). |  |

