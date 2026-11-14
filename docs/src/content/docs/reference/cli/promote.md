---
title: deployify promote
description: "Make another environment deploy the images an environment already published, by digest, without rebuilding them."
sidebar:
  label: promote
  order: 9
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Make another environment deploy the images an environment already published, by digest, without rebuilding them.

```
deployify promote [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--from` | **Required.** Environment whose published images to promote (e.g. staging). |  |
| `--to` | **Required.** Environment to promote them to (e.g. prod). |  |
| `--image-tag`, `-i` | Tag to promote, for example the commit SHA. Defaults to the source environment's configured tag. |  |
| `--dry-run` | Simulate the operation without making changes. | `false` |

