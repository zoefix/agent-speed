#!/bin/sh
set -eu
ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
TEST_ROOT=$(mktemp -d /tmp/agent-speed-smoke.XXXXXX)
export XDG_CONFIG_HOME="$TEST_ROOT/config" XDG_DATA_HOME="$TEST_ROOT/data" XDG_CACHE_HOME="$TEST_ROOT/cache"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET=${DOTNET:-dotnet}
"$DOTNET" build "$ROOT/linux/src/AgentSpeed.Linux" -c Release --nologo
timeout 45s "$DOTNET" "$ROOT/linux/src/AgentSpeed.Linux/bin/Release/net10.0/agent-speed.dll" --smoke-test
test -s "$TEST_ROOT/cache/agent-speed/smoke-dashboard.png"
test -s "$TEST_ROOT/cache/agent-speed/smoke-dashboard-zh-dark.png"
test -s "$TEST_ROOT/cache/agent-speed/smoke-dashboard-compact.png"
test -s "$TEST_ROOT/cache/agent-speed/smoke-dashboard-light.png"
test -s "$TEST_ROOT/cache/agent-speed/smoke-dashboard-sources.png"
test -s "$TEST_ROOT/cache/agent-speed/smoke-history.png"
test -s "$TEST_ROOT/cache/agent-speed/smoke-history-today.png"
test -s "$TEST_ROOT/cache/agent-speed/smoke-history-light.png"
test -s "$TEST_ROOT/cache/agent-speed/smoke-history-compact.png"
test -s "$TEST_ROOT/cache/agent-speed/smoke-settings.png"
test -s "$TEST_ROOT/cache/agent-speed/smoke-settings-zh.png"
for index in 0 1 2 3 4 5 6 7; do test -s "$TEST_ROOT/cache/agent-speed/smoke-settings-$index-compact.png"; done
for style in Led Needle Liquid Reactor Minimal; do test -s "$TEST_ROOT/cache/agent-speed/smoke-$style.png"; done
echo "Smoke-test images: $TEST_ROOT/cache/agent-speed"
