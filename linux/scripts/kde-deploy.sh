#!/usr/bin/env bash
# Syncs the sources to the KDE test box, builds and installs the app there, then either relaunches it in the desktop
# session (default) or renders offscreen snapshots into ./build/snapshots (--snapshot).
set -euo pipefail
# AGENT_SPEED_KDE_HOST: user@host of the box (root, since it installs into /usr/local); AGENT_SPEED_KDE_KEY: its ssh key.
host=${AGENT_SPEED_KDE_HOST:?set AGENT_SPEED_KDE_HOST to user@host of the KDE box}
key=${AGENT_SPEED_KDE_KEY:-$HOME/.ssh/id_ed25519}
ssh_cmd=(ssh -i "$key" -o BatchMode=yes "$host")
root=$(cd "$(dirname "$0")/../.." && pwd)

rsync -az --delete --exclude build -e "ssh -i $key -o BatchMode=yes" "$root/linux" "$root/spec" "$host:/root/agent-speed-src/"
"${ssh_cmd[@]}" 'set -o pipefail; cd /root/agent-speed-src/linux &&
  { cmake -S . -B build -G Ninja -DAGENTSPEED_BUILD_APP=ON -DAGENTSPEED_BUILD_TESTS=OFF -DCMAKE_INSTALL_PREFIX=/usr/local >/tmp/agent-speed-configure.log 2>&1 ||
    { tail -20 /tmp/agent-speed-configure.log; exit 1; }; } &&
  { cmake --build build >/tmp/agent-speed-build.log 2>&1 || { grep -E "error|FAILED" /tmp/agent-speed-build.log | head -30; exit 1; }; } &&
  { grep -E "warning:" /tmp/agent-speed-build.log | head -20 || true; } &&
  cmake --install build >/dev/null'

if [[ "${1:-}" == "--snapshot" ]]; then
  mkdir -p "$root/linux/build/snapshots"
  "${ssh_cmd[@]}" 'cd /tmp && for mode in light dark; do
      flag=--snapshot=/tmp/agent-speed-$mode.png; [[ $mode == dark ]] && flag=--snapshot-dark=/tmp/agent-speed-$mode.png
      QT_QPA_PLATFORM=offscreen AGENT_SPEED_LANGUAGE=${AGENT_SPEED_LANGUAGE:-zh-Hant} /usr/local/bin/agent-speed --demo "$flag" 2>&1 | grep -v "^QFont\|propagateSizeHints" || true
    done'
  scp -q -i "$key" -o BatchMode=yes "$host:/tmp/agent-speed-light.png" "$host:/tmp/agent-speed-dark.png" "$root/linux/build/snapshots/"
  echo "snapshots: $root/linux/build/snapshots"
else
  "${ssh_cmd[@]}" 'systemctl --user -M claude@ stop agent-speed-test 2>/dev/null || true
    systemd-run --user -M claude@ --unit=agent-speed-test --collect /usr/local/bin/agent-speed --demo >/dev/null
    sleep 3; systemctl --user -M claude@ is-active agent-speed-test
    journalctl --since "-10s" --no-pager -o cat | grep -E "qml|QML|Dashboard|agent-speed-test" | grep -v "^Started\|^Stopp" | tail -20 || true'
fi
