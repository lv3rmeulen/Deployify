---
title: "Known limitations"
description: "Things Deployify can't do yet, how it tells you, and what to do instead."
---

These are gaps in what Deployify supports today, not bugs. Each section says how Deployify
reacts and what to do instead.

## Dockerfile build secrets

`publish`, `ci` and `aspire deploy` build resources added with `AddDockerfile` or
`WithDockerfile`, but they can't pass build secrets to the build yet. Two kinds of resource stop
the publish before anything is built:

- A build that uses secrets (`WithBuildSecret`):

  ```
  'web' uses build secrets (NPM_TOKEN), which Deployify can't pass to the build yet.
  Build and push this image yourself, or remove the secrets.
  ```

- A build argument whose value comes from another resource, such as a parameter
  (`WithBuildArg("VERSION", versionParameter)`). Literal build arguments work.

**Instead:** build and push that image yourself, and give the AppHost the image instead of the
Dockerfile when it publishes. The local run keeps building from the Dockerfile:

```csharp
var web = builder.ExecutionContext.IsPublishMode
    ? builder.AddContainer("web", "registry.example.com/shop/web", builder.Configuration["WebImageTag"]!)
    : builder.AddDockerfile("web", "../Shop.Web").WithBuildSecret("NPM_TOKEN", npmToken);
```

Deployify then deploys `web` as a container with an image: it isn't built, recorded with a digest
or checked by `--require-digest`, so use a unique tag per build.

## Nomad container `args`

On the [Nomad](/Deployify/targets/nomad/) and [Nomad + Consul](/Deployify/targets/nomad-consul/)
targets, a resource's arguments (`WithArgs`) are written into the task's `args` as they are.
Nomad renders templates into files and environment variables, not into `args`, which has two
consequences:

- **Service addresses (Nomad).** An argument that refers to another service, such as
  `api.GetEndpoint("http")`, can't be looked up in the service catalog when the task starts, as
  environment variables are. Deployify sets it to `localhost` and warns, which only works when
  both services run on the same node.
- **Secrets (both targets).** An argument that contains a secret parameter is written into the
  job file in plain text, and Deployify warns. Secrets in environment variables are kept in Nomad
  Variables instead.

The same applies to the `raw_exec` mode.

**Instead:** pass the value as an environment variable (`WithEnvironment`) and have the service
read it from there.

## Elsewhere in the docs

Smaller limitations are described on the page they apply to:

- `aspire deploy` doesn't prune yet; run `deployify apply --prune`.
  ([Running from aspire deploy](/Deployify/guides/aspire-deploy/))
- On Kubernetes, rollouts need `kubectl`; without it there is no health gate.
  ([Rollouts](/Deployify/guides/rollouts/))
- On Azure Container Apps, rollbacks restore the revision template and Key Vault secrets, not
  app-level settings such as ingress. ([Rollouts](/Deployify/guides/rollouts/))
- On Nomad with `registerServices: false`, services reach each other on `localhost`, which only
  works on a single node. ([Nomad](/Deployify/targets/nomad/))
- Canary and blue-green aren't available on Docker Compose, IIS, DigitalOcean App Platform or
  plugin targets, and Helm has no blue-green. On Kubernetes, canary weights are rounded to whole
  pods; on Nomad, the canaries' share is fixed for the whole rollout.
  ([Canary and blue-green](/Deployify/guides/rollout-strategies/))
- Cloud Run runs only HTTP services; containers such as databases have to run elsewhere.
  ([Google Cloud Run](/Deployify/targets/cloud-run/))
- Argo CD and Flux, and DigitalOcean App Platform, have no Deployify health gate or `rollback`.
  ([Argo CD and Flux](/Deployify/targets/gitops/), [DigitalOcean](/Deployify/targets/digitalocean/))
