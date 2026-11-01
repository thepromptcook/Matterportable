#!/usr/bin/env bash
# Prepares a machine/container for working on this repo without Unity (idempotent):
#   .NET 8 SDK (-> /opt/dotnet if missing), glslang-tools (shader checks), Godot .NET + xvfb (headless Godot runs)
#   and a NuGet restore of the Unity reference assemblies (tools/UnityCompileCheck) and the Godot SDK. Then run tools/check.sh.
set -euo pipefail
cd "$(dirname "$0")/.."

if ! command -v dotnet >/dev/null && [[ ! -x /opt/dotnet/dotnet ]]; then
  echo "Installing .NET 8 SDK to /opt/dotnet…"
  curl -sSL https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 8.0 --install-dir /opt/dotnet >/dev/null
fi
DOTNET="$(command -v dotnet || echo /opt/dotnet/dotnet)"
if [[ ! -x /opt/dotnet10/dotnet ]]; then
  echo "Installing .NET 10 SDK to /opt/dotnet10 (the user's SDK; the web build is checked with both)…"
  curl -sSL https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /opt/dotnet10 >/dev/null || echo "  could not install .NET 10"
fi
echo "dotnet: $("$DOTNET" --version)"

if ! command -v glslangValidator >/dev/null; then
  echo "Installing glslang-tools (optional, for shader checks)…"
  (apt-get update -qq && apt-get install -y -qq glslang-tools) >/dev/null 2>&1 \
    || (sudo apt-get update -qq && sudo apt-get install -y -qq glslang-tools) >/dev/null 2>&1 \
    || echo "  could not install glslang-tools; shader checks will be skipped"
fi

GODOT_VERSION=4.7.2
if [[ ! -x /opt/godot/godot ]]; then
  echo "Installing Godot $GODOT_VERSION (.NET) to /opt/godot (headless runs of the Godot project)…"
  name="Godot_v${GODOT_VERSION}-stable_mono_linux_x86_64"
  curl -sSL -o /tmp/godot.zip "https://github.com/godotengine/godot/releases/download/${GODOT_VERSION}-stable/${name}.zip" &&
    rm -rf /opt/godot && unzip -q /tmp/godot.zip -d /tmp/godot-unzip && mv "/tmp/godot-unzip/$name" /opt/godot &&
    ln -s "/opt/godot/Godot_v${GODOT_VERSION}-stable_mono_linux.x86_64" /opt/godot/godot && rm -rf /tmp/godot.zip /tmp/godot-unzip \
    || echo "  could not install Godot; the Godot run check will be skipped"
fi
command -v xvfb-run >/dev/null || (apt-get install -y -qq xvfb >/dev/null 2>&1 || echo "  xvfb-run missing; the Godot run check will be skipped")

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$DOTNET" restore tools/UnityCompileCheck >/dev/null
"$DOTNET" restore tools/Matterportable.Core.Tests >/dev/null
"$DOTNET" restore godot/Matterportable.csproj >/dev/null
"$DOTNET" restore web/server >/dev/null
"$DOTNET" restore web/wasm/Matterportable.Wasm.csproj >/dev/null
NODE_PATH="$(npm root -g 2>/dev/null)" node -e "require('playwright')" 2>/dev/null \
  || echo "  node + playwright not found: the web run check will be skipped (npm i -g playwright && npx playwright install chromium)"
echo "Ready. Run: tools/check.sh   (add /opt/dotnet to PATH if dotnet is not found)"
