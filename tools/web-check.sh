#!/usr/bin/env bash
# Runs the web version in headless Chromium against a local server and saves screenshots:
#   $OUT/inside.png (after moving from sweep 0 to sweep 1)  $OUT/dollhouse.png  $OUT/measure.png
# (default OUT=/tmp/matterportable-web). Fails on page errors. Extra arguments go into the page URL (e.g. sweep=3,
# view=2). Real-space comparison: tools/web-check.sh --compare <model id> [sweeps], then
# python3 tools/compare_alignment.py $OUT. One page: tools/web-check.sh --page <name> '<query>' -> $OUT/<name>.png.
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="/opt/dotnet:$PATH" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
OUT="${OUT:-/tmp/matterportable-web}"
PORT="${PORT:-8093}"
mkdir -p "$OUT"
dotnet build web/server -v q -nologo 2>&1 | grep -E " error |error MSB" && { echo "error: web server build failed"; exit 1; }
ASPNETCORE_URLS="http://127.0.0.1:$PORT" dotnet "$(ls web/server/bin/Debug/net*/matterportable-web.dll | head -1)" >"$OUT/server.log" 2>&1 &
SERVER=$!
trap 'kill $SERVER 2>/dev/null' EXIT
for _ in $(seq 120); do curl -sf "http://127.0.0.1:$PORT/api/spaces" >/dev/null && break; sleep 0.5; done
RUN=(); command -v xvfb-run >/dev/null && RUN=(xvfb-run -a -s "-screen 0 1920x1080x24")
NODE_PATH="$(npm root -g)" "${RUN[@]}" node tools/web_check.js "http://127.0.0.1:$PORT/" "$OUT" "$@"
