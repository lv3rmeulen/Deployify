---
title: "CI/CD"
description: "Running Deployify from a pipeline."
---

## `ci`

Runs the full pipeline sequentially. Any step failure aborts and exits non-zero.

```bash
# Full pipeline
deployify ci

# Generate + plan only (no deploy)
deployify ci --skip-apply

# Skip validation when the cluster is not reachable during the build phase
deployify ci --skip-validate --skip-apply

# Exit non-zero if the plan detects any drift (useful for change gating).
# A service that could not be checked counts as a change here.
deployify ci --skip-apply --fail-on-changes

# Deploy without a prompt (required when there is no interactive terminal)
deployify ci --auto-approve
```

When `ci` reaches the apply step it asks for confirmation in an interactive terminal. In a
non-interactive run (a CI agent) it refuses to apply unless `--auto-approve` is passed, so
a pipeline never deploys by accident.

Steps printed during a run:

```
 1/4 Generate... ✓
 2/4 Validate... ✓
 3/4 Plan... ✓
 4/4 Apply... ✓

✓ CI pipeline complete.
```

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

### Separate steps

```bash
deployify generate --env prod
deployify validate --env prod
deployify plan     --env prod
deployify apply    --env prod --auto-approve
```
