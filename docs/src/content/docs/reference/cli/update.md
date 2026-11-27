---
title: deployify update
description: "Update the project's plugins to their newest version without breaking changes (like 'deployify plugin update') and check that the AppHost's Deployify.Hosting package matches this Deployify. With --self, update Deployify itself."
sidebar:
  label: update
  order: 24
---

:::note
Generated from the command definitions by `tools/Deployify.DocGen`; don't edit by hand. Run `dotnet run --project tools/Deployify.DocGen` after changing a command.
:::

Update the project's plugins to their newest version without breaking changes (like 'deployify plugin update') and check that the AppHost's Deployify.Hosting package matches this Deployify. With --self, update Deployify itself.

```
deployify update [options]
```

**Options**

| Option | Description | Default |
|---|---|---|
| `--self` | Update Deployify itself instead of the project, with 'dotnet tool update'. Works for a global, --tool-path or local .NET tool. On Windows a global or --tool-path Deployify is updated in the background once this command has exited, with its output in %TEMP%\deployify-update.log. | `false` |
| `--project-path`, `-p` | Path to the Aspire AppHost .csproj or its directory. | current directory |
| `--major` | Also move to a new major version (for 0.x: a new minor version), which may have breaking changes. Default: stay on versions without breaking changes. | `false` |
| `--prerelease` | Consider pre-release versions. Default: only when the current version is one. | `false` |
| `--check` | Only report what would be updated. Exits 2 when something can be updated, 0 when nothing can, 1 when a check failed. | `false` |
| `--dry-run` | Simulate the operation without making changes. | `false` |
| `--source` | With --self: the NuGet feed to look for Deployify in, a v3 service index URL or a folder of .nupkg files. Default: DEPLOYIFY_UPDATE_SOURCE, else https://api.nuget.org/v3/index.json. |  |
| `--index` | The plugin index to use instead of the public one (https://lv3rmeulen.github.io/Deployify/plugins/index.json): an https:// URL or a local path. Default: DEPLOYIFY_PLUGIN_INDEX. Another index needs --index-identity or --index-key. |  |
| `--index-identity` | Who must have signed the index given with --index (keyless): the exact certificate identity, such as the workflow that signs it. Default: DEPLOYIFY_PLUGIN_INDEX_IDENTITY. |  |
| `--index-issuer` | The OIDC issuer of --index-identity. Default: DEPLOYIFY_PLUGIN_INDEX_ISSUER, else GitHub Actions (https://token.actions.githubusercontent.com). |  |
| `--index-key` | The public key file the index given with --index is signed with, instead of a keyless identity. Default: DEPLOYIFY_PLUGIN_INDEX_KEY. |  |

