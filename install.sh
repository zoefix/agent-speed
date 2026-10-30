#!/bin/sh
# Agent Speed installer for macOS and Linux.
#
#   curl -fsSL https://raw.githubusercontent.com/zoefix/agent-speed/main/install.sh | sh
#
# Options (pass after `sh -s --`):
#   --version vX.Y.Z   install a specific release instead of the latest
#   --dir PATH         install into PATH instead of /Applications or ~/Applications (macOS),
#                      or ~/.local/share/agent-speed (Linux, where the AppImage updates itself)
#   --no-launch        do not open the app after installing
#   --uninstall        remove Agent Speed
#   --purge            with --uninstall, also remove settings
#
# Environment:
#   AGENT_SPEED_DOWNLOAD_BASE   alternative base URL for release assets (mirrors, testing)

set -eu

REPO="zoefix/agent-speed"
APP_NAME="Agent Speed"
BUNDLE_ID="io.github.zoefix.agentspeed"
ASSET="AgentSpeed-macOS.zip"
CHECKSUMS="SHA256SUMS.txt"
MIN_MACOS_MAJOR=14
MIN_GLIBC="2.35"
LINUX_PROCESS="agent-speed"

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

    case "$(uname -s)" in
        Darwin) os="macos" ;;
        Linux) os="linux" ;;
        *) fail "$T_UNSUPPORTED" ;;
    esac

    if [ "$action" = "uninstall" ]; then
        if [ "$os" = "linux" ]; then
            uninstall_linux "$dest" "$purge"
        else
            uninstall "$dest" "$purge"
        fi
        return
    fi

    if [ "$os" = "linux" ]; then
        arch="$(linux_arch)"
        [ -n "$arch" ] || fail "$T_BAD_ARCH: $(uname -m)"
        check_glibc
        [ "$arch" = "aarch64" ] && MIN_GLIBC="2.39"
        ASSET="AgentSpeed-Linux-$arch.AppImage"
        CHECKSUMS="SHA256SUMS-Linux-$arch.txt"
        [ -n "$dest" ] || dest="$(linux_dest)"
    else
        check_macos_version
        [ -n "$dest" ] || dest="$(default_dest)"
    fi
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
        actual="$(sha256 "$tmp/$ASSET")"
        [ "$expected" = "$actual" ] && break
        [ "$attempt" -lt 3 ] || fail "$T_BAD_CHECKSUM
    $T_MIRROR_HINT"
        hint "$T_RETRYING"
        attempt=$((attempt + 1))
    done

    if [ "$os" = "linux" ]; then
        install_linux "$tmp/$ASSET" "$dest" "$launch"
        return
    fi

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

