#!/usr/bin/env bash
# Runs the Godot project headless on the offline demo (virtual display, OpenGL/llvmpipe) and saves screenshots:
#   $OUT/inside.png (after moving from sweep 0 to sweep 1)  $OUT/dollhouse.png   (default OUT=/tmp/matterportable-godot; open them to look at the result)
# Fails on any engine, shader or script error. Needs Godot .NET (tools/setup-dev.sh installs it to /opt/godot)
# and xvfb-run. Extra arguments are passed to every run, e.g. --sweep 3 or --view 2.
set -uo pipefail
cd "$(dirname "$0")/.."
GODOT="${GODOT:-$(command -v godot || echo /opt/godot/godot)}"
OUT="${OUT:-/tmp/matterportable-godot}"
export PATH="/opt/dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-/opt/dotnet}" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
[[ -x "$GODOT" ]] || { echo "error: Godot not found (run tools/setup-dev.sh or set GODOT)"; exit 1; }
mkdir -p "$OUT"

# Godot rewrites the csproj SDK version to its own on open; keep the committed one.
cp godot/Matterportable.csproj "$OUT/csproj.bak"
trap 'cp "$OUT/csproj.bak" godot/Matterportable.csproj; rm -f godot/Matterportable.csproj.old' EXIT

dotnet build godot/Matterportable.csproj -v q -nologo 2>&1 | grep -E "error|warning CS" && { echo "error: Godot C# build failed"; exit 1; }
[[ -d godot/.godot ]] || timeout 120 "$GODOT" --headless --path godot --import >/dev/null 2>&1

rc=0
for mode in inside dollhouse; do
  extra=(); [[ $mode == inside ]] && extra=(--goto 1)   # exercise a full sweep-to-sweep transition
  log="$OUT/$mode.log"
  timeout 180 xvfb-run -a -s "-screen 0 1600x900x24" "$GODOT" --path godot --audio-driver Dummy \
    --rendering-driver opengl3 --rendering-method gl_compatibility -- --open demo --mode "$mode" --screenshot "$OUT/$mode.png" "${extra[@]}" "$@" >"$log" 2>&1
  status=$?
  if [[ $status != 0 ]] || grep -qE "ERROR|Unhandled exception|SCRIPT ERROR|SHADER ERROR" "$log" || ! grep -q "screenshot saved" "$log"; then
    echo "error: Godot $mode run failed (exit $status), log: $log"; grep -E "ERROR|rror|Exception" "$log" | head -15; rc=1
  else
    grep "\[Matterportable\]" "$log" | tail -1; echo "  screenshot: $OUT/$mode.png"
  fi
done
exit $rc
