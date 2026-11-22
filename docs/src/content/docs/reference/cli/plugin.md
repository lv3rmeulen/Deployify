---
title: deployify plugin
description: "Find, install, show and lock the target plugins this project can use."
sidebar:
  label: plugin
  order: 22
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Find, install, show and lock the target plugins this project can use.

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

Record the plugin version and the SHA-256 of its files each target uses in deployify-plugins.lock.json. Deployify refuses to start a plugin target without one. A platform-specific plugin gets a hash per platform: run this on each platform that deploys.

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

## `plugin search`

Search the signed plugin index for target plugins. Deployify checks the index's signature before reading it, so this needs cosign.

```
deployify plugin search <term> [options]
```

**Arguments**

| Argument | Description |
|---|---|
| `<term>` | Text to look for in plugin IDs, names, publishers and descriptions. Default: list every plugin. |

**Options**

| Option | Description | Default |
|---|---|---|
| `--prerelease` | Consider pre-release versions when picking the newest one. | `false` |
| `--index` | The plugin index to use instead of the public one (https://lv3rmeulen.github.io/Deployify/plugins/index.json): an https:// URL or a local path. Default: DEPLOYIFY_PLUGIN_INDEX. Another index needs --index-identity or --index-key. |  |
| `--index-identity` | Who must have signed the index given with --index (keyless): the exact certificate identity, such as the workflow that signs it. Default: DEPLOYIFY_PLUGIN_INDEX_IDENTITY. |  |
| `--index-issuer` | The OIDC issuer of --index-identity. Default: DEPLOYIFY_PLUGIN_INDEX_ISSUER, else GitHub Actions (https://token.actions.githubusercontent.com). |  |
| `--index-key` | The public key file the index given with --index is signed with, instead of a keyless identity. Default: DEPLOYIFY_PLUGIN_INDEX_KEY. |  |

## `plugin install`

Install target plugins from the signed plugin index. Each package must match the SHA-256 the index lists and be signed by the publisher the index names; nothing of it runs before both checks pass. In a project, the plugin is then locked in deployify-plugins.lock.json. Needs cosign.

```
deployify plugin install <targets> [options]
```

**Arguments**

| Argument | Description |
|---|---|
| `<targets>` | Plugin target IDs to install, optionally with a version: digitalocean or digitalocean@1.2.0. Default: every plugin deployify-plugins.lock.json records as installed from an index, at its locked version. |

**Options**

| Option | Description | Default |
|---|---|---|
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--local` | Install into the project's .deployify/plugins folder instead of ~/.deployify/plugins. | `false` |
| `--prerelease` | Consider pre-release versions when picking the newest one. | `false` |
| `--index` | The plugin index to use instead of the public one (https://lv3rmeulen.github.io/Deployify/plugins/index.json): an https:// URL or a local path. Default: DEPLOYIFY_PLUGIN_INDEX. Another index needs --index-identity or --index-key. |  |
| `--index-identity` | Who must have signed the index given with --index (keyless): the exact certificate identity, such as the workflow that signs it. Default: DEPLOYIFY_PLUGIN_INDEX_IDENTITY. |  |
| `--index-issuer` | The OIDC issuer of --index-identity. Default: DEPLOYIFY_PLUGIN_INDEX_ISSUER, else GitHub Actions (https://token.actions.githubusercontent.com). |  |
| `--index-key` | The public key file the index given with --index is signed with, instead of a keyless identity. Default: DEPLOYIFY_PLUGIN_INDEX_KEY. |  |

