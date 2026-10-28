#!/bin/sh
# Agent Speed installer for macOS.
#
#   curl -fsSL https://raw.githubusercontent.com/zoefix/agent-speed/main/install.sh | sh
#
# Options (pass after `sh -s --`):
#   --version vX.Y.Z   install a specific release instead of the latest
#   --dir PATH         install into PATH instead of /Applications (or ~/Applications)
#   --no-launch        do not open the app after installing
#   --uninstall        remove Agent Speed
#   --purge            with --uninstall, also remove settings
#
# Environment:
#   AGENT_SPEED_DOWNLOAD_BASE   alternative base URL for release assets (mirrors, testing)

set -eu

# Linux uses the Debian installer; keep the established macOS entry point.
if [ "$(uname -s)" = Linux ]; then
    case "$0" in
        install.sh|*/install.sh)
            linux_installer="$(dirname -- "$0")/install-linux.sh"
            if [ -f "$linux_installer" ]; then exec sh "$linux_installer" "$@"; fi
            ;;
    esac
    linux_installer=$(mktemp /tmp/agent-speed-linux-installer.XXXXXX)
    trap 'rm -f -- "$linux_installer"' EXIT HUP INT TERM
    curl -fsSL https://raw.githubusercontent.com/zoefix/agent-speed/main/install-linux.sh -o "$linux_installer"
    sh "$linux_installer" "$@"
    exit $?
fi

REPO="zoefix/agent-speed"
APP_NAME="Agent Speed"
BUNDLE_ID="io.github.zoefix.agentspeed"
ASSET="AgentSpeed-macOS.zip"
CHECKSUMS="SHA256SUMS.txt"
MIN_MACOS_MAJOR=14

main() {
    setup_text
    version=""
    dest=""
    launch=1
    action="install"
    purge=0

    while [ $# -gt 0 ]; do
        case "$1" in
            --version) [ $# -ge 2 ] || fail "$T_BAD_ARGS"; version="$2"; shift 2 ;;
            --dir) [ $# -ge 2 ] || fail "$T_BAD_ARGS"; dest="$2"; shift 2 ;;
            --no-launch) launch=0; shift ;;
            --uninstall) action="uninstall"; shift ;;
            --purge) purge=1; shift ;;
            -h|--help) usage; exit 0 ;;
            *) fail "$T_BAD_ARGS: $1" ;;
        esac
    done

    [ "$(uname -s)" = "Darwin" ] || fail "$T_NOT_MAC"

    if [ "$action" = "uninstall" ]; then
        uninstall "$dest" "$purge"
        return
    fi

    check_macos_version
    [ -n "$dest" ] || dest="$(default_dest)"
    case "$version" in
        ""|v*) ;;
        *) version="v$version" ;;
    esac
    case "$version" in
        *[!A-Za-z0-9._-]*) fail "$T_BAD_ARGS: --version" ;;
    esac

    base="${AGENT_SPEED_DOWNLOAD_BASE:-}"
    if [ -z "$base" ]; then
        if [ -n "$version" ]; then
            base="https://github.com/$REPO/releases/download/$version"
        else
            base="https://github.com/$REPO/releases/latest/download"
        fi
    fi

    tmp="$(mktemp -d "${TMPDIR:-/tmp}/agent-speed.XXXXXX")"
    trap 'rm -rf "$tmp"' EXIT INT TERM

    title
    step "$T_DOWNLOADING"
    download "$base/$CHECKSUMS" "$tmp/$CHECKSUMS"
    expected="$(awk -v f="$ASSET" '$2 == f || $2 == "*"f { print $1; exit }' "$tmp/$CHECKSUMS")"
    [ -n "$expected" ] || fail "$T_NO_CHECKSUM"

    attempt=1
    while :; do
        download "$base/$ASSET" "$tmp/$ASSET" progress
        [ "$attempt" -eq 1 ] && step "$T_VERIFYING"
        actual="$(shasum -a 256 "$tmp/$ASSET" | awk '{ print $1 }')"
        [ "$expected" = "$actual" ] && break
        [ "$attempt" -lt 3 ] || fail "$T_BAD_CHECKSUM
    $T_MIRROR_HINT"
        hint "$T_RETRYING"
        attempt=$((attempt + 1))
    done

    mkdir -p "$tmp/unpacked"
    ditto -x -k "$tmp/$ASSET" "$tmp/unpacked"
    app_src="$tmp/unpacked/$APP_NAME.app"
    [ -d "$app_src" ] || fail "$T_BAD_ARCHIVE"
    codesign --verify --deep --strict "$app_src" >/dev/null 2>&1 || fail "$T_BAD_SIGNATURE"

    step "$T_INSTALLING"
    quit_running
    mkdir -p "$dest"
    target="$dest/$APP_NAME.app"
    if [ -e "$target" ]; then
        rm -rf "$target"
    fi
    ditto "$app_src" "$target"
    xattr -dr com.apple.quarantine "$target" 2>/dev/null || true

    installed_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$target/Contents/Info.plist" 2>/dev/null || echo "?")"
    done_line "$T_DONE $installed_version → $target"

    if [ "$launch" -eq 1 ]; then
        open "$target"
        hint "$T_LAUNCHED"
    fi
    hint "$T_UPDATE_HINT"
}