# Linux: the AppImage in a folder the user owns (its in-app updater replaces it in place), a menu entry with its
# icon, and nothing outside the home folder. Only an instance started from this very AppImage is stopped.
install_linux() {
    appimage="$1"
    dest="$2"
    launch="$3"
    target="$dest/AgentSpeed.AppImage"
    data="${XDG_DATA_HOME:-$HOME/.local/share}"

    step "$T_INSTALLING"
    chmod +x "$appimage"
    quit_linux "$target"
    mkdir -p "$dest"
    cp "$appimage" "$dest/.AgentSpeed.AppImage.part"
    mv -f "$dest/.AgentSpeed.AppImage.part" "$target"

    icon="$dest/agent-speed.svg"
    (cd "$tmp" && "$target" --appimage-extract 'usr/share/icons/hicolor/scalable/apps/agent-speed.svg' >/dev/null 2>&1) || true
    if [ -f "$tmp/squashfs-root/usr/share/icons/hicolor/scalable/apps/agent-speed.svg" ]; then
        cp "$tmp/squashfs-root/usr/share/icons/hicolor/scalable/apps/agent-speed.svg" "$icon"
    else
        icon="agent-speed"
    fi

    mkdir -p "$data/applications"
    {
        printf '[Desktop Entry]\nType=Application\nName=Agent Speed\nGenericName=AI agent speed meter\n'
        printf 'Comment=Live token speed of your AI coding agents\n'
        printf 'Exec="%s"\nIcon=%s\nTerminal=false\nCategories=Utility;Monitor;Development;\nStartupNotify=false\n' \
            "$(desktop_quote "$target")" "$icon"
    } >"$data/applications/agent-speed.desktop"
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" >/dev/null 2>&1 || true

    done_line "$T_DONE${version:+ $version} → $target"
    [ -d /opt/agent-speed ] && hint "$T_DEB_PRESENT"
    if ! command -v fusermount >/dev/null 2>&1 && ! command -v fusermount3 >/dev/null 2>&1; then
        hint "$T_NO_FUSE"
    fi
    if [ "$launch" -eq 1 ]; then
        if [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
            if command -v setsid >/dev/null 2>&1; then
                setsid "$target" >/dev/null 2>&1 </dev/null &
            else
                nohup "$target" >/dev/null 2>&1 </dev/null &
            fi
            hint "$T_LAUNCHED_LINUX"
        else
            hint "$T_NO_DISPLAY"
        fi
    fi
    hint "$T_UPDATE_HINT_LINUX"
}

uninstall_linux() {
    custom_dest="$1"
    purge="$2"
    data="${XDG_DATA_HOME:-$HOME/.local/share}"
    config="${XDG_CONFIG_HOME:-$HOME/.config}"
    title
    step "$T_UNINSTALLING"
    removed=0
    for dir in "$custom_dest" "$(linux_dest)"; do
        [ -n "$dir" ] || continue
        target="$dir/AgentSpeed.AppImage"
        [ -f "$target" ] || continue
        quit_linux "$target"
        if [ -f "$config/autostart/agent-speed.desktop" ] && grep -qF "$target" "$config/autostart/agent-speed.desktop"; then
            rm -f "$config/autostart/agent-speed.desktop"
        fi
        rm -f "$target" "$dir/agent-speed.svg" "$dir/.AgentSpeed-update.AppImage"
        rmdir "$dir" 2>/dev/null || true
        removed=1
    done
    if [ -f "$data/applications/agent-speed.desktop" ]; then
        rm -f "$data/applications/agent-speed.desktop"
        command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" >/dev/null 2>&1 || true
    fi
    if [ "$purge" -eq 1 ]; then
        rm -f "$config/zoefix/Agent Speed.conf" "$config/autostart/agent-speed.desktop"
        rm -rf "$data/zoefix/Agent Speed"
        rmdir "$config/zoefix" "$data/zoefix" 2>/dev/null || true
    fi
    if [ "$removed" -eq 1 ]; then
        done_line "$T_UNINSTALLED"
    else
        done_line "$T_NOT_INSTALLED"
    fi
    [ -d /opt/agent-speed ] && hint "$T_DEB_PRESENT"
    return 0
}

linux_dest() { echo "${XDG_DATA_HOME:-$HOME/.local/share}/agent-speed"; }

linux_arch() {
    case "$(uname -m)" in
        x86_64|amd64) echo "x86_64" ;;
        aarch64|arm64) echo "aarch64" ;;
        *) echo "" ;;
    esac
}

check_glibc() {
    found="$(getconf GNU_LIBC_VERSION 2>/dev/null | awk '{ print $2 }')"
    need="$(printf '%s' "$T_OLD_GLIBC" | sed "s/@MIN@/$MIN_GLIBC/")"
    [ -n "$found" ] || fail "$need"
    awk -v have="$found" -v need="$MIN_GLIBC" 'BEGIN {
        split(have, h, "."); split(need, n, ".")
        exit !((h[1] + 0 > n[1] + 0) || (h[1] + 0 == n[1] + 0 && h[2] + 0 >= n[2] + 0))
    }' || fail "$need ($found)"
}

# Stops only an instance started from `$1`: the AppImage runtime exports APPIMAGE with its own path.
quit_linux() {
    for pid in $(pgrep -x "$LINUX_PROCESS" 2>/dev/null); do
        if tr '\0' '\n' <"/proc/$pid/environ" 2>/dev/null | grep -qxF "APPIMAGE=$1"; then
            kill -TERM "$pid" 2>/dev/null || true
            i=0
            while kill -0 "$pid" 2>/dev/null && [ $i -lt 20 ]; do
                sleep 0.25
                i=$((i + 1))
            done
            kill -KILL "$pid" 2>/dev/null || true
        fi
    done
}

