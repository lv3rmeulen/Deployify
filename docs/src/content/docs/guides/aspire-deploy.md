---
title: "Running from aspire deploy"
description: "Use Deployify as a step in Aspire's own deployment pipeline."
---

:::tip
The [`aspire-deploy` sample](https://github.com/lv3rmeulen/Deployify/tree/main/samples/aspire-deploy) is a runnable AppHost wired up this way.
:::

`Deployify.Hosting` plugs Deployify into Aspire's deployment pipeline, so `aspire deploy` runs it
like any other deployment integration. Reference it from the AppHost and add one line:

```csharp
builder.AddDeployify();
```

`aspire deploy` then runs a `deployify-deploy` step after Aspire's `publish-manifest` step. It uses
the manifest Aspire just wrote and does what `deployify apply` does for the active environment:
plan, ask for approval, publish project images, record state, and apply (health-gated where the
target supports it). In CI, set `Deployify__AutoApprove=true`; other settings (`Environment`,
`ImageTag`, `Publish`, …) work the same way; see [Details](#details) below.

```
aspire deploy --list-steps   # publish-manifest → deployify-deploy → deploy
```

Not yet from `aspire deploy`: `--prune` (use `deployify apply --prune`).

## Details

approval, build and push project images, record the deployment in state, and apply (health-gated
with automatic rollback where the target supports it). Run `deployify init` once first.

```
aspire deploy --list-steps                         # publish-manifest → deployify-deploy → deploy
aspire deploy                                      # asks before deploying
Deployify__AutoApprove=true aspire deploy --non-interactive   # CI
aspire do deployify-deploy                         # just this step
```

## Settings

Set in code, or per run through configuration under `Deployify:` (environment variables
`Deployify__…`), which wins:

| Option | Key | Default |
|---|---|---|
| `Environment` | `Deployify:Environment` | the state file's active environment |
| `ProjectPath` | `Deployify:ProjectPath` | AppHost directory (state file searched upward) |
| `Target` | `Deployify:Target` | the environment's target |
| `Publish` | `Deployify:Publish` | `true` — Deployify builds and pushes project images |
| `ImageTag` | `Deployify:ImageTag` | the environment's `ImageTag` |
| `HealthGate` | `Deployify:HealthGate` | `true` |
| `AutoRollback` | `Deployify:AutoRollback` | `true` |
| `HealthTimeout` | `Deployify:HealthTimeoutSeconds` | 10 minutes |
| `AutoApprove` | `Deployify:AutoApprove` | `false`; without it, a run that can't ask fails |

```csharp
builder.AddDeployify(configure: o =>
{
    o.Environment = "prod";
    o.Publish = false;      // images are built elsewhere
});
```

## Notes

- Parameter values come from Deployify (state, `Parameters__*` environment variables, secret
  backends), as with the CLI — not from Aspire's parameter prompts, so generated passwords stay the
  ones Deployify already stored.
- Pruning is not done from `aspire deploy` yet; run `deployify apply --prune`.
- When no `--output-path` is given, the integration pins Aspire's output folder to the default
  (`<AppHost>/aspire-output`) so the manifest is written where the step reads it.
- Built on Aspire's experimental pipeline APIs (`ASPIREPIPELINES001`); pinned to Aspire.Hosting 13.6.5.
