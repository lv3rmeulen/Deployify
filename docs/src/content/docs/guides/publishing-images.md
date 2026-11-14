---
title: "Publishing images"
description: "How Deployify builds and pushes project and Dockerfile images, and pins them by digest."
---

## `publish`

Builds the images for your own code and pushes them to `containerRegistry`: .NET projects, and
resources built from a Dockerfile. Containers that already name an image (`redis:7`) are pulled
as they are.

```bash
deployify publish
deployify publish --image-tag v1.2.3
deployify publish --container-runtime podman
deployify publish --dry-run   # lists what would be built
```

Each image is named after its resource under the registry: with `containerRegistry` set to
`registry.example.com/myapp`, the `api` project becomes `registry.example.com/myapp/api`. Without
a registry, images stay in the local Docker or Podman store.

## Dockerfile builds

Resources added with `AddDockerfile`, or a container with `WithDockerfile`, are built with
`docker build` (or `podman build`) and pushed like projects. Deployify builds for the platform
set by `containerOs` and `containerArch` (for example `linux/amd64`) and passes the resource's
build arguments as `--build-arg`.

Two things it can't build yet, and stops on before building anything:

- Build secrets (`WithBuildSecret`).
- Build arguments whose value comes from another resource, such as a parameter
  (`WithBuildArg("VERSION", versionParameter)`). Literal values work.

Build and push those images yourself and give the resource the resulting image instead.

## Pinning images by digest

Every image Deployify pushes is recorded with its content digest, and the artifacts generated
afterwards reference it as `registry.example.com/myapp/api@sha256:…` instead of by tag. A
deploy runs exactly the image that was published, and a rollback runs exactly the previous
one, even if the tag is pushed again later.

The digests are kept in the environment's deployment state (`images.json`, next to the
inventory), locally or in the [remote state backend](/concepts/state/#remote-state), so a later
`apply`, a rollback or a drift check in another CI job finds them. A digest is used only for the
exact repository and tag it was published as: change the tag and the artifacts reference the new
tag until it is published. Images in the local store (no registry) are referenced by tag.

## Promoting images to another environment

Build once, deploy many times: `promote` makes another environment deploy the images an
environment already published, by digest, without building them again.

```bash
deployify promote --from staging --to prod --image-tag "$GITHUB_SHA"
deployify apply   --env prod --image-tag "$GITHUB_SHA" --require-digest
```

`promote` looks up the digest the source environment recorded for each project and Dockerfile
build at that tag, and records it in the target environment's deployment state. It deploys
nothing: the target's next `apply` (or `ci --no-publish`) references the images by those digests.
Without `--image-tag`, it promotes the source environment's configured tag.

It promotes all images or none. It stops without recording anything when the source
environment has no digest for an image at that tag (it never published that build), or when the
target has no `containerRegistry`.

`--dry-run` shows what would be promoted and copied. The target environment's state is locked
while `promote` runs.

### Promoting to another registry

When the target environment has a different `containerRegistry`, for example a production
registry in another account or region, `promote` copies each image there first. The copy goes
from registry to registry by digest, so the target ends up with exactly the same image:
nothing is rebuilt or pulled into a local image store. Images already in the target registry
are skipped.

The copy uses the first of these tools that is installed:

| Tool | Command it runs |
|---|---|
| [crane](https://github.com/google/go-containerregistry/tree/main/cmd/crane) | `crane copy` |
| [skopeo](https://github.com/containers/skopeo) | `skopeo copy --all --preserve-digests` |
| Docker with Buildx | `docker buildx imagetools create` |

`--copy-with crane|skopeo|docker` picks one. All three sign in to the registries with the
credentials `docker login` stored, including credential helpers such as the ones for ECR, ACR
and GCR, so sign in to both registries before promoting. On a CI runner the job needs pull
access to the source registry and push access to the target one.

After each copy, `promote` checks that the target registry has a manifest with the source
digest. crane and skopeo always keep it; if Docker ever rewrites a manifest, `promote` stops
and asks for crane or skopeo. Copies happen before anything is recorded: when one fails,
the target's state is unchanged and a second run skips the images already copied.

```bash
docker login registry.staging.example.com
docker login registry.prod.example.com
deployify promote --from staging --to prod --image-tag "$GITHUB_SHA"
```

### Requiring digests

`--require-digest` on `apply` and `ci` stops the deploy when a built image would run by tag
because no digest is recorded for it, for example when the promotion was skipped or used a
different tag. Without it, such an image is deployed by tag, as before.

Promotion only reuses images when the environments don't need different builds. Configuration
and secrets that Aspire passes at deploy time are fine. A Dockerfile build whose build arguments
differ per environment needs its own build in each one.

## Container runtime

Deployify uses `dotnet publish -t:PublishContainer` to build project images, and `docker build`
or `podman build` for Dockerfiles. By default it tries Docker; if Docker is unavailable it automatically falls back to Podman.

### Auto-detection

When `containerRuntime` is `Docker` (the default), Deployify:

1. Runs `docker info` — if it succeeds, uses Docker.
2. If Docker is unreachable, runs `podman info`. If Podman is found, sets `DOCKER_HOST` to the Podman socket automatically — probing well-known paths like `/run/user/<uid>/podman/podman.sock` on Linux and `~/.local/share/containers/podman/machine/qemu/podman.sock` on macOS.
3. If neither runtime is available, logs a warning and proceeds (the publish step may fail with a clear error).

When Podman is used, the publish runs with `-p:LocalRegistry=podman` so the .NET SDK loads
the image into Podman's local store rather than Docker's.

### Explicit configuration

Set `containerRuntime` in the state file or pass `--container-runtime` per run:

```bash
deployify publish --container-runtime podman
deployify init --non-interactive --target Nomad --container-runtime Podman
```

State file:

```json
{
  "environments": {
    "local": {
      "containerRuntime": "Podman"
    }
  }
}
```
