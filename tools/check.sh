#!/usr/bin/env bash
# Runs every check that works without Unity. Exit code 0 = all passed.
#   tools/check.sh          everything
#   tools/check.sh --fast   skip the Unity compile checks and the Godot/web runs (core tests + scripts only)
# First time on a new machine/container: tools/setup-dev.sh
set -uo pipefail
cd "$(dirname "$0")/.."

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
DOTNET="$(command -v dotnet || echo /opt/dotnet/dotnet)"
FAST=0
[[ "${1:-}" == "--fast" ]] && FAST=1

failed=()
step() {
  local name="$1"; shift
  local log; log="$(mktemp)"
  if "$@" >"$log" 2>&1; then
    echo "ok    $name"
  else
    echo "FAIL  $name"
    grep -E "error|Error|FAIL|Failed|failed|missing|orphaned|unused" "$log" | grep -v "^\s*$" | head -25 | sed 's/^/      /'
    failed+=("$name")
  fi
  rm -f "$log"
}
skip() { echo "skip  $1 ($2)"; }

if [[ ! -x "$DOTNET" ]]; then
  echo "dotnet not found - run tools/setup-dev.sh"; exit 1
fi

step "core tests (dotnet test)" "$DOTNET" test tools/Matterportable.Core.Tests
if [[ $FAST == 0 ]]; then
  step "unity compile: editor + tests" "$DOTNET" build tools/UnityCompileCheck -warnaserror:CS0618
  step "unity compile: player" "$DOTNET" build tools/UnityCompileCheck -p:Player=true
  step "unity compile: input system path" "$DOTNET" build tools/UnityCompileCheck -p:InputSystem=true
fi
step "cli build" "$DOTNET" build tools/Matterportable.Cli
step "godot compile" "$DOTNET" build godot/Matterportable.csproj -warnaserror
step "web compile (server + WebAssembly Core)" "$DOTNET" build web/server
# The user builds with .NET 10; SDK versions differ in how the WebAssembly Core publishes, so build with it too.
# Built in a copy (kept between runs, so it is incremental): two SDKs sharing obj/ folders break each other.
web_build_net10() {
  local tmp=/tmp/matterportable-net10 core=Packages/com.thepromptcook.matterportable/Runtime/Core
  mkdir -p "$tmp"
  tar --exclude=bin --exclude=obj -cf - web "$core" | tar -C "$tmp" -xf - &&
    /opt/dotnet10/dotnet build "$tmp/web/server" -nodeReuse:false -v q -nologo &&
    test -f "$tmp"/web/server/bin/Debug/net10.0/wwwroot/_framework/dotnet.js
}
if [[ -x /opt/dotnet10/dotnet ]]; then step "web compile with .NET 10 (the user's SDK)" web_build_net10
else skip "web compile with .NET 10" "run tools/setup-dev.sh"; fi

if command -v glslangValidator >/dev/null; then step "shaders (glslang HLSL)" python3 tools/check_shaders.py
else skip "shaders" "glslang-tools not installed"; fi
if [[ $FAST == 0 ]] && [[ -x "${GODOT:-/opt/godot/godot}" ]] && command -v xvfb-run >/dev/null; then
  step "godot run (demo screenshots in /tmp/matterportable-godot)" tools/godot-check.sh
else skip "godot run" "Godot or xvfb-run not installed"; fi
godot_uids_committed() {
  local missing=0
  for f in $(git ls-files 'godot/*.cs' 'godot/*.gd' 'godot/*.gdshader' 'godot/*.gdshaderinc'); do
    git ls-files --error-unmatch "$f.uid" >/dev/null 2>&1 || { echo "missing: $f.uid is not committed (run tools/godot-check.sh, then git add it)"; missing=1; }
  done
  return $missing
}
step "godot .uid files committed (else users' pulls fail)" godot_uids_committed
if [[ $FAST == 0 ]] && command -v node >/dev/null && NODE_PATH="$(npm root -g 2>/dev/null)" node -e "require('playwright')" 2>/dev/null; then
  step "web run (demo screenshots in /tmp/matterportable-web)" tools/web-check.sh
else skip "web run" "node + playwright not installed"; fi
step ".meta files" python3 tools/generate_metas.py --check
step "no unused members" python3 tools/find_unused.py

echo
if [[ ${#failed[@]} -eq 0 ]]; then echo "All checks passed."; exit 0; fi
echo "${#failed[@]} check(s) failed: ${failed[*]}"; exit 1