uninstall() {
    custom_dest="$1"
    purge="$2"
    title
    step "$T_UNINSTALLING"
    quit_running
    removed=0
    for dir in "$custom_dest" "/Applications" "$HOME/Applications"; do
        [ -n "$dir" ] || continue
        if [ -d "$dir/$APP_NAME.app" ]; then
            rm -rf "$dir/$APP_NAME.app"
            removed=1
        fi
    done
    if [ "$purge" -eq 1 ]; then
        defaults delete "$BUNDLE_ID" >/dev/null 2>&1 || true
        rm -f "$HOME/Library/Preferences/$BUNDLE_ID.plist"
        rm -rf "$HOME/Library/Application Support/AgentSpeed"
    fi
    if [ "$removed" -eq 1 ]; then
        done_line "$T_UNINSTALLED"
    else
        done_line "$T_NOT_INSTALLED"
    fi
}

default_dest() {
    if [ -w "/Applications" ]; then
        echo "/Applications"
    else
        echo "$HOME/Applications"
    fi
}

check_macos_version() {
    major="$(sw_vers -productVersion | cut -d. -f1)"
    case "$major" in
        ''|*[!0-9]*) return ;;
    esac
    [ "$major" -ge "$MIN_MACOS_MAJOR" ] || fail "$T_OLD_MACOS"
}

download() {
    meter="--silent"
    if [ "${3:-}" = "progress" ] && [ -t 2 ]; then
        meter="--progress-bar"
    fi
    case "$1" in
        https://*) set -- "$1" "$2" --proto '=https' --tlsv1.2 ;;
        *) set -- "$1" "$2" ;;
    esac
    url="$1"
    out="$2"
    shift 2
    # shellcheck disable=SC2086
    curl -fL $meter --show-error --retry 3 --retry-delay 2 --connect-timeout 15 \
        --speed-limit 1024 --speed-time 60 "$@" -o "$out" "$url" || fail "$T_DOWNLOAD_FAILED: $url
    $T_MIRROR_HINT"
}

quit_running() {
    if pgrep -x AgentSpeed >/dev/null 2>&1; then
        osascript -e "tell application id \"$BUNDLE_ID\" to quit" >/dev/null 2>&1 || true
        i=0
        while pgrep -x AgentSpeed >/dev/null 2>&1 && [ $i -lt 20 ]; do
            sleep 0.25
            i=$((i + 1))
        done
        pkill -x AgentSpeed >/dev/null 2>&1 || true
    fi
}

usage() {
    sed -n '2,16p' "$0" 2>/dev/null | sed 's/^# \{0,1\}//' || true
}

