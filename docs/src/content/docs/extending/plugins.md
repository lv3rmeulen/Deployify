---
title: Writing target plugins
description: Add any platform as an out-of-process plugin, written with the .NET SDK or in any language.
---

Deployify ships nine deployment targets. Any other platform can be added as a **plugin**: a
separate executable that Deployify starts and talks to over stdin/stdout. Plugins don't run
inside Deployify's process, so they can't conflict with its dependencies (or with
`Aspire.Hosting` when Deployify runs inside `aspire deploy`). They can also be written in any
language.


## Using a plugin

A plugin for target `example` is an executable named `deployify-target-example` (`.exe`, `.cmd` or
`.bat` on Windows). Install it in one of these places. Deployify uses the first match:

1. a directory listed in `DEPLOYIFY_PLUGIN_PATH`
2. the project's `.deployify/plugins/` folder (or a subfolder one level down)
3. `~/.deployify/plugins/` (or a subfolder one level down)
4. `PATH`, which covers `dotnet tool install -g deployify-target-example`

In the plugin folders (1–3), a framework-dependent `deployify-target-example.dll` works too: Deployify
starts it with `dotnet`.

Then use the target like a built-in one:

```jsonc
// deployify-state.json
"prod": {
  "Target": "example",
  "TargetConfig": { "example": { "app": "shop" } }
}
```

```bash
deployify plan --target example
```

A plugin can't take over a built-in target ID: `compose` is always the built-in Compose target.

### Install from the plugin index

