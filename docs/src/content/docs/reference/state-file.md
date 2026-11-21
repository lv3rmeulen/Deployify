---
title: "State file (deployify-state.json)"
description: "The configuration file Deployify reads for every environment."
---

```json
{
  "activeEnvironment": "local",
  "environments": {
    "local": {
      "target": "Nomad",
      "containerRuntime": "Docker",
      "containerRegistry": "registry.example.com/myapp",
      "imageTag": "latest",
      "outputPath": "deployify-output",
      "serviceOverrides": {
        "api": { "imageTag": "canary", "count": 2 }
      },
      "targetConfig": {
        "nomad": {
          "address": "http://localhost:4646",
          "namespace": "default",
          "datacenters": ["dc1"]
        }
      }
    },
    "prod": {
      "target": "Nomad",
      "containerRuntime": "Docker",
      "containerRegistry": "registry.example.com/myapp",
      "imageTag": "stable",
      "outputPath": "deployify-output",
      "targetConfig": {
        "nomad": {
          "address": "http://nomad.internal:4646",
          "token": "env:NOMAD_TOKEN",
          "namespace": "production",
          "datacenters": ["prod-dc1"]
        }
      }
    }
  }
}
```

## Layered environments

An environment can extend another and set only what differs, instead of repeating every
setting. `staging` and `prod` below start from `base` and keep everything they don't change:

```json
"environments": {
  "base": {
    "abstract": true,
    "target": "Nomad",
    "containerRegistry": "registry.example.com/myapp",
    "ignoreResources": ["storage", "frontend"],
    "parameters": { "db-user": "app", "region": "eu" },
    "targetConfig": {
      "nomad": { "address": "http://nomad.internal:4646", "datacenters": ["dc1"] }
    }
  },
  "staging": {
    "extends": "base",
    "imageTag": "staging",
    "targetConfig": { "nomad": { "namespace": "staging" } }
  },
  "prod": {
    "extends": "staging",
    "imageTag": "stable",
    "protected": true,
    "ignoreResources": ["storage"],
    "parameters": { "region": null },
    "targetConfig": { "nomad": { "namespace": "production", "token": "env:NOMAD_TOKEN" } }
  }
}
```

How the layers combine:

- **Objects merge key by key**, at any depth. `prod` above keeps the Nomad address from `base`
  and replaces only the namespace and token. This covers `targetConfig`, `parameters`,
  `serviceOverrides`, `secrets`, `dashboard` and every other object.
- **Everything else replaces** the base's value, lists included: `prod` ignores only `storage`.
- **`null` removes** the base's value, so the setting falls back to its default (or, in a map
  such as `parameters`, the entry is gone).
- **Chains** are allowed (`prod` → `staging` → `base`), up to 10 environments deep. A base that
  doesn't exist, or environments that extend each other in a loop, stop every command with a
  message naming the environments.
- **Not inherited:** `extends`, `abstract`, and the secret references `apply` records under
  `secrets.references`, which belong to the environment that wrote them.

An `abstract` environment only holds settings to extend. Commands refuse to deploy it,
`env use` refuses to switch to it, and `env list` marks it.

Deployify keeps the layers when it saves the file. An extending environment is written back
as what it set itself plus whatever the command changed in it, so `env set staging --image-tag 2.0`
adds one key to `staging`, and `env set base --container-registry …` reaches every
environment that inherits the registry. To add a layered environment from the command line:

```bash
deployify env add canary --extends prod --image-tag canary
```

`env remove` refuses to remove an environment that others extend.

A state file in which an environment extends another is written in format version 2, which
earlier releases refuse rather than deploy an environment without its base's settings. Files
without layers stay at version 1.

## Secrets

Don't put tokens and passwords in `deployify-state.json` as plain text. Any secret field
(Nomad and Consul tokens, registry passwords, artifact store credentials, the dashboard
token) accepts an environment variable reference instead:

```json
"token": "env:NOMAD_TOKEN"
```

When a secret field is left empty, Deployify also falls back to the usual variable for it:
`NOMAD_TOKEN`, `CONSUL_HTTP_TOKEN` and `DEPLOYIFY_REGISTRY_PASSWORD`. `init` masks secret
prompts and suggests the `env:` form.

Add these to your `.gitignore`:

```text
deployify-state.json
deployify-output/
**/Properties/launchSettings.json
```

Deployify prints a warning after saving the state file when it contains a literal secret and
git does not ignore the file. If `launchSettings.json` or the state file was already
committed, remove it from the index with `git rm --cached <file>` and rotate any secrets it held.

## Ignored resources

Deployify deploys projects, Dockerfile builds and containers with an image. Before planning, it
checks every other resource in the manifest and reports the ones it can't deploy: executables
(`executable.v0`), containers with neither an image nor a build, Azure and AWS
infrastructure (`azure.*`, `aws.*`), and types it doesn't know. Parameters, values, Dapr
resources and child resources such as databases need no deploying and aren't reported.

When a deployed service depends on such a resource, `plan` fails and `apply`, `ci` and
`aspire deploy` stop before changing anything. Otherwise Deployify warns and deploys the rest.
If you run a resource another way, list it under `ignoreResources` to turn the error or
warning into an info line:

```json
"ignoreResources": ["frontend", "storage"]
```

Deployify still reports names in the list that aren't in the manifest, or that it deploys
anyway, so the list doesn't go stale. Pass `--strict` to `plan`, `apply` or `ci` to fail on
warnings too.

## Service overrides

Override image tag, replica count, or environment variables per service:

```json
"serviceOverrides": {
  "api":    { "imageTag": "v1.2.3", "count": 3 },
  "worker": { "count": 2, "env": { "LOG_LEVEL": "debug" } }
}
```

## SBOMs

`sbom` turns on SBOM generation at publish and attaches each SBOM to the pushed image as a
signed cosign attestation. It needs syft and cosign installed. See
[SBOMs and attestations](/Deployify/guides/publishing-images/#sboms-and-attestations).

```json
"sbom": { "enabled": true, "format": "Spdx", "key": "awskms:///alias/deployify-signing" }
```

## Signing

`signing` turns on signing the images publish pushes with cosign, and says which signatures
`--require-signature` on `apply`, `ci` and `promote` trusts. See
[signing images](/Deployify/guides/publishing-images/#signing-images).

```json
"signing": { "enabled": true, "key": "awskms:///alias/deployify-signing" }
```
