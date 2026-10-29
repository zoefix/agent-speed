#!/usr/bin/env bash
# Builds and tests the Linux core inside the development container: scripts/dev.sh [ctest args...]
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
repo="$(cd "$here/.." && pwd)"
image=agentspeed-linux-dev
docker build -q -t "$image" "$here/docker" >/dev/null
docker run --rm -v "$repo:/src" -w /src/linux "$image" bash -c "
  cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Debug >/dev/null &&
  cmake --build build &&
  ctest --test-dir build --output-on-failure $*"
