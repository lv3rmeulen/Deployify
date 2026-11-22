# Arguments shared by plan, diff and ci, built from the action's inputs.
# shellcheck disable=SC2034  # sourced by plan.sh and deploy.sh
common_args=()
[ -n "${PROJECT_PATH:-}" ] && common_args+=(--project-path "$PROJECT_PATH")
[ -n "${DEPLOYIFY_ENV:-}" ] && common_args+=(--env "$DEPLOYIFY_ENV")
[ -n "${TARGET:-}" ] && common_args+=(--target "$TARGET")
extra_args=()
[ -n "${EXTRA_ARGS:-}" ] && read -r -a extra_args <<< "$EXTRA_ARGS"
