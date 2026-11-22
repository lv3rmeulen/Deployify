#!/usr/bin/env bash
# Runs deployify drift with JSON output: every environment when the env input is empty, otherwise
# that one. Never fails the step itself: the report step runs first, and a later step fails the
# job with drift's exit code (0 in sync, 2 drifted, 1 a check or alert failed).
set -uo pipefail
source "$GITHUB_ACTION_PATH/action/common.sh"

out="$RUNNER_TEMP/deployify-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT-$RANDOM"
mkdir -p "$out"

drift_args=("${common_args[@]}")
[ -z "${DEPLOYIFY_ENV:-}" ] && drift_args+=(--all)
[ "${NOTIFY:-true}" = true ] || drift_args+=(--no-notify)

echo "::group::deployify drift"
deployify drift "${drift_args[@]}" "${extra_args[@]}" --output json > "$out/drift.json"
code=$?
echo "::endgroup::"
echo "drift-json=$out/drift.json" >> "$GITHUB_OUTPUT"
echo "exit-code=$code" >> "$GITHUB_OUTPUT"
exit 0