Third-party plugins are listed in a signed [plugin index](https://github.com/lv3rmeulen/Deployify/tree/main/plugin-index),
published at `https://lv3rmeulen.github.io/Deployify/plugins/index.json`. Search it and install
from it by target ID:

```bash
deployify plugin search             # every plugin in the index
deployify plugin search cloud       # IDs, names, publishers and descriptions containing "cloud"
deployify plugin install example    # the newest version
deployify plugin install example@1.2.0
deployify plugin install example --local   # into the project's .deployify/plugins/ instead of ~/.deployify/plugins/
```

Both need [cosign](https://docs.sigstore.dev/cosign/system_config/installation/) 3.0.3 or later.
Before anything of a plugin runs, Deployify checks:

1. **The index's signature.** The public index is signed keyless by the workflow that builds it,
   from main. Deployify won't read an index with any other signature, or none, and refuses an
   index that has expired (it is re-signed weekly), so an old copy can't hide a withdrawn version.
2. **The package's hash.** It must match the SHA-256 the index lists.
3. **The package's signature.** Each index entry names who signs that plugin's packages,
   usually the publisher's own release workflow. The package's Sigstore bundle must be signed
   by exactly that identity.

Then Deployify unpacks the package into `~/.deployify/plugins/<id>/`, starts it once to check
that it reports the version and target the index promised, and, inside a project, locks it in
`deployify-plugins.lock.json` together with where it came from:

```json
"example": {
  "plugin": "example",
  "version": "1.2.0",
  "sha256": { "any": "3f9a…" },
  "source": {
    "index": "https://lv3rmeulen.github.io/Deployify/plugins/index.json",
    "signer": "keyless, /^https://github\\.com/acme/deployify-target-example/…$/, issued by https://token.actions.githubusercontent.com"
  }
}
```

With no arguments, `deployify plugin install` installs every plugin the lock file records from an
index, at its locked version. That is the step for CI and for a new machine:

```bash
deployify plugin install     # same versions, same files as the lock file; a different hash is refused
deployify apply prod
```

A version the index has withdrawn (`yanked`) isn't installed by default. Installing it by version,
or from the lock file, warns.

#### Your own index

A company can run its own index, for internal plugins, with the same format and checks. Point
Deployify at it and say who signs it:

```bash
deployify plugin install example \
  --index https://plugins.acme.internal/index.json \
  --index-identity https://github.com/acme/deployify-plugins/.github/workflows/index.yml@refs/heads/main
```

Or set `DEPLOYIFY_PLUGIN_INDEX` and `DEPLOYIFY_PLUGIN_INDEX_IDENTITY` (and
`DEPLOYIFY_PLUGIN_INDEX_ISSUER` outside GitHub Actions, or `DEPLOYIFY_PLUGIN_INDEX_KEY` for an
index signed with a key). Deployify doesn't read an unsigned index. Build one with
`tools/Deployify.PluginIndex` (see the [index's README](https://github.com/lv3rmeulen/Deployify/tree/main/plugin-index))
and sign it with `cosign sign-blob --bundle index.json.sigstore.json index.json`.

### See what is installed

```bash
deployify plugin list            # where each plugin target was found
deployify plugin list --check    # also start each one: name, version, capabilities
```

### Lock plugins

Deployify only starts a plugin target that the project's lock file has a hash for. Lock the
plugins before the first deploy:

```bash
deployify plugin lock            # every plugin target the project's environments use
deployify plugin lock example    # just this one
```

This writes `deployify-plugins.lock.json` next to `deployify-state.json`. Commit it. From then on:

- Deployify refuses to start a plugin whose files no longer match the locked SHA-256.
- Deployify refuses a plugin that reports a different version than the one locked.
- A locked target whose plugin isn't installed is an error, rather than "unknown target".

A plugin that isn't locked is refused before it runs:

```
✗ Plugin 'deployify-target-example' at /home/me/.deployify/plugins/deployify-target-example (found in user plugins folder)
  isn't verified: deployify-plugins.lock.json doesn't lock it. Run 'deployify plugin lock' and commit
  deployify-plugins.lock.json, or allow unverified plugins (--allow-unverified, or AllowUnverifiedPlugins
  for aspire deploy) to start it anyway.
```

`--allow-unverified` (or `AllowUnverifiedPlugins` with `aspire deploy`) starts it anyway and
logs a warning every time it does. It's meant for trying a plugin out, not for pipelines. It
doesn't get past a hash that doesn't match: a plugin whose files changed is always refused.

What the hash covers:

| Installed as | Hashed |
|---|---|
| A folder of its own (`plugins/example/deployify-target-example.exe`) | every file in that folder, so its dependencies too |
| `dotnet tool install` (on `PATH` or a `--tool-path`) | the tool's folder in the tool store, not the launcher, which differs per machine |
| A single file at the top of a plugin folder, or on `PATH` | that file |

Give a plugin with dependencies a folder of its own, so they are covered too.

A framework-dependent plugin (a `.dll`, or a `dotnet tool`) is the same on every platform and is
locked once. A self-contained executable differs per platform, so the lock file keeps a hash per
runtime ID. Run `deployify plugin lock` on each platform that deploys (for example on your
Windows machine and in Linux CI) and commit the result. Locking the same plugin version again
keeps the other platforms' hashes; a new version starts over.

```json
{
  "version": 1,
  "plugins": {
    "example": {
      "plugin": "Example",
      "version": "1.2.0",
      "sha256": { "linux-x64": "3f9a…", "win-x64": "b71c…" }
    }
  }
}
```

### Settings checks

A plugin can publish a JSON Schema for its section of `TargetConfig`. Deployify checks the
environment against it before calling the plugin, so a wrong type is reported up front:

```
✗ The settings for target 'compose-plugin' don't match what its plugin expects:
  - TargetConfig.compose.ProjectName: Value is "integer" but should be "string"
```

### What a plugin target supports

Every plugin target supports `generate`, `validate`, `plan`, `apply`, `publish`, `diff`,
`explain` and `graph`. Health-gated rollouts and `rollback` need the plugin to declare the
`rollout` capability. `destroy`, `apply --prune` and `state adopt` need the `lifecycle`
capability. `deployify plugin list --check` shows which ones a plugin declares.

## Writing a plugin in .NET

Reference `Deployify.Plugin.Sdk` and implement the same interfaces the built-in targets do:

- `IDeploymentTarget`: required
- `IRolloutTarget`: optional, adds health-gated rollouts with automatic rollback
- `IResourceLifecycle`: optional, adds destroy and prune

```csharp
// Program.cs of a project with <AssemblyName>deployify-target-example</AssemblyName>
using Deployify.Plugin.Sdk;

return await DeployifyPlugin.RunAsync(plugin => plugin
    .Named("example", "1.0.0")
    .AddTarget<ExampleDeploymentTarget>("example", "Example", target => target.WithConfigSchema<ExampleTargetConfig>()));
```

The SDK:

- answers the handshake, and works out the capabilities from the interfaces your class implements;
- builds your target from `builder.Services`. Logging, `IPublisher` (build and push images
  the way Deployify does) and `IProcessRunner` are already registered;
- forwards your `ILogger` output to Deployify's log;
- moves anything written to `Console.Out` to stderr, so it can't corrupt the protocol;
- reads `TargetConfig` and the Aspire manifest into the same `EnvironmentConfig` and
  `AspireManifest` types the built-in targets use.

### Resource types beyond projects and containers

Before it calls your target, Deployify reports every manifest resource it can't deploy itself,
and stops the deploy when a service depends on one. If your target deploys another resource
type, such as executables, declare it so Deployify leaves those resources to you:

```csharp
.AddTarget<ExampleDeploymentTarget>("example", "Example", target => target.ResourceTypes.Add("executable.v0"))
```

The types go out in the handshake (`resourceTypes`). The conformance kit fails a plugin that
declares a type Deployify handles itself, such as `project.v0` or `parameter.v0`.

[`samples/Deployify.Plugin.Sample.Compose`](https://github.com/lv3rmeulen/Deployify/blob/main/samples/Deployify.Plugin.Sample.Compose/Program.cs)
is a complete plugin: it serves Deployify's own Compose target as `compose-plugin`.

### Parameters and secrets

A plugin receives the environment without the explicit parameter values from
`deployify-state.json`. When your target resolves a parameter, it calls
`await ParameterResolution.For(config).GetValueAsync(...)`, exactly as the built-in targets do. The SDK
then asks Deployify for that one value. Deployify only answers for `parameter.v0` resources in
the manifest of the current call, and records every secret value it hands out, so its leak
checks cover plugin output too.

Keep the contracts the interface documentation describes. The conformance kit checks the
important ones:

- `ObserveAsync` takes one look and returns. The engine does the polling and the timeouts.
- `CaptureAsync` snapshots hold references (a revision, an ARN), never secret values. They are
  written to the rollout journal.
- `ListManagedAsync` throws when the platform can't be asked. An empty list would read as
  "nothing deployed".
- `DeleteServiceAsync` can be repeated: deleting a service that is already gone is not an error.

## Testing a plugin

`Deployify.Plugin.Testing` runs conformance checks against your plugin:

```csharp
[Fact]
public async Task Plugin_KeepsDeployifyContracts()
{
    var report = await PluginConformance.RunInProcessAsync(
        p => p.Named("example", "1.0.0").AddTarget<ExampleDeploymentTarget>("example", "Example"),
        "example",
        new ConformanceOptions
        {
            Manifest = TestManifests.ApiWithDatabase(),
            Config = new EnvironmentConfig { TargetConfig = { ["example"] = ... } },
            OutputDirectory = Directory.CreateTempSubdirectory().FullName,
            Live = false,          // true: also ping, plan, diff, capture, observe, list managed
            Destructive = false    // true (with Live): also delete a missing service twice
        });

    report.ThrowIfFailed();
}
```

Use `PluginConformance.RunAsync("path/to/deployify-target-example", "example", options)` to check a
built executable, including one written in another language. The kit fills every secret
parameter with a recognisable value and fails if that value shows up in `explain` output or a
rollout snapshot.

## Publishing a plugin

To make a plugin installable with `deployify plugin install`, release it as a signed package and
list it in the plugin index.

**The package** is a `.zip` of the plugin's folder with `deployify-target-<id>` (or its `.dll`)
at the top. Publish it framework-dependent without an apphost and one package serves every
platform as `any`. A self-contained plugin needs a package per runtime ID (`linux-x64`,
`win-x64`, `osx-arm64`, …).

**The signature** is a Sigstore bundle next to the package, `<package>.sigstore.json`. Sign
keyless from your release workflow, so the identity is your repository's workflow and no key
can leak:

```yaml
# .github/workflows/release.yml
on:
  release:
    types: [published]

jobs:
  package:
    runs-on: ubuntu-latest
    permissions:
      contents: write   # attach to the release
      id-token: write   # keyless signing
    steps:
      - uses: actions/checkout@v6
      - uses: actions/setup-dotnet@v5
      - uses: sigstore/cosign-installer@756fe2d284c7292c4e9b62c8a28f1e5629c59532 # v4.1.3

      - name: Package and sign
        env:
          TAG: ${{ github.event.release.tag_name }}
          GH_TOKEN: ${{ github.token }}
        run: |
          version="${TAG#v}"
          package="deployify-target-example-$version-any.zip"
          dotnet publish src/Example -c Release -p:UseAppHost=false -o out
          (cd out && zip -r -X ../"$package" .)
          cosign sign-blob --yes --bundle "$package.sigstore.json" "$package"
          gh release upload "$TAG" "$package" "$package.sigstore.json"
```

The handshake version has to match the version in the index exactly. With
`Deployify.Plugin.Sdk` a plugin reports its package version (without `+build` metadata) unless
you pass one to `Named`.

**The index entry** is a pull request adding `plugin-index/plugins/<id>.json`, naming your
signing identity and each version's package. The
[index's README](https://github.com/lv3rmeulen/Deployify/tree/main/plugin-index) describes the
format. Pin `identityRegexp` to your repository and release workflow, anchored with `^…$`. The
pull request's checks download each package, check its signature and unpack it. Add each new
version the same way.

## The protocol

The protocol is JSON-RPC 2.0 over the plugin's stdin and stdout. Each message is framed with
LSP-style headers (`Content-Length: <bytes>\r\n\r\n<json>`). Every request takes a single
object parameter, and names are camelCase. stdout is for protocol messages only; stderr goes to
Deployify's debug log, and its last lines are shown if the plugin dies. The message shapes are
in [`Deployify.Plugin.Contracts`](https://github.com/lv3rmeulen/Deployify/blob/main/src/Deployify.Plugin.Contracts/Messages.cs).

A session:

1. Deployify starts the plugin with `DEPLOYIFY_PLUGIN=1` and `DEPLOYIFY_PLUGIN_PROTOCOL=1.0` set.
2. It sends `initialize` `{ protocolVersion, hostVersion }`. The plugin answers
   `{ protocolVersion, plugin: { name, version }, targets: [{ id, displayName, capabilities, configKey?, configSchema? }] }`.
   The major protocol versions must match; a higher minor version only adds optional things.
3. Deployify calls target methods, possibly several at once. Each request carries `targetId`,
   `environment` (the environment as it appears in `deployify-state.json`, without
   `Parameters`), usually `manifest` (Aspire's `manifest.json`), and `outputDirectory` where relevant.
4. It sends a `shutdown` notification and closes stdin. The plugin exits when stdin closes.

| Method | Extra request fields | Result |
|---|---|---|
| `target/ping` | | `{ ok, error? }` |
| `target/generate` | | `{ files: [] }` |
| `target/validate` | | `[{ artifactName, errors: [] }]` |
| `target/plan` | | `{ toCreate, toUpdate, toDelete, unchanged, unknown: [{ serviceName, reason }] }` |
| `target/apply` | | `[{ serviceName, deploymentId?, success, error? }]` |
| `target/publish` | `appHostDirectory` | `null` |
| `target/explain` | | `[{ name, image?, imageTag?, count, env, ports, dependencies, additionalInfo }]` |
| `target/dependencies` | | `[{ from, to }]` |
| `target/diff` | | `[{ serviceName, kind, oldValue?, newValue?, field?, reason? }]` |
| `rollout/capture` | `service` | `{ serviceName, existed, revision?, data }` |
| `rollout/applyService` | `service` | `{ serviceName, success, revision?, error? }` |
| `rollout/observe` | `service`, `revision` | `{ state, detail?, revision? }` |
| `rollout/restore` | `snapshot` | `{ revision? }` |
| `rollout/nativeRollback` | | `{ value }` |
| `rollout/unavailableReason` | | `{ reason? }` |
| `rollout/complete` | | `null` |
| `lifecycle/listManaged` | | `[{ name, targetId?, environment? }]` |
| `lifecycle/deleteService` | `service`, `keepSecrets`, `removeVolumes` | `null` |
| `lifecycle/destroyEnvironment` | `keepSecrets`, `removeVolumes` | `{ lines: [] }` |
| `lifecycle/describeKept` | `keepSecrets`, `removeVolumes` | `{ lines: [] }` |

`kind` is one of `added`, `removed`, `changed`, `unchanged`, `unknown`. `state` is one of
`progressing`, `healthy`, `failed`, `revertedByPlatform`.

The plugin may call Deployify:

- `host/resolveParameter` `{ parameter, input }` → `{ value? }`, during a call only.
- `host/log` `{ level, message, category? }` as a notification.

Errors use JSON-RPC error responses. Their message is shown to the user. A plugin returns
`4001` for a target it doesn't serve, `4002` for a method it doesn't support, and `5000` when
the target itself failed. Cancellation arrives as `$/cancelRequest`.

## Security

A plugin runs as you, with your cloud credentials and your environment variables. Nothing
sandboxes it. Install plugins only from sources you trust, the same as any other tool you put
on `PATH`. The lock file makes sure every machine runs the plugin files someone locked and
committed, and stops them from changing without anyone noticing. It doesn't make an untrusted
plugin safe.

The plugin index adds who the plugin comes from. Listing a plugin means its signing identity
was reviewed and merged into the index; it doesn't mean its code was reviewed. A package
installed from the index was signed by that identity, is the exact file the index pinned, and
was recorded in the public Sigstore transparency log when it was signed. If a publisher's
release workflow or key is compromised, so are their signatures: the index can then yank the
affected versions or replace the publisher's signer.
