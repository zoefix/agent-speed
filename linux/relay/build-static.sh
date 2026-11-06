#!/usr/bin/env bash
# Builds the fully static relay (REMOTE 3.1) in an Alpine 3.20 container: relay/build-static.sh <x86_64|aarch64>
# Writes relay/dist/agent-speed-relay-linux-<arch>, stripped. The sources go in and the executable comes out as tar
# streams, so no folder needs to be shared with the Docker host.
set -euo pipefail
arch="${1:-}"
case "$arch" in
    x86_64) platform=linux/amd64 base=amd64/alpine:3.20 ;;
    aarch64) platform=linux/arm64 base=arm64v8/alpine:3.20 ;;
    *) echo "usage: $0 <x86_64|aarch64>" >&2; exit 2 ;;
esac
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
image="agentspeed-relay-build:$arch"
output="agent-speed-relay-linux-$arch"
docker build -q --platform "$platform" --build-arg "BASE=$base" -t "$image" "$here/docker" >/dev/null
mkdir -p "$here/dist"
# macOS tar would add AppleDouble files for extended attributes.
tar_flags=()
if tar --version 2>/dev/null | grep -q bsdtar; then tar_flags=(--no-xattrs --no-mac-metadata); fi
COPYFILE_DISABLE=1 tar ${tar_flags[@]+"${tar_flags[@]}"} -C "$repo" -cf - VERSION spec/assets linux/CMakeLists.txt linux/cmake linux/core linux/relay/CMakeLists.txt linux/relay/src |
    docker run -i --rm --platform "$platform" "$image" sh -euc "
        mkdir -p /src /dist && tar -xf - -C /src
        {
            cmake -S /src/linux -B /tmp/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DAGENTSPEED_BUILD_TESTS=OFF \
                  -DAGENTSPEED_BUILD_APP=OFF -DAGENTSPEED_BUILD_RELAY=ON -DAGENTSPEED_STATIC=ON >/dev/null
            cmake --build /tmp/build --target agent-speed-relay
            strip -o /dist/$output /tmp/build/relay/agent-speed-relay
            chmod 755 /dist/$output
            test \"\$(uname -m)\" = $arch
            if ldd /dist/$output >/dev/null 2>&1; then echo 'not a static executable' >&2; exit 1; fi
            /dist/$output --version
        } >&2
        tar -cf - -C /dist $output
    " | tar -xf - -C "$here/dist"
echo "$here/dist/$output"
