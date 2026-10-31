#!/usr/bin/env bash
# Runs every check that works without Unity. Exit code 0 = all passed.
#   tools/check.sh          everything
#   tools/check.sh --fast   skip the Unity compile checks (core tests + scripts only)
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

browser_script_up_to_date() {
  local tmp; tmp="$(mktemp)"
  "$DOTNET" run --project tools/Matterportable.Cli --no-build -- browser-script >"$tmp" &&
    diff -q "$tmp" "Packages/com.thepromptcook.matterportable/Tools~/browser-export.js" >/dev/null
  local rc=$?; rm -f "$tmp"
  [[ $rc == 0 ]] || echo "error: Tools~/browser-export.js is stale - regenerate: dotnet run --project tools/Matterportable.Cli -- browser-script > Packages/com.thepromptcook.matterportable/Tools~/browser-export.js"
  return $rc
}
step "browser-export.js matches the C# query" browser_script_up_to_date

if command -v node >/dev/null; then step "browser export (node, mock endpoint)" node tools/test_browser_export.js
else skip "browser export" "node not installed"; fi
if command -v glslangValidator >/dev/null; then step "shaders (glslang HLSL)" python3 tools/check_shaders.py
else skip "shaders" "glslang-tools not installed"; fi
step ".meta files" python3 tools/generate_metas.py --check
step "no unused members" python3 tools/find_unused.py

echo
if [[ ${#failed[@]} -eq 0 ]]; then echo "All checks passed."; exit 0; fi
echo "${#failed[@]} check(s) failed: ${failed[*]}"; exit 1
