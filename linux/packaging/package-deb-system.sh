#!/usr/bin/env bash
# A .deb that uses the distribution's Qt 6 (Debian 13, Ubuntu 25.04 and later) instead of a bundled one: smaller, and
# built with nothing but dpkg-deb. The release workflow publishes the bundled .deb from package.sh.
#   packaging/package-deb-system.sh <build dir> <output dir> <version>
set -euo pipefail
build=$(realpath "$1")
out=$(mkdir -p "$2" && realpath "$2")
version=$3
arch=$(dpkg --print-architecture)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
root="$work/root"
DESTDIR="$root" cmake --install "$build" --prefix /usr >/dev/null
mkdir -p "$root/DEBIAN"
size=$(du -sk "$root" | cut -f1)
cat >"$root/DEBIAN/control" <<CONTROL
Package: agent-speed
Version: $version
Section: utils
Priority: optional
Architecture: $arch
Installed-Size: $size
Depends: libqt6core6t64 (>= 6.5) | libqt6core6 (>= 6.5), libqt6gui6t64 | libqt6gui6, libqt6network6t64 | libqt6network6, libqt6websockets6t64 | libqt6websockets6, libqt6opengl6t64 | libqt6opengl6, libqt6widgets6t64 | libqt6widgets6, libqt6qml6, libqt6quick6, qml6-module-qtquick, qml6-module-qtquick-controls, qml6-module-qtquick-layouts, qml6-module-qtquick-window, qml6-module-qtquick-effects, qml6-module-qtcore, qml6-module-qtqml-workerscript, qt6-qpa-plugins, libzstd1, libsqlite3-0, libssl3t64 | libssl3, libxcb1, libxcb-shape0
Maintainer: zoefix <zony1937@gmail.com>
Homepage: https://github.com/zoefix/agent-speed
Description: Live token speed of your AI coding agents
 Agent Speed reads the local logs of AI coding agents and shows their live
 output speed, costs, task scores and network traffic in the tray, a floating
 orb and a usage history. Everything stays on this computer.
CONTROL
# ICU: the distribution's current libicu, whatever its soname.
icu=$(dpkg-query -W -f='${Package} ${Status}\n' 'libicu[0-9]*' 2>/dev/null | awk '/installed$/{print $1; exit}' || true)
if [[ -n "$icu" ]]; then sed -i "s/^Depends: /Depends: $icu, /" "$root/DEBIAN/control"; fi
dpkg-deb --build --root-owner-group "$root" "$out/agent-speed_${version}_${arch}.deb" >/dev/null
echo "$out/agent-speed_${version}_${arch}.deb"