# A path for a desktop entry's Exec, inside double quotes.
desktop_quote() { printf '%s' "$1" | sed -e 's/[\\"`$]/\\&/g' -e 's/%/%%/g'; }

sha256() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{ print $1 }'
    else
        shasum -a 256 "$1" | awk '{ print $1 }'
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
    sed -n '2,17p' "$0" 2>/dev/null | sed 's/^# \{0,1\}//' || true
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
            T_UNSUPPORTED="這個腳本適用於 macOS 與 Linux，Windows 請使用 install.ps1"
            T_BAD_ARCH="不支援這個處理器架構"; T_OLD_GLIBC="Linux 版需要 glibc @MIN@ 以上（x86_64：Ubuntu 22.04、Debian 12 起；arm64：Ubuntu 24.04、Debian 13 起）"
            T_LAUNCHED_LINUX="Agent Speed 已啟動，請看系統匣（GNOME 需要 AppIndicator 擴充）。"
            T_NO_DISPLAY="沒有偵測到圖形工作階段；登入桌面後從應用程式選單開啟 Agent Speed。"
            T_UPDATE_HINT_LINUX="之後可在 App 內更新，也可以重新執行同一行指令。"
            T_DEB_PRESENT="另外裝有 .deb 版（/opt/agent-speed）；應用程式選單會開啟這次安裝的版本。不需要的話可執行 sudo apt remove agent-speed。"
            T_NO_FUSE="找不到 fusermount：若 App 無法啟動，請安裝 fuse3（或 libfuse2）。"
            T_OLD_MACOS="需要 macOS 14 或更新版本"; T_DOWNLOAD_FAILED="下載失敗"
            T_NO_CHECKSUM="找不到校驗碼"; T_BAD_CHECKSUM="校驗碼不符，已中止安裝"
            T_BAD_ARCHIVE="安裝包內容不正確"; T_BAD_SIGNATURE="App 簽章驗證失敗"; T_BAD_ARGS="參數錯誤"
            T_RETRYING="下載不完整，重新下載…"; T_MIRROR_HINT="若 GitHub 在你的網路很慢，可把 AGENT_SPEED_DOWNLOAD_BASE 設為發佈檔案的鏡像網址。" ;;
        zh*)
            T_DOWNLOADING="下载最新版本"; T_VERIFYING="验证文件完整性"; T_INSTALLING="安装"
            T_DONE="安装完成"; T_LAUNCHED="Agent Speed 已启动，请查看菜单栏。"
            T_UPDATE_HINT="之后重新运行同一行命令即可更新。"; T_UNINSTALLING="卸载 Agent Speed"
            T_UNINSTALLED="已卸载"; T_NOT_INSTALLED="没有找到已安装的 Agent Speed"
            T_UNSUPPORTED="此脚本适用于 macOS 与 Linux，Windows 请使用 install.ps1"
            T_BAD_ARCH="不支持这个处理器架构"; T_OLD_GLIBC="Linux 版需要 glibc @MIN@ 以上（x86_64：Ubuntu 22.04、Debian 12 起；arm64：Ubuntu 24.04、Debian 13 起）"
            T_LAUNCHED_LINUX="Agent Speed 已启动，请查看系统托盘（GNOME 需要 AppIndicator 扩展）。"
            T_NO_DISPLAY="没有检测到图形会话；登录桌面后从应用程序菜单打开 Agent Speed。"
            T_UPDATE_HINT_LINUX="之后可在 App 内更新，也可以重新运行同一行命令。"
            T_DEB_PRESENT="另外装有 .deb 版（/opt/agent-speed）；应用程序菜单会打开这次安装的版本。不需要的话可运行 sudo apt remove agent-speed。"
            T_NO_FUSE="找不到 fusermount：若 App 无法启动，请安装 fuse3（或 libfuse2）。"
            T_OLD_MACOS="需要 macOS 14 或更高版本"; T_DOWNLOAD_FAILED="下载失败"
            T_NO_CHECKSUM="找不到校验码"; T_BAD_CHECKSUM="校验码不匹配，已中止安装"
            T_BAD_ARCHIVE="安装包内容不正确"; T_BAD_SIGNATURE="App 签名验证失败"; T_BAD_ARGS="参数错误"
            T_RETRYING="下载不完整，重新下载…"; T_MIRROR_HINT="若 GitHub 在你的网络很慢，可将 AGENT_SPEED_DOWNLOAD_BASE 设为发布文件的镜像地址。" ;;
        ja*)
            T_DOWNLOADING="最新バージョンをダウンロード"; T_VERIFYING="ファイルの整合性を確認"; T_INSTALLING="インストール"
            T_DONE="インストール完了"; T_LAUNCHED="Agent Speed を起動しました。メニューバーを確認してください。"
            T_UPDATE_HINT="同じコマンドを再実行すると更新できます。"; T_UNINSTALLING="Agent Speed をアンインストール"
            T_UNINSTALLED="削除しました"; T_NOT_INSTALLED="インストールされた Agent Speed が見つかりません"
            T_UNSUPPORTED="このスクリプトは macOS と Linux 用です。Windows では install.ps1 を使ってください"
            T_BAD_ARCH="この CPU アーキテクチャには対応していません"; T_OLD_GLIBC="Linux 版には glibc @MIN@ 以降が必要です（x86_64：Ubuntu 22.04、Debian 12 以降、arm64：Ubuntu 24.04、Debian 13 以降）"
            T_LAUNCHED_LINUX="Agent Speed を起動しました。システムトレイを確認してください（GNOME では AppIndicator 拡張が必要です）。"
            T_NO_DISPLAY="グラフィカルセッションが見つかりません。デスクトップにログインしてアプリケーションメニューから Agent Speed を開いてください。"
            T_UPDATE_HINT_LINUX="アプリ内から更新できます。同じコマンドを再実行しても更新できます。"
            T_DEB_PRESENT=".deb 版（/opt/agent-speed）もインストールされています。アプリケーションメニューは今回インストールした版を開きます。不要なら sudo apt remove agent-speed を実行してください。"
            T_NO_FUSE="fusermount が見つかりません。アプリが起動しない場合は fuse3（または libfuse2）をインストールしてください。"
            T_OLD_MACOS="macOS 14 以降が必要です"; T_DOWNLOAD_FAILED="ダウンロードに失敗しました"
            T_NO_CHECKSUM="チェックサムが見つかりません"; T_BAD_CHECKSUM="チェックサムが一致しないため中止しました"
            T_BAD_ARCHIVE="パッケージの内容が正しくありません"; T_BAD_SIGNATURE="App の署名を検証できません"; T_BAD_ARGS="引数が正しくありません"
            T_RETRYING="ダウンロードが不完全なため再試行します…"; T_MIRROR_HINT="GitHub が遅い場合は AGENT_SPEED_DOWNLOAD_BASE にリリースファイルのミラーを設定してください。" ;;
        *)
            T_DOWNLOADING="Downloading the latest release"; T_VERIFYING="Verifying integrity"; T_INSTALLING="Installing"
            T_DONE="Installed"; T_LAUNCHED="Agent Speed is running — look at your menu bar."
            T_UPDATE_HINT="Run the same command again any time to update."; T_UNINSTALLING="Uninstalling Agent Speed"
            T_UNINSTALLED="Removed"; T_NOT_INSTALLED="Agent Speed is not installed"
            T_UNSUPPORTED="This script is for macOS and Linux. On Windows use install.ps1"
            T_BAD_ARCH="This processor architecture is not supported"; T_OLD_GLIBC="The Linux version needs glibc @MIN@ or later (x86_64: Ubuntu 22.04 or Debian 12 and newer; arm64: Ubuntu 24.04 or Debian 13 and newer)"
            T_LAUNCHED_LINUX="Agent Speed is running — look at your system tray (GNOME needs the AppIndicator extension)."
            T_NO_DISPLAY="No graphical session found; open Agent Speed from the application menu once you are signed in to the desktop."
            T_UPDATE_HINT_LINUX="Update from inside the app, or run the same command again."
            T_DEB_PRESENT="The .deb version is also installed (/opt/agent-speed); the application menu now opens this one. Remove the other with sudo apt remove agent-speed if you don't need it."
            T_NO_FUSE="fusermount was not found: if the app doesn't start, install fuse3 (or libfuse2)."
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
