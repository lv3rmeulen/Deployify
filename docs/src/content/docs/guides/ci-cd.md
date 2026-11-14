---
title: "CI/CD"
description: "Running Deployify from a pipeline."
---

## `ci`

Runs the full pipeline sequentially: generate, validate, plan, publish, apply. Any step
failure aborts and exits non-zero.

```bash
# Full pipeline: builds and pushes project images, then deploys them
deployify ci

# Generate + plan only (no publish, no deploy)
deployify ci --skip-apply

# Deploy images that another job already built and pushed
deployify ci --no-publish --image-tag "$GITHUB_SHA"

# Skip validation when the cluster is not reachable during the build phase
deployify ci --skip-validate --skip-apply

# Exit non-zero if the plan detects any drift (useful for change gating).
# A service that could not be checked counts as a change here.
deployify ci --skip-apply --fail-on-changes

# Deploy without a prompt (required when there is no interactive terminal)
deployify ci --auto-approve
```

After the plan, `ci` asks for confirmation in an interactive terminal before it publishes or
deploys anything. In a non-interactive run (a CI agent) it refuses unless `--auto-approve`
is passed, so a pipeline never pushes images or deploys by accident.

Steps printed during a run:

```
Image tag: 3f2a9c1e5b7d4e8a9c217d4e5f6a8b90c1d2e3f4 (commit SHA from GITHUB_SHA)
 1/5 Generate... ✓
 2/5 Validate... ✓
 3/5 Plan... ✓
 4/5 Publish... ✓
 5/5 Apply... ✓

✓ CI pipeline complete.
```

### Image tags

`ci` publishes project images with a tag that is unique to the build, so every deploy runs a
new image and a rollback can return to the previous one. With `latest` (the default) or no tag
configured, `ci` uses the commit SHA instead. It reads the SHA from the CI system's variable
(`GITHUB_SHA`, `BUILD_SOURCEVERSION`, `CI_COMMIT_SHA`, `BITBUCKET_COMMIT` or `GIT_COMMIT`),
or from `git rev-parse HEAD` when the working tree has no uncommitted changes.

- `--image-tag <tag>` sets the tag for the run.
- A configured tag other than `latest` (for example `1.4.2`) is used as is.
- When no SHA is available, `ci` stops. Pass `--image-tag`, or `--allow-mutable-tag` to publish
  `latest` anyway; a rollback then re-deploys whatever `latest` points to.

The commit tag applies only to runs that publish. `--skip-apply` and `--no-publish` use the
configured tag, so a drift check compares with what is deployed. After a `ci` deploy, run
the drift check with the same tag: `deployify ci --skip-apply --fail-on-changes --image-tag "$GITHUB_SHA"`.

The publish step builds projects and Dockerfile-based resources, then regenerates the
artifacts so the apply step deploys each image by the digest it was just pushed with. See
[publishing images](/guides/publishing-images/#pinning-images-by-digest).

Without a terminal, as on most CI runners, output is plain text with no colors, and long
lines aren't wrapped, so each message stays on one line in the log. Set `COLUMNS` to wrap
at a given width instead.

## Workflows

### GitHub Actions — full deploy

```yaml
- name: Deploy
  run: |
    deployify ci \
      --project-path src/MyApp.AppHost \
      --env prod \
      --skip-validate \
      --auto-approve
  env:
    NOMAD_TOKEN: ${{ secrets.NOMAD_TOKEN }}
```

### GitHub Actions — plan-gate (fail on drift)

```yaml
- name: Check for drift
  run: |
    deployify ci \
      --project-path src/MyApp.AppHost \
      --skip-validate \
      --skip-apply \
      --fail-on-changes
```

### GitHub Actions — build once, promote to production

Staging builds and deploys the images; production deploys the same images by digest, without
rebuilding them. When production uses another registry, `promote` copies the images there
first; the job then needs to be signed in to both registries.

```yaml
- name: Deploy to staging
  run: deployify ci --env staging --image-tag "$GITHUB_SHA" --skip-validate --auto-approve

# Later, for example in a job that needs an approval:
- name: Promote to production
  run: |
    deployify promote --from staging --to prod --image-tag "$GITHUB_SHA"
    deployify ci --env prod --no-publish --image-tag "$GITHUB_SHA" --require-digest \
      --skip-validate --auto-approve
```

See [promoting images](/guides/publishing-images/#promoting-images-to-another-environment).

### Separate steps

```bash
deployify generate --env prod
deployify validate --env prod
deployify plan     --env prod
deployify publish  --env prod
deployify apply    --env prod --auto-approve
```
