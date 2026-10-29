#!/usr/bin/env bash
# Build the signed universal app using the repository's packaging workflow.
set -euo pipefail

MODE="${1:-run}"
case "$MODE" in
    run|--debug|--logs|--telemetry|--verify) ;;
    *) printf 'Usage: %s [--debug|--logs|--telemetry|--verify]\n' "$0" >&2; exit 2 ;;
esac
[[ $# -le 1 ]] || { printf 'Expected at most one option.\n' >&2; exit 2; }

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
APP_BUNDLE="$ROOT_DIR/dist/Agent Speed.app"
APP_BINARY="$APP_BUNDLE/Contents/MacOS/AgentSpeed"

# Swift Testing and universal builds need full Xcode, not Command Line Tools.
if [[ -z "${DEVELOPER_DIR:-}" && -d /Applications/Xcode.app/Contents/Developer ]]; then
    export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
fi

pkill -x AgentSpeed >/dev/null 2>&1 || true
for ((attempt = 0; attempt < 50; attempt++)); do
    pgrep -x AgentSpeed >/dev/null || break
    sleep 0.1
done
if pgrep -x AgentSpeed >/dev/null; then
    printf 'Agent Speed is still stopping; try again after it exits.\n' >&2
    exit 1
fi

"$ROOT_DIR/macos/scripts/build-app.sh"

if [[ "$MODE" == --debug ]]; then
    exec xcrun lldb -- "$APP_BINARY"
fi
/usr/bin/open -n "$APP_BUNDLE"

case "$MODE" in
    --verify)
        sleep 2
        pgrep -x AgentSpeed >/dev/null
        printf 'Agent Speed launched: %s\n' "$APP_BUNDLE"
        ;;
    --logs)
        exec /usr/bin/log stream --info --style compact --predicate 'process == "AgentSpeed"'
        ;;
    --telemetry)
        exec /usr/bin/log stream --info --style compact --predicate 'subsystem == "io.github.zoefix.agentspeed"'
        ;;
esac
