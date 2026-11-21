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
| `--allow-unverified` | Start target plugins that deployify-plugins.lock.json has no hash for, with a warning each time (a plugin whose files don't match its locked hash is still refused). Run 'deployify plugin lock' instead to verify them. | `false` |
| `--from` | **Required.** Environment whose published images to promote (e.g. staging). |  |
| `--to` | **Required.** Environment to promote them to (e.g. prod). |  |
| `--image-tag`, `-i` | Tag to promote, for example the commit SHA. Defaults to the source environment's configured tag. |  |
| `--copy-with` | Tool that copies images when the target uses another registry: crane \| skopeo \| docker. Defaults to the first one installed, in that order. |  |
| `--dry-run` | Simulate the operation without making changes. | `false` |
| `--output`, `-o` | Output format: text \| json. With json, stdout gets one JSON document (see the JSON output reference) and the human-readable output goes to stderr. | `Text` |

