#!/usr/bin/env bash
# Runs the full pipeline: generate, validate, plan, publish, apply.
set -euo pipefail
source "$GITHUB_ACTION_PATH/action/common.sh"

args=("${common_args[@]}" --auto-approve)
[ -n "${IMAGE_TAG:-}" ] && args+=(--image-tag "$IMAGE_TAG")
[ "${SKIP_VALIDATE:-false}" = true ] && args+=(--skip-validate)
[ "${STRICT:-false}" = true ] && args+=(--strict)

deployify ci "${args[@]}" "${extra_args[@]}"

{
  echo "### Deployify deploy"
  echo
  echo "Deployed \`${GITHUB_SHA:0:7}\`${DEPLOYIFY_ENV:+ to \`$DEPLOYIFY_ENV\`}${IMAGE_TAG:+ with image tag \`$IMAGE_TAG\`}."
} >> "$GITHUB_STEP_SUMMARY"
