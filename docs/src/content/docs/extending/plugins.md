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

### See what is installed

```bash
deployify plugin list            # where each plugin target was found
deployify plugin list --check    # also start each one: name, version, capabilities
```

### Lock plugin versions

```bash
deployify plugin lock            # every plugin target the project's environments use
deployify plugin lock example        # just this one
deployify plugin lock --no-hash  # version only
```

This writes `deployify-plugins.lock.json` next to `deployify-state.json`. Commit it. From then on:

- Deployify refuses to start a plugin whose file no longer matches the locked SHA-256.
- Deployify refuses a plugin that reports a different version than the one locked.
- A locked target whose plugin isn't installed is an error, rather than "unknown target".

The hash covers the file Deployify starts. That works for a self-contained executable or a
`.dll`. A `dotnet tool` launcher differs per machine, so lock those with `--no-hash`.

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
on `PATH`. The lock file stops a plugin from changing without anyone noticing. It doesn't make
an untrusted plugin safe.
