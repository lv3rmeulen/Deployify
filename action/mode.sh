#!/usr/bin/env bash
# Decides whether this run plans or deploys, and whether it may comment.
set -euo pipefail

mode="${INPUT_MODE:-auto}"
comment=false

case "$mode" in
  plan | deploy) ;;
  auto)
    case "$EVENT" in
      pull_request | pull_request_target) mode=plan ;;
      workflow_dispatch) mode=deploy ;;
      push)
        if [ -n "${DEFAULT_BRANCH:-}" ] && [ "$REF" = "refs/heads/$DEFAULT_BRANCH" ]; then
          mode=deploy
        else
          mode=plan
          echo "::notice title=Deployify::Push to $REF, not the default branch ($DEFAULT_BRANCH): planning only."
        fi
        ;;
      *)
        echo "::error title=Deployify::mode: auto doesn't know what to do on '$EVENT'. Set mode to plan or deploy."
        exit 1
        ;;
    esac
    ;;
  *)
    echo "::error title=Deployify::mode must be auto, plan or deploy, not '$mode'."
    exit 1
    ;;
esac

if [ "$mode" = plan ] && { [ "$EVENT" = pull_request ] || [ "$EVENT" = pull_request_target ]; }; then
  comment=true
fi

echo "Mode: $mode"
{
  echo "mode=$mode"
  echo "comment=$comment"
} >> "$GITHUB_OUTPUT"
