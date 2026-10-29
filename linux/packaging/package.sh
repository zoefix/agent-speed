#!/usr/bin/env bash
# Builds the Linux release artifacts from an app build: AgentSpeed-Linux-<arch>.AppImage (self-contained, updates itself,
# UPDATES 5) and agent-speed_<version>_<debarch>.deb (the same bundle under /opt/agent-speed).
#   packaging/package.sh <build dir> <output dir> <version>
# QMAKE must point at the qmake of the Qt the app was built with. linuxdeploy and its Qt plugin are fetched into
# $AGENTSPEED_TOOLS (default ~/.cache/agent-speed-packaging).
set -euo pipefail
build=$(realpath "$1")
out=$(mkdir -p "$2" && realpath "$2")
version=$3
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
arch=$(uname -m)
debarch=$([[ $arch == x86_64 ]] && echo amd64 || echo arm64)
tools=${AGENTSPEED_TOOLS:-$HOME/.cache/agent-speed-packaging}
: "${QMAKE:?set QMAKE to the qmake of the Qt the app was built with}"

mkdir -p "$tools"
fetch() {
    local name=$1 url=$2
    if [[ ! -x "$tools/$name" ]]; then
        curl -fsSL --retry 3 -o "$tools/$name.part" "$url"
        chmod +x "$tools/$name.part"
        mv "$tools/$name.part" "$tools/$name"
    fi
}
fetch "linuxdeploy-$arch.AppImage" "https://github.com/linuxdeploy/linuxdeploy/releases/download/continuous/linuxdeploy-$arch.AppImage"
fetch "linuxdeploy-plugin-qt-$arch.AppImage" "https://github.com/linuxdeploy/linuxdeploy-plugin-qt/releases/download/continuous/linuxdeploy-plugin-qt-$arch.AppImage"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
appdir="$work/AppDir"
DESTDIR="$appdir" cmake --install "$build" --prefix /usr >/dev/null

export APPIMAGE_EXTRACT_AND_RUN=1
export QML_SOURCES_PATHS="$repo/linux/app/qml"
export EXTRA_PLATFORM_PLUGINS="libqoffscreen.so"
export EXTRA_QT_MODULES="svg"
export LINUXDEPLOY_OUTPUT_VERSION="$version"
export PATH="$tools:$PATH"
ln -sf "$tools/linuxdeploy-plugin-qt-$arch.AppImage" "$tools/linuxdeploy-plugin-qt"
(
    cd "$work"
    "$tools/linuxdeploy-$arch.AppImage" --appdir "$appdir" \
        --executable "$appdir/usr/bin/agent-speed" \
        --desktop-file "$appdir/usr/share/applications/agent-speed.desktop" \
        --icon-file "$appdir/usr/share/icons/hicolor/scalable/apps/agent-speed.svg" \
        --plugin qt --output appimage >"$work/linuxdeploy.log" 2>&1 || { tail -40 "$work/linuxdeploy.log"; exit 1; }
)
appimage="$out/AgentSpeed-Linux-$arch.AppImage"
mv "$work"/Agent*Speed*.AppImage "$appimage"
chmod +x "$appimage"

# The .deb carries the same bundle, so it runs on every distribution the AppImage runs on.
deb="$work/deb"
mkdir -p "$deb/opt/agent-speed" "$deb/usr/bin" "$deb/usr/share/applications" "$deb/usr/share/icons/hicolor/scalable/apps" "$deb/DEBIAN"
cp -a "$appdir/usr/." "$deb/opt/agent-speed/"
rm -rf "$deb/opt/agent-speed/share/applications" "$deb/opt/agent-speed/share/icons"
ln -s /opt/agent-speed/bin/agent-speed "$deb/usr/bin/agent-speed"
cp "$repo/linux/app/packaging/agent-speed.desktop" "$deb/usr/share/applications/"
cp "$repo/linux/app/packaging/agent-speed.svg" "$deb/usr/share/icons/hicolor/scalable/apps/"
size=$(du -sk "$deb" | cut -f1)
cat >"$deb/DEBIAN/control" <<CONTROL
Package: agent-speed
Version: $version
Section: utils
Priority: optional
Architecture: $debarch
Installed-Size: $size
Depends: libc6 (>= 2.35), libgl1, libegl1, libfontconfig1, libfreetype6, libx11-6, libx11-xcb1, libxcb1, libxkbcommon0, libxkbcommon-x11-0, libdbus-1-3
Maintainer: zoefix <zony1937@gmail.com>
Homepage: https://github.com/zoefix/agent-speed
Description: Live token speed of your AI coding agents
 Agent Speed reads the local logs of AI coding agents and shows their live
 output speed, costs, task scores and network traffic in the tray, a floating
 orb and a usage history. Everything stays on this computer.
CONTROL
dpkg-deb --build --root-owner-group "$deb" "$out/agent-speed_${version}_${debarch}.deb" >/dev/null

(cd "$out" && sha256sum "$(basename "$appimage")" "agent-speed_${version}_${debarch}.deb" >"SHA256SUMS-Linux-$arch.txt")
echo "$out:"
ls -la "$out"
