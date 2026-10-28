#!/bin/sh
# Agent Speed for Ubuntu 24.04 / GNOME / X11 (x64).
set -eu
version=''
launch=1
action=install
purge=0
while [ "$#" -gt 0 ]; do
    case "$1" in
        --version) [ "$#" -ge 2 ] || exit 2; version=$2; shift 2;;
        --no-launch) launch=0; shift;;
        --uninstall) action=uninstall; shift;;
        --purge) purge=1; shift;;
        -h|--help) echo 'Usage: install-linux.sh [--version vX.Y.Z] [--no-launch] [--uninstall [--purge]]'; exit 0;;
        *) echo "Unknown option: $1" >&2; exit 2;;
    esac
done
if [ "$(uname -s)" != Linux ] || [ "$(uname -m)" != x86_64 ]; then echo 'Linux x64 is required.' >&2; exit 1; fi
command -v apt-get >/dev/null 2>&1 || { echo 'Install the .deb on Ubuntu 24.04.' >&2; exit 1; }
as_root() { if [ "$(id -u)" = 0 ]; then "$@"; else sudo "$@"; fi; }
resolve_xdg_home() {
    xdg_value=$1
    fallback=$2
    case "$xdg_value" in
        /*)
            while [ "$xdg_value" != / ] && [ "${xdg_value%/}" != "$xdg_value" ]; do xdg_value=${xdg_value%/}; done
            printf '%s\n' "$xdg_value"
            ;;
        *)
            home=${HOME:-}
            case "$home" in
                /*) ;;
                *) echo 'HOME must be an absolute path to uninstall Agent Speed data.' >&2; return 1;;
            esac
            while [ "$home" != / ] && [ "${home%/}" != "$home" ]; do home=${home%/}; done
            case "$home" in
                /) printf '/%s\n' "$fallback";;
                *) printf '%s/%s\n' "$home" "$fallback";;
            esac
            ;;
    esac
}
if [ "$action" = uninstall ]; then
    config_home=$(resolve_xdg_home "${XDG_CONFIG_HOME:-}" .config)
    data_home=$(resolve_xdg_home "${XDG_DATA_HOME:-}" .local/share)
    cache_home=$(resolve_xdg_home "${XDG_CACHE_HOME:-}" .cache)
    if command -v agent-speed >/dev/null 2>&1; then agent-speed --quit || true; fi
    as_root apt-get remove -y agent-speed
    rm -f "${config_home%/}/autostart/agent-speed.desktop"
    if [ "$purge" = 1 ]; then
        rm -rf -- "${config_home%/}/agent-speed" "${data_home%/}/agent-speed" "${cache_home%/}/agent-speed"
    fi
    exit 0
fi
case "$version" in ''|v*) ;; *) version="v$version";; esac
case "$version" in *[!0-9A-Za-z._-]*) echo 'Invalid release version' >&2; exit 2;; esac
base=${AGENT_SPEED_DOWNLOAD_BASE:-}
if [ -z "$base" ]; then
    if [ -n "$version" ]; then base="https://github.com/zoefix/agent-speed/releases/download/$version";
    else base='https://github.com/zoefix/agent-speed/releases/latest/download'; fi
fi
tmp=$(mktemp -d /tmp/agent-speed-install.XXXXXX)
trap 'rm -rf -- "$tmp"' EXIT HUP INT TERM
asset=AgentSpeed-Linux-x64.deb
curl -fL --retry 2 "$base/SHA256SUMS.txt" -o "$tmp/SHA256SUMS.txt"
curl -fL --retry 2 "$base/$asset" -o "$tmp/$asset"
expected=$(awk -v name="$asset" '$2==name || $2=="*"name {print $1}' "$tmp/SHA256SUMS.txt")
[ "${#expected}" = 64 ] || { echo 'Missing or ambiguous checksum' >&2; exit 1; }
actual=$(sha256sum "$tmp/$asset" | cut -d ' ' -f 1)
[ "$expected" = "$actual" ] || { echo 'SHA-256 verification failed' >&2; exit 1; }
chmod 755 "$tmp"; chmod 644 "$tmp/$asset"
as_root apt-get install -y "$tmp/$asset"
if [ "$launch" = 1 ] && [ "$(id -u)" != 0 ]; then
    nohup agent-speed >/dev/null 2>&1 </dev/null &
fi
echo 'Installed Agent Speed. Enable the Agent Speed extension in GNOME Extensions to show top-bar numbers.'
