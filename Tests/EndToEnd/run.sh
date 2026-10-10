#!/usr/bin/env bash
set -euo pipefail
umask 077
if [[ $# != 4 && $# != 5 ]]; then
  echo 'Usage: run.sh QUASAR_CHECKOUT BACKEND_CHECKOUT PLUGIN_SDK_DIRECTORY PRIVATE_ARTIFACTS_DIRECTORY [SERVER_LIST_CHECKOUT]' >&2
  exit 2
fi
quasar_directory="$(cd -- "$1" && pwd)"
backend_directory="$(cd -- "$2" && pwd)"
export MagnetarBin="$(cd -- "$3" && pwd)"
mkdir -p -- "$4"
artifacts_directory="$(cd -- "$4" && pwd)"
repo_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
project="$repo_directory/Tests/EndToEnd/EndToEnd.csproj"
project_args=(-p:QuasarRepo="$quasar_directory" -p:BackendRepo="$backend_directory")
runtime_args=()
if [[ $# == 5 ]]; then
  site_directory="$(cd -- "$5" && pwd)"
  project_args+=(-p:ServerListRepo="$site_directory")
  runtime_args+=("$site_directory/Server/bin/Debug/net10.0/Server.dll")
fi
if [[ "${DIAGNOSTICS_SKIP_PACKAGE_BUILD:-0}" != 1 ]]; then
  "$quasar_directory/scripts/bootstrap-diagnostics.sh" "$repo_directory/Diagnostics/CometWorks.Diagnostics.csproj"
fi
# The unpublished local prerelease is repacked under one version during development.
# Refresh only this fixture's generated package cache, never the user's global cache.
rm -rf -- "$artifacts_directory/packages/cometworks.diagnostics/0.1.0"
dotnet restore "$project" --configfile "$quasar_directory/NuGet.Config" --packages "$artifacts_directory/packages" \
  "${project_args[@]}"
dotnet build "$project" --no-restore "${project_args[@]}"
dotnet build "$quasar_directory/Quasar.Host/Quasar.Host.csproj" --configfile "$quasar_directory/NuGet.Config" \
  -p:RestorePackagesPath="$artifacts_directory/packages"
echo 'END-TO-END BUILD COMPLETE; starting isolated runtime fixtures.'
dotnet "$repo_directory/Tests/EndToEnd/bin/Debug/net10.0/Quasar.Tests.dll" \
  "$quasar_directory/Quasar.Host/bin/Debug/net10.0/Quasar.Host.dll" \
  "$backend_directory/Backend/bin/Debug/net10.0/Backend.dll" "$artifacts_directory/run-$(date -u +%Y%m%dT%H%M%S)-$$" "${runtime_args[@]}"
