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

## Service overrides

Override image tag, replica count, or environment variables per service:

```json
"serviceOverrides": {
  "api":    { "imageTag": "v1.2.3", "count": 3 },
  "worker": { "count": 2, "env": { "LOG_LEVEL": "debug" } }
}
```
