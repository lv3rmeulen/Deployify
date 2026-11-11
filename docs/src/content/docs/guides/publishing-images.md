---
title: "Publishing images"
description: "How Deployify builds and pushes project images, with Docker or Podman."
---

## `publish`

```bash
deployify publish
deployify publish --image-tag v1.2.3
deployify publish --container-runtime podman
```

## Container runtime

Deployify uses `dotnet publish -t:PublishContainer` to build images. By default it tries Docker; if Docker is unavailable it automatically falls back to Podman.

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
