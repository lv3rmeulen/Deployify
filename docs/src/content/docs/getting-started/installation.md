---
title: "Installation"
description: "Install the deployify .NET tool."
---

## Requirements

- .NET 10 SDK
- A running deployment target (Nomad agent, Kubernetes cluster, Docker daemon, AWS CLI, etc.)
- For the Helm target: `helm` 3 and `kubectl`; the helm-diff plugin for `plan` and `diff`
- For the Argo CD and Flux targets: `git` with access to the config repository, `kubectl`, and the controller installed in the cluster

## Install

```bash
dotnet tool install --global deployify
deployify --version
```

Or as a local tool, pinned in the repository's `dotnet-tools.json`:

```bash
dotnet new tool-manifest   # once per repository
dotnet tool install deployify
dotnet deployify --version
```

To build it from source instead:

```bash
dotnet pack src/Deployify/Deployify.csproj -c Release
dotnet tool install --global --add-source ./src/Deployify/nupkg deployify
```

## Update

```bash
deployify update --self            # the newest version without breaking changes
deployify update --self --check    # only report; exits 2 when there is a newer version
deployify update --self --major    # also move to a new major version (read the changelog first)
```

`update --self` runs `dotnet tool update` for the way Deployify was installed: a global tool, a
`--tool-path` tool, or a local tool (which updates the version pinned in `dotnet-tools.json`; commit
it). It stays on the current major version unless you pass `--major`, and only considers
pre-releases with `--prerelease` or when you're on one already.

On Windows, a running program's files can't be replaced, so a global or `--tool-path` Deployify
starts the update in the background, prints `updating, run deployify --version` and exits; the
update runs once Deployify has exited. Its output goes to `%TEMP%\deployify-update.log`, so look
there if `deployify --version` still shows the old version.

Deployify looks for new versions on nuget.org. For another feed, pass `--source` (a v3 feed's
`index.json` URL or a folder of `.nupkg` files) or set `DEPLOYIFY_UPDATE_SOURCE`.

To update the project instead (its plugins, and a check that its `Deployify.Hosting` package matches
the CLI), run `deployify update` without `--self`.
