---
title: "Stability and versioning"
description: "What Deployify 1.x promises not to break, and how changes and removals are announced."
---

Deployify follows [Semantic Versioning](https://semver.org/). From 1.0.0 on, an upgrade within
1.x shouldn't make a working project, pipeline or plugin stop working. This page says exactly
what that covers.

| Release | May contain |
|---|---|
| Patch (1.4.**1**) | Bug fixes and security fixes. No new features, no deprecations, no format changes. |
| Minor (1.**5**.0) | New commands, options, config keys, targets and protocol additions. Deprecations. A new state format version that older 1.x releases can't read (see [State files](#state-files)). |
| Major (**2**.0.0) | Removal of anything deprecated during 1.x, and any other breaking change. |

Pre-releases (`-alpha`, `-beta`, `-rc`) make no promises. The 1.0.0 alphas are pre-releases too.

## What 1.x won't break

### CLI commands and options

- Command and subcommand names (`apply`, `state unlock`, `plugin lock`, …).
- Option names and their short aliases (`--project-path`, `-p`), and positional arguments.
- What an option does, its accepted values, and its default when the default changes what gets
  deployed, deleted or published.
- Exit codes: `0` means the command succeeded, anything else means it failed. A command that
  succeeds today won't start failing on the same input, except where the input was already
  wrong and Deployify didn't notice (see [Bug fixes](#bug-fixes)).
- The environment variables Deployify reads: `DEPLOYIFY_PLUGIN_PATH`, `DEPLOYIFY_RUN_URL`,
  `DEPLOYIFY_REGISTRY_PASSWORD`, `DEPLOYIFY_SECRETS_FILE`, `DEPLOYIFY_STATE_SAS_TOKEN`,
  `DEPLOYIFY_DASHBOARD_TOKEN`, `DEPLOYIFY_RESOURCE_SERVICE_API_KEY`, and the platform variables
  it falls back to (`NOMAD_TOKEN`, `CONSUL_HTTP_TOKEN`, …).

New commands, options and aliases can be added in any minor release.

### Configuration

Everything you write in `deployify-state.json` and commit:

- Every key under an environment (`Target`, `TargetConfig`, `ServiceOverrides`, `State`,
  `IgnoreResources`, `Protected`, `LockTtlSeconds`, …) and every key inside each built-in
  target's `TargetConfig` section: its name, type, meaning and default.
- Key names stay case-insensitive (`target` and `Target` are the same key).
- The `env:NAME` form for secret fields.
- Target IDs (`Nomad`, `Kubernetes`, `Compose`, …), container runtime names and state backend
  names (`Local`, `AzureBlob`, `S3`, `Consul`, `Kubernetes`).
- `deployify-plugins.lock.json`.

New keys can be added in a minor release, always optional, with a default that keeps the
current behavior.

### What Deployify creates on a platform

`plan` and prune decide what belongs to Deployify from the names it gives resources and the
[ownership markers](/Deployify/concepts/state/#ownership) it puts on them. Within 1.x:

- Ownership markers, labels and tags don't change, so a newer release still recognizes what an
  older one deployed and never orphans or deletes it by mistake.
- Resource names derived from the AppHost (job, service, namespace and Compose project names)
  don't change, so an upgrade doesn't recreate running services.

The rest of a generated artifact (Nomad job files, Kubernetes manifests, Compose files, Helm charts) can
change in a minor release, for example to set a new field or follow a platform's
recommendation. Such changes are listed in the changelog, and `plan` shows them before you apply.

### State files

The files Deployify writes for itself, locally or in a remote backend: `deployify-state.json`,
`inventory.json`, `images.json`, `parameters.json` (and `deployify-parameters.json`),
`lock.json` and the rollout journals.

- **Every 1.x release reads every file an earlier 1.x release wrote**, and files from the
  1.0.0 alphas, and upgrades them to its own format the next time it saves them.
- A format version is only raised in a minor release, and the changelog says which file
  changed. Patch releases never raise one.
- **Downgrading isn't supported.** Once a newer release has rewritten a file, an older release
  refuses it with a message naming the file, rather than reading part of it and overwriting the
  rest. Everyone sharing remote state should upgrade together, or at least never run an older
  minor release than the newest one in use.
- The local layout (`.deployify/state/`, `.deployify/rollouts/`) and the remote key layout
  (`{prefix}/{env}/…`) don't change.

See [Format versions](/Deployify/concepts/state/#format-versions) for how this works.

### Plugin protocol 1.0

Everything described under [The protocol](/Deployify/extending/plugins/#the-protocol):

- Framing (JSON-RPC 2.0 with `Content-Length` headers over stdin and stdout), the
  `initialize` handshake and `shutdown`, and the `DEPLOYIFY_PLUGIN` and
  `DEPLOYIFY_PLUGIN_PROTOCOL` variables.
- Every method name, its request fields and its result fields, and the values of `kind` and
  `state`.
- The capabilities (`rollout`, `lifecycle`) and what they enable.
- Error codes `4001`, `4002` and `5000`, `$/cancelRequest`, `host/resolveParameter` and `host/log`.
- How plugins are found (`DEPLOYIFY_PLUGIN_PATH`, `.deployify/plugins/`, `~/.deployify/plugins/`,
  `PATH`) and named (`deployify-target-<id>`).

Every Deployify 1.x release accepts a plugin that speaks protocol 1.0. A minor release can move
the protocol to 1.1, 1.2, …, which only adds things: new optional request fields, new optional
result fields, new methods behind a new capability. So:

- **Plugins must ignore fields they don't know**, in requests and in `initialize`.
- Deployify only calls a method a plugin doesn't have when the plugin declared the capability
  it belongs to.
- A field a plugin may leave out keeps a default that means what it meant before.

Protocol 2.0 would come with Deployify 2.0.

### .NET APIs for AppHosts and plugins

The public API of the packages you write code against:

- **`Deployify.Hosting`**: `AddDeployify()` and the rest of `DeployifyBuilderExtensions`,
  `DeployifyDeployOptions` and `DeployifyEnvironmentResource`.
- **`Deployify.Plugin.Sdk`**: `DeployifyPlugin`, `PluginBuilder` and `TargetOptions`.
- **`Deployify.Plugin.Testing`**: `PluginConformance`, `ConformanceOptions`, `ConformanceReport`,
  `ConformanceCheck`, `CheckOutcome` and `ConformanceException`.
- **The `Deployify.Core` types a plugin implements or is handed**: `IDeploymentTarget`,
  `IRolloutTarget`, `IResourceLifecycle`, `EnvironmentConfig`, `AspireManifest`, `IPublisher`,
  `IProcessRunner` and `ParameterResolution`, along with the types their members use.

Within 1.x, code that compiles against one of these keeps compiling against a later release
and behaves the same. In a minor release:

- New types, methods, overloads, properties and optional parameters can be added.
- Members on the interfaces a plugin implements are only added with a default implementation,
  so existing plugins keep compiling. The contracts their documentation describes
  (`ObserveAsync` takes one look, `DeleteServiceAsync` can be repeated, …) don't change.
- The conformance kit can add checks, but only for contracts that are already documented. A
  plugin that keeps the documented contracts keeps passing.

Something that has to go is marked `[Obsolete]` with a message naming the replacement, under
the same rules as any other [deprecation](#deprecation).

## Not covered

These can change in any minor release:

- **Human-readable output**: wording, colors, layout, ordering, progress display, log
  messages and error messages. Don't parse them; rely on exit codes and the files Deployify writes.
- **The rest of the .NET API** of `Deployify.Core`, `Deployify.Targets`, the
  `Deployify.Target.*` packages and `Deployify.Plugins`. They are published because the covered
  packages depend on them, not as a library for you to build on.
- **Generated artifacts**, beyond the names and markers listed above.
- **Minimum versions** of .NET, Aspire and the tools Deployify drives (`kubectl`, `helm`, the
  AWS CLI, PowerShell, …). They can go up in a minor release, for example when a version
  leaves support, and the changelog says so.
- **Undocumented behavior**, and anything marked experimental in its documentation.

## Deprecation

Nothing covered above is removed or changed incompatibly during 1.x. When something has to go:

1. A minor release marks it deprecated. It keeps working exactly as before.
2. From then on, using it prints a warning on stderr naming what to use instead and the release
   it goes away in. In the .NET APIs, it is marked `[Obsolete]` instead, so the compiler warns. Deprecation warnings don't count as warnings for `--strict`, so a
   deprecation can't fail a pipeline on its own.
3. The changelog lists it under **Deprecated**, and its documentation says so.
4. It is removed in the next major release, provided at least one minor release shipped the
   warning first. Something deprecated in 1.9 can't be removed before 2.0; something
   deprecated in the last 1.x minor waits until 3.0.

A renamed option or key keeps its old name as an alias during 1.x, with the warning.

## Bug fixes

A fix can make Deployify reject input it used to accept by mistake, when accepting it was the
bug: a config value of the wrong type, a combination of options that never worked, or output
that leaked a secret. The changelog lists such fixes under **Fixed**, says what now fails, and
how to fix your input.

## Security fixes

When the only safe fix for a vulnerability breaks something on this page, the fix ships in a
patch release anyway, the changelog explains what broke and why, and the advisory says what to
do. See [`SECURITY.md`](https://github.com/lv3rmeulen/Deployify/blob/main/SECURITY.md).

## Supported versions

Fixes go into the latest 1.x minor release. There are no backports to older minor releases;
upgrading within 1.x is meant to be safe.
