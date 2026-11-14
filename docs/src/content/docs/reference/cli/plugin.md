---
title: deployify plugin
description: "Show and lock the target plugins this project can use."
sidebar:
  label: plugin
  order: 19
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Show and lock the target plugins this project can use.

```
deployify plugin <command>
```

## `plugin list`

List the target plugins Deployify can find, and where it found them.

```
deployify plugin list [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--check` | Start each plugin and show what it reports (name, version, capabilities). | `false` |

## `plugin lock`

Record the plugin version (and file hash) each target uses in deployify-plugins.lock.json.

```
deployify plugin lock <targets> [options]
```

**Arguments**

| Argument | Description |
|---|---|
| `<targets>` | Plugin target IDs to lock. Default: every plugin target the project's environments use. |

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--no-hash` | Lock the plugin version only, not the exact file (for dotnet tool installs, whose launcher differs per machine). | `false` |

