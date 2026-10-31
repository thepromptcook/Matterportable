#!/usr/bin/env bash
# Prepares a machine/container for working on this repo without Unity (idempotent):
#   .NET 8 SDK (-> /opt/dotnet if missing), glslang-tools (shader checks), and a NuGet restore of the
#   Unity reference assemblies used by tools/UnityCompileCheck. Then run tools/check.sh.
set -euo pipefail
cd "$(dirname "$0")/.."

if ! command -v dotnet >/dev/null && [[ ! -x /opt/dotnet/dotnet ]]; then
  echo "Installing .NET 8 SDK to /opt/dotnet…"
  curl -sSL https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 8.0 --install-dir /opt/dotnet >/dev/null
fi
DOTNET="$(command -v dotnet || echo /opt/dotnet/dotnet)"
echo "dotnet: $("$DOTNET" --version)"

if ! command -v glslangValidator >/dev/null; then
  echo "Installing glslang-tools (optional, for shader checks)…"
  (apt-get update -qq && apt-get install -y -qq glslang-tools) >/dev/null 2>&1 \
    || (sudo apt-get update -qq && sudo apt-get install -y -qq glslang-tools) >/dev/null 2>&1 \
    || echo "  could not install glslang-tools; shader checks will be skipped"
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$DOTNET" restore tools/UnityCompileCheck >/dev/null
"$DOTNET" restore tools/Matterportable.Core.Tests >/dev/null
echo "Ready. Run: tools/check.sh   (add /opt/dotnet to PATH if dotnet is not found)"
