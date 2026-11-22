---
title: "GitHub Action"
description: "Plan on pull requests, deploy on merge, with the Deployify GitHub Action."
---

The Deployify action installs the CLI and, by default:

- on a **pull request**, runs `deployify plan` and posts the plan as a comment, updated in place
  on every push;
- on a **push to the default branch** (a merged pull request), runs `deployify ci`: generate,
  validate, plan, publish and apply.

```yaml
# .github/workflows/deploy.yml
name: Deploy

on:
  pull_request:
  push:
    branches: [main]

# One deploy per environment at a time; a newer push waits rather than cancelling a deploy.
concurrency:
  group: deploy-prod-${{ github.event_name }}-${{ github.event.pull_request.number || github.ref }}
  cancel-in-progress: ${{ github.event_name == 'pull_request' }}

permissions:
  contents: read
  pull-requests: write   # the plan comment
  packages: write        # pushing images to GHCR

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6
        with:
          fetch-depth: 0

      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - uses: lv3rmeulen/Deployify@v1
        with:
          project-path: src/MyApp.AppHost
          env: prod
        env:
          # Whatever your target needs, as for the CLI; for Nomad:
          NOMAD_TOKEN: ${{ secrets.NOMAD_TOKEN }}
```

To require an approval before a deploy, move the deploy into its own job with an
[environment](https://docs.github.com/actions/deployment/targeting-different-environments/using-environments-for-deployment)
that has required reviewers, and keep the plan job without one.

The plan job needs to reach the target, just like `deployify plan` on your machine: give it the
same credentials as the deploy. To sign in to AWS or Azure without stored keys, see
[signing in from CI](/Deployify/guides/ci-cloud-sign-in/).

## The plan comment

The comment shows what `apply` would create, update and delete, services that couldn't be
checked, manifest findings, and, from `deployify diff`, each service's changes in a collapsed
block. Each environment and AppHost gets its own comment, so a workflow that plans `staging` and
`prod` keeps two.

When the plan is blocked (for example, a parameter a deployed service uses has no value) or
fails, the comment says why and the job fails, so a required check stops the merge.

Secret parameter values Deployify resolved are masked in the diff (see
[JSON output](/Deployify/reference/json-output/#diff)). Turn the diff off with `diff: false` if
your services' configuration shouldn't appear in pull requests at all.

Pull requests from forks get a read-only token and no secrets: the plan can't reach the target
and the comment can't be posted. The action warns instead of failing on the comment.

## Choosing the version

`lv3rmeulen/Deployify@v1.2.0` installs Deployify 1.2.0 and `@v1` the newest 1.x. Pin a
version with the `version` input instead when you reference the action by commit SHA.

## Inputs

| Input | Default | |
|---|---|---|
| `version` | the action's tag | Deployify version to install. `latest` for the newest release. |
| `source` | | Extra NuGet source (feed URL or folder) to install Deployify from. |
| `mode` | `auto` | `auto`, `plan` or `deploy`. `auto` plans on `pull_request`, deploys on a push to the default branch and on `workflow_dispatch`, and plans without deploying on a push to another branch. |
| `project-path` | | Path to the AppHost `.csproj` or its directory. |
| `env` | active environment | Deployify environment. |
| `target` | the environment's | Deployment target override. |
| `working-directory` | `.` | Directory to run Deployify in. |
| `comment` | `true` | Post the plan on the pull request. |
| `diff` | `true` | Include each service's changes in the comment. |
| `image-tag` | the commit SHA | Image tag for the deploy (`ci --image-tag`). |
| `skip-validate` | `false` | `ci --skip-validate`. |
| `strict` | `false` | `--strict` for plan and deploy. |
| `plan-args` | | Extra `plan` arguments, space-separated. |
| `deploy-args` | | Extra `ci` arguments, space-separated, for example `--require-signature --prune`. |
| `setup-dotnet` | `true` | Install the .NET SDK (from `global.json` if there is one, otherwise 10.0.x). |
| `github-token` | `github.token` | Token for the comment; needs `pull-requests: write`. |

## Outputs

| Output | |
|---|---|
| `mode` | `plan` or `deploy`. |
| `plan-json` | Path to the `deployify plan --output json` document. |
| `up-to-date` | `true` when the plan has no changes. |
| `comment-url` | The plan comment. |
| `version` | The Deployify version that ran. |

## Build once, promote

For staging-then-production with the same images, run the action in `deploy` mode for staging
and promote in a later job; see
[build once, promote to production](/Deployify/guides/ci-cd/#github-actions--build-once-promote-to-production):

```yaml
- uses: lv3rmeulen/Deployify@v1
  with:
    mode: deploy
    env: prod
    deploy-args: --no-publish --require-digest
```