setup_text() {
    if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
        BOLD="$(printf '\033[1m')"; DIM="$(printf '\033[2m')"; GREEN="$(printf '\033[32m')"
        RED="$(printf '\033[31m')"; CYAN="$(printf '\033[36m')"; RESET="$(printf '\033[0m')"
    else
        BOLD=""; DIM=""; GREEN=""; RED=""; CYAN=""; RESET=""
    fi

    lang="${LC_ALL:-${LC_MESSAGES:-${LANG:-}}}"
    if [ -z "$lang" ]; then
        lang="$(defaults read -g AppleLocale 2>/dev/null || echo en)"
    fi
    case "$lang" in
        zh_TW*|zh_HK*|zh_MO*|zh-Hant*|zh_Hant*)
            T_DOWNLOADING="下載最新版本"; T_VERIFYING="驗證檔案完整性"; T_INSTALLING="安裝"
            T_DONE="安裝完成"; T_LAUNCHED="Agent Speed 已啟動，請看選單列。"
            T_UPDATE_HINT="之後重新執行同一行指令即可更新。"; T_UNINSTALLING="移除 Agent Speed"
            T_UNINSTALLED="已移除"; T_NOT_INSTALLED="沒有找到已安裝的 Agent Speed"
            T_NOT_MAC="這個腳本只適用於 macOS，Windows 請使用 install.ps1"
            T_OLD_MACOS="需要 macOS 14 或更新版本"; T_DOWNLOAD_FAILED="下載失敗"
            T_NO_CHECKSUM="找不到校驗碼"; T_BAD_CHECKSUM="校驗碼不符，已中止安裝"
            T_BAD_ARCHIVE="安裝包內容不正確"; T_BAD_SIGNATURE="App 簽章驗證失敗"; T_BAD_ARGS="參數錯誤"
            T_RETRYING="下載不完整，重新下載…"; T_MIRROR_HINT="若 GitHub 在你的網路很慢，可把 AGENT_SPEED_DOWNLOAD_BASE 設為發佈檔案的鏡像網址。" ;;
        zh*)
            T_DOWNLOADING="下载最新版本"; T_VERIFYING="验证文件完整性"; T_INSTALLING="安装"
            T_DONE="安装完成"; T_LAUNCHED="Agent Speed 已启动，请查看菜单栏。"
            T_UPDATE_HINT="之后重新运行同一行命令即可更新。"; T_UNINSTALLING="卸载 Agent Speed"
            T_UNINSTALLED="已卸载"; T_NOT_INSTALLED="没有找到已安装的 Agent Speed"
            T_NOT_MAC="此脚本仅适用于 macOS，Windows 请使用 install.ps1"
            T_OLD_MACOS="需要 macOS 14 或更高版本"; T_DOWNLOAD_FAILED="下载失败"
            T_NO_CHECKSUM="找不到校验码"; T_BAD_CHECKSUM="校验码不匹配，已中止安装"
            T_BAD_ARCHIVE="安装包内容不正确"; T_BAD_SIGNATURE="App 签名验证失败"; T_BAD_ARGS="参数错误"
            T_RETRYING="下载不完整，重新下载…"; T_MIRROR_HINT="若 GitHub 在你的网络很慢，可将 AGENT_SPEED_DOWNLOAD_BASE 设为发布文件的镜像地址。" ;;
        ja*)
            T_DOWNLOADING="最新バージョンをダウンロード"; T_VERIFYING="ファイルの整合性を確認"; T_INSTALLING="インストール"
            T_DONE="インストール完了"; T_LAUNCHED="Agent Speed を起動しました。メニューバーを確認してください。"
            T_UPDATE_HINT="同じコマンドを再実行すると更新できます。"; T_UNINSTALLING="Agent Speed をアンインストール"
            T_UNINSTALLED="削除しました"; T_NOT_INSTALLED="インストールされた Agent Speed が見つかりません"
            T_NOT_MAC="このスクリプトは macOS 専用です。Windows では install.ps1 を使ってください"
            T_OLD_MACOS="macOS 14 以降が必要です"; T_DOWNLOAD_FAILED="ダウンロードに失敗しました"
            T_NO_CHECKSUM="チェックサムが見つかりません"; T_BAD_CHECKSUM="チェックサムが一致しないため中止しました"
            T_BAD_ARCHIVE="パッケージの内容が正しくありません"; T_BAD_SIGNATURE="App の署名を検証できません"; T_BAD_ARGS="引数が正しくありません"
            T_RETRYING="ダウンロードが不完全なため再試行します…"; T_MIRROR_HINT="GitHub が遅い場合は AGENT_SPEED_DOWNLOAD_BASE にリリースファイルのミラーを設定してください。" ;;
        *)
            T_DOWNLOADING="Downloading the latest release"; T_VERIFYING="Verifying integrity"; T_INSTALLING="Installing"
            T_DONE="Installed"; T_LAUNCHED="Agent Speed is running — look at your menu bar."
            T_UPDATE_HINT="Run the same command again any time to update."; T_UNINSTALLING="Uninstalling Agent Speed"
            T_UNINSTALLED="Removed"; T_NOT_INSTALLED="Agent Speed is not installed"
            T_NOT_MAC="This script is for macOS. On Windows use install.ps1"
            T_OLD_MACOS="macOS 14 or later is required"; T_DOWNLOAD_FAILED="Download failed"
            T_NO_CHECKSUM="Checksum not found"; T_BAD_CHECKSUM="Checksum mismatch, installation aborted"
            T_BAD_ARCHIVE="Unexpected package contents"; T_BAD_SIGNATURE="App signature verification failed"; T_BAD_ARGS="Invalid arguments"
            T_RETRYING="The download was incomplete, trying again…"; T_MIRROR_HINT="If GitHub is slow on your network, set AGENT_SPEED_DOWNLOAD_BASE to a mirror of the release files." ;;
    esac
}

title() { printf '\n%s%s◉ Agent Speed%s\n\n' "$BOLD" "$CYAN" "$RESET"; }
step() { printf '  %s›%s %s\n' "$CYAN" "$RESET" "$1"; }
done_line() { printf '\n  %s✓%s %s%s%s\n' "$GREEN" "$RESET" "$BOLD" "$1" "$RESET"; }
hint() { printf '    %s%s%s\n' "$DIM" "$1" "$RESET"; }
fail() { printf '\n  %s✗ %s%s\n\n' "$RED" "$1" "$RESET" >&2; exit 1; }

main "$@"
