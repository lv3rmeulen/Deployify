#!/usr/bin/env bash
# Every place that pins the Aspire version. They have to move together: Deployify.Hosting builds
# on Aspire's experimental pipeline APIs, and the samples' AppHost SDK, integrations and the
# Aspire CLI in samples.yml must match Aspire.Hosting.
#
#   tools/aspire-version.sh            prints the pinned version; fails if the pins disagree
#   tools/aspire-version.sh <version>  sets every pin to <version>
#
# Not covered, check them by hand when you move Aspire: StreamJsonRpc in Directory.Packages.props
# (kept at Aspire.Hosting's own dependency) and src/Deployify.ResourceServer/Protos/dashboard_service.proto.
set -euo pipefail
cd "$(dirname "$0")/.."

packages=(Directory.Packages.props samples/*/Directory.Packages.props)
apphosts=(samples/*/*.AppHost/*.AppHost.csproj)
workflows=(.github/workflows/samples.yml)

pins() {
  grep -HoE '<PackageVersion Include="Aspire\.[^"]*" Version="[^"]*"' "${packages[@]}" \
    | sed -E 's/^([^:]*):.*Include="([^"]*)" Version="([^"]*)"/\3\t\1 (\2)/' || true
  grep -HoE 'Sdk="Aspire\.AppHost\.Sdk/[^"]*"' "${apphosts[@]}" \
    | sed -E 's|^([^:]*):.*/([^"]*)"|\2\t\1 (Aspire.AppHost.Sdk)|' || true
  grep -HoE 'Aspire\.Cli --version [^ [:space:]]+' "${workflows[@]}" \
    | sed -E 's/^([^:]*):.* --version (.*)/\2\t\1 (Aspire.Cli)/' || true
}

if [ $# -eq 0 ]; then
  list=$(pins)
  versions=$(cut -f1 <<<"$list" | sort -u)
  if [ "$(wc -l <<<"$versions")" -ne 1 ]; then
    echo "The Aspire pins disagree:" >&2
    sed 's/^/  /' <<<"$list" >&2
    exit 1
  fi
  echo "$versions"
  exit 0
fi

v=$1
if ! [[ $v =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "Not a version: $v" >&2
  exit 2
fi
sed -i -E "s/(<PackageVersion Include=\"Aspire\.[^\"]*\" Version=\")[^\"]*/\1$v/" "${packages[@]}"
sed -i -E "s|(Sdk=\"Aspire\.AppHost\.Sdk/)[^\"]*|\1$v|" "${apphosts[@]}"
sed -i -E "s/(Aspire\.Cli --version )[^ [:space:]]+/\1$v/" "${workflows[@]}"
pins | sed 's/^/  /'
