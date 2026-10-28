#!/bin/sh
set -eu
umask 022
ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
VERSION=${AGENT_SPEED_VERSION:-$(cat "$ROOT/VERSION")}
VERSION=${VERSION#v}
case "$VERSION" in ''|*[!0-9A-Za-z.+-]*) echo 'Invalid version' >&2; exit 1;; esac
DOTNET=${DOTNET:-dotnet}
DIST="$ROOT/dist"
mkdir -p "$DIST" "$ROOT/build"
STAGE=$(mktemp -d "$ROOT/build/linux-deb.XXXXXX")
chmod 755 "$STAGE"
trap 'rm -rf -- "$STAGE"' EXIT HUP INT TERM
sh "$ROOT/linux/traffic-helper/build.sh"
"$DOTNET" publish "$ROOT/linux/src/AgentSpeed.Linux" -c Release -r linux-x64 --self-contained true \
    -p:Version="$VERSION" -p:PublishTrimmed=false -o "$STAGE/usr/lib/agent-speed/app"
install -Dm755 "$ROOT/linux/traffic-helper/build/agent-speed-traffic" "$STAGE/usr/lib/agent-speed/agent-speed-traffic"
install -Dm755 "$ROOT/linux/packaging/install-traffic-helper" "$STAGE/usr/lib/agent-speed/install-traffic-helper"
install -Dm644 "$ROOT/linux/packaging/agent-speed-traffic.service" "$STAGE/usr/lib/systemd/system/agent-speed-traffic.service"
install -Dm644 "$ROOT/linux/packaging/agent-speed-traffic.socket" "$STAGE/usr/lib/systemd/system/agent-speed-traffic.socket"
install -Dm644 "$ROOT/linux/packaging/agent-speed.desktop" "$STAGE/usr/share/applications/agent-speed.desktop"
install -Dm644 "$ROOT/assets/icon/icon-1024.png" "$STAGE/usr/share/icons/hicolor/1024x1024/apps/agent-speed.png"
install -Dm644 "$ROOT/LICENSE" "$STAGE/usr/share/doc/agent-speed/copyright"
EXT="$STAGE/usr/share/gnome-shell/extensions/agent-speed@zoefix.github.io"
mkdir -p "$EXT" "$STAGE/usr/bin" "$STAGE/DEBIAN"
cp "$ROOT/linux/gnome-extension/extension.js" "$ROOT/linux/gnome-extension/metadata.json" "$ROOT/linux/gnome-extension/stylesheet.css" "$EXT/"
ln -sfn ../lib/agent-speed/app/agent-speed "$STAGE/usr/bin/agent-speed"
cat > "$STAGE/DEBIAN/control" <<EOF
Package: agent-speed
Version: $VERSION
Architecture: amd64
Maintainer: Agent Speed contributors <noreply@github.com>
Section: utils
Priority: optional
Depends: libc6 (>= 2.38), libgcc-s1, libstdc++6, libicu74, libssl3t64, libsqlite3-0, libx11-6, libxext6, libice6, libsm6, libfontconfig1, libfreetype6, libgl1, libbpf1 (>= 1.3), libelf1t64, zlib1g
Recommends: fonts-noto-cjk, policykit-1
Description: Real-time AI agent token speed and usage dashboard
 Native Linux desktop dashboard with floating instruments, history, costs,
 GNOME top bar integration and optional per-process network accounting.
EOF
cat > "$STAGE/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if [ -d /run/systemd/system ]; then
    systemctl daemon-reload
    # Apply helper fixes on upgrade while preserving the user's opt-in state.
    if systemctl -q is-active agent-speed-traffic.service; then systemctl try-restart agent-speed-traffic.service; fi
fi
# Network accounting stays disabled until the user opts in.
if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database -q || true; fi
EOF
cat > "$STAGE/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = remove ] && [ -d /run/systemd/system ]; then
    systemctl disable --now agent-speed-traffic.socket || true
    systemctl stop agent-speed-traffic.service || true
fi
EOF
chmod 755 "$STAGE/DEBIAN/postinst" "$STAGE/DEBIAN/prerm"
dpkg-deb --root-owner-group --build "$STAGE" "$DIST/AgentSpeed-Linux-x64.deb"
(cd "$DIST" && sha256sum AgentSpeed-Linux-x64.deb > AgentSpeed-Linux-SHA256SUMS.txt)
printf 'Built %s\n' "$DIST/AgentSpeed-Linux-x64.deb"
