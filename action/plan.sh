#!/usr/bin/env bash
# Runs deployify plan (and diff) with JSON output. Never fails the step itself: the report step
# needs to run first, and a later step fails the job with plan's exit code.
set -uo pipefail
source "$GITHUB_ACTION_PATH/action/common.sh"

out="$RUNNER_TEMP/deployify-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT-$RANDOM"
mkdir -p "$out"

plan_args=("${common_args[@]}")
[ "${STRICT:-false}" = true ] && plan_args+=(--strict)

echo "::group::deployify plan"
deployify plan "${plan_args[@]}" "${extra_args[@]}" --output json > "$out/plan.json"
code=$?
echo "::endgroup::"
echo "plan-json=$out/plan.json" >> "$GITHUB_OUTPUT"
echo "exit-code=$code" >> "$GITHUB_OUTPUT"

if [ "$code" = 0 ] && [ "${RUN_DIFF:-true}" = true ]; then
  echo "::group::deployify diff"
  if deployify diff "${common_args[@]}" --summary --output json > "$out/diff.json"; then
    echo "diff-json=$out/diff.json" >> "$GITHUB_OUTPUT"
  else
    echo "::warning title=Deployify::deployify diff failed; the comment lists services without their changes."
  fi
  echo "::endgroup::"
fi
exit 0
