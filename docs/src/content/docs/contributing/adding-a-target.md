---
title: "Adding a built-in target"
description: "Adding a deployment target to Deployify itself."
---

:::note
To support a platform without changing Deployify, write a [plugin](/Deployify/extending/plugins/) instead.
:::

1. Create `src/Deployify.Target.MyTarget/` with a class implementing `IDeploymentTarget`.
2. Add `DeploymentTargetType.MyTarget` to the enum in `DeploymentTargetType.cs`.
3. Register in `DependencyInjection.cs`:
   ```csharp
   .AddSingleton<IDeploymentTarget, MyTargetDeploymentTarget>()
   ```
4. Add init flags in `InitCommand.cs` and display address handling in `EnvCommand.cs`.
5. All commands (`generate`, `validate`, `plan`, `apply`, `ci`, `watch`, etc.) pick it up automatically via the target factory.
6. For `destroy` and `apply --prune`, also implement `IResourceLifecycle`: put an ownership marker
   on everything the target creates, list what carries it (throwing when the platform can't be
   asked), and delete one service. Which services may be deleted is decided for you.
7. For [`deployify dashboard`](/Deployify/guides/resource-server/), also implement `IResourceSourceProvider`:
   return an `IResourceSource` (usually a `PollingResourceSource` that lists the deployed services and
   their instances as `ResourceSnapshot`s, streams their logs and runs start/stop/restart). The
   resource server turns it into what the Aspire Dashboard expects.
