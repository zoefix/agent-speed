#!/bin/sh
set -eu
ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
BUILD="$ROOT/build"
BPFTOOL=${BPFTOOL:-bpftool}
if ! "$BPFTOOL" version >/dev/null 2>&1; then
    # Ubuntu's wrapper expects tools matching the running kernel. BTF dumping and
    # skeleton generation can use any installed recent bpftool instead.
    BPFTOOL=$(find /usr/lib/linux-tools -type f -name bpftool 2>/dev/null | sort | tail -n 1)
    [ -n "$BPFTOOL" ] || { echo 'Install linux-tools-generic or set BPFTOOL.' >&2; exit 1; }
fi
mkdir -p "$BUILD"
"$BPFTOOL" btf dump file /sys/kernel/btf/vmlinux format c > "$BUILD/vmlinux.h"
# Compiler flag variables deliberately contain whitespace-separated arguments.
# shellcheck disable=SC2086
"${CLANG:-clang}" -g -O2 -target bpf -D__TARGET_ARCH_x86 -I"$BUILD" ${BPF_CFLAGS:-} -c "$ROOT/traffic.bpf.c" -o "$BUILD/traffic.bpf.o"
"$BPFTOOL" gen skeleton "$BUILD/traffic.bpf.o" > "$BUILD/traffic.skel.h"
# shellcheck disable=SC2086
"${CC:-cc}" -O2 -Wall -Wextra -Werror -I"$BUILD" ${BPF_CFLAGS:-} "$ROOT/helper.c" ${BPF_LDFLAGS:-} -lbpf -lelf -lz -o "$BUILD/agent-speed-traffic"
