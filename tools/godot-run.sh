#!/usr/bin/env bash
# One headless Godot run with the launcher's arguments (see the dev skill), e.g.
#   tools/godot-run.sh --open rGMMnZDh3Tf --mode walk --sweep 35 --yaw 46 --pitch 35 --move-prop -50 --screenshot /tmp/a.png
# Builds the C# first, restores the committed csproj afterwards (Godot rewrites its SDK version), and prints only
# our log lines and errors. RES=WxH sets the window (default 1280x720).
set -uo pipefail
cd "$(dirname "$0")/.."
GODOT="${GODOT:-$(command -v godot || echo /opt/godot/godot)}"
RES="${RES:-1280x720}"
export PATH="/opt/dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-/opt/dotnet}" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
backup=$(mktemp)
cp godot/Matterportable.csproj "$backup"
trap 'cp "$backup" godot/Matterportable.csproj; rm -f "$backup" godot/Matterportable.csproj.old' EXIT
dotnet build godot/Matterportable.csproj -v q -nologo 2>&1 | grep -E "error|warning CS" && { echo "error: Godot C# build failed"; exit 1; }
timeout "${TIMEOUT:-300}" xvfb-run -a -s "-screen 0 ${RES}x24" "$GODOT" --path godot --resolution "$RES" --audio-driver Dummy \
  --rendering-method gl_compatibility -- "$@" 2>&1 | grep -E "^\[Matterportable\]|SCRIPT ERROR|SHADER ERROR|Unhandled exception|^ERROR: .*(shader|script|\.cs)"
exit "${PIPESTATUS[0]}"
