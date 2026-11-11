# Deployify.Hosting

Runs [Deployify](https://github.com/lv3rmeulen/Deployify) inside `aspire deploy`.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Api>("api");

builder.AddDeployify();   // deploys on `aspire deploy`

builder.Build().Run();
```

`AddDeployify` adds one step to Aspire's deployment pipeline, `deployify-deploy`, which runs after
Aspire's `publish-manifest` step and is required by `deploy`. It reads the manifest Aspire writes,
then does what `deployify apply` does for the environment in `deployify-state.json`: plan, ask for
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
| `ProjectPath` | `Deployify:ProjectPath` | AppHost directory (state file searched upwards) |
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
