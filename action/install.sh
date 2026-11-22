#!/usr/bin/env bash
# Installs the deployify .NET tool globally.
set -euo pipefail

version="${INPUT_VERSION:-}"

# 'latest' -> the newest release, whatever the action's tag.
# No version given: follow the action's own tag. v1.2.0 / v1.2.0-alpha.3 -> that version,
# v1 / v1.2 -> the newest release in that line. A branch or SHA ref -> the newest release.
if [ "$version" = latest ]; then
  version=""
elif [ -z "$version" ]; then
  ref="${ACTION_REF:-}"
  if [[ "$ref" =~ ^v([0-9]+\.[0-9]+\.[0-9]+.*)$ ]]; then
    version="${BASH_REMATCH[1]}"
  elif [[ "$ref" =~ ^v([0-9]+(\.[0-9]+)?)$ ]]; then
    version="${BASH_REMATCH[1]}.*"
  fi
fi

tools_dir="$HOME/.dotnet/tools"
echo "$tools_dir" >> "$GITHUB_PATH"
export PATH="$tools_dir:$PATH"

source_args=()
[ -n "${INPUT_SOURCE:-}" ] && source_args+=(--add-source "$INPUT_SOURCE")

verb=install
if dotnet tool list --global | awk 'NR>2 {print $1}' | grep -qx deployify; then
  verb=update
fi

if [ -n "$version" ]; then
  echo "Installing deployify $version"
  dotnet tool "$verb" --global deployify "${source_args[@]}" --version "$version"
else
  echo "Installing the newest deployify"
  # Before 1.0 there is no stable release, so fall back to the newest prerelease.
  dotnet tool "$verb" --global deployify "${source_args[@]}" ||
    dotnet tool "$verb" --global deployify "${source_args[@]}" --prerelease
fi

installed="$(dotnet tool list --global | awk 'NR>2 && $1 == "deployify" {print $2}')"
echo "Installed deployify $installed"
echo "version=$installed" >> "$GITHUB_OUTPUT"
