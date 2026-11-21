---
title: "Adding a built-in target"
description: "Adding a deployment target to Deployify itself."
---

:::note
To support a platform without changing Deployify, write a [plugin](/Deployify/extending/plugins/) instead.
:::

1. Create `src/Deployify.Target.MyTarget/` with a class implementing `IDeploymentTarget`.
2. Add `DeploymentTargetType.MyTarget` (and its name under `Names`) in `DeploymentTargetType.cs`, and add it to `BuiltIn`.
3. Register it in `src/Deployify.Targets/DeployifyServiceCollectionExtensions.cs`:
   ```csharp
   .AddSingleton<IDeploymentTarget, MyTargetDeploymentTarget>()
   ```
4. Add an init flow in `src/Deployify/Commands/Init/`: a class implementing `ITargetInitFlow` with the
   target's flags, its interactive questions and the target config both produce. List it in
   `TargetInitFlows` (and in `TargetInitFlows.Choices` for the interactive prompt), then update the
   `init` snapshots with `DEPLOYIFY_UPDATE_SNAPSHOTS=1` and review the diff. Add display address
   handling in `EnvCommand.cs`.
5. All commands (`generate`, `validate`, `plan`, `apply`, `ci`, `watch`, etc.) pick it up automatically via the target factory.
6. For `destroy` and `apply --prune`, also implement `IResourceLifecycle`: put an ownership marker
   on everything the target creates, list what carries it (throwing when the platform can't be
   asked), and delete one service. Which services may be deleted is decided for you.
7. For [`deployify logs`](/Deployify/guides/logs/), also implement `IServiceLogSource`: find the
   service's instances and read their output from the platform, honoring `Tail` (per instance),
   `Since` and `Follow`. Throw `ServiceLogsException` for what the user can fix (a service that
   isn't deployed, an option the platform can't honor), and never copy the lines into Deployify's
   own log. `LogLines` has the helpers the built-in targets share, and the test in
   `LogsCommandTests` fails until every built-in target supports it.
8. For [`deployify dashboard`](/Deployify/guides/resource-server/), also implement `IResourceSourceProvider`:
   return an `IResourceSource` (usually a `PollingResourceSource` that lists the deployed services and
   their instances as `ResourceSnapshot`s, streams their logs and runs start/stop/restart). The
   resource server turns it into what the Aspire Dashboard expects.
