#!/usr/bin/env bash
# Builds a universal, ad-hoc signed "Agent Speed.app" and a distributable zip.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
MACOS_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
REPO_ROOT="$(cd -- "$MACOS_DIR/.." && pwd -P)"

APP_NAME="Agent Speed"
EXECUTABLE="AgentSpeed"
HELPER="agent-speed-statusline"
ZIP_NAME="AgentSpeed-macOS.zip"
PLIST_TEMPLATE="$MACOS_DIR/Resources/Info.plist"
ICON_FILE="$REPO_ROOT/assets/icon/AgentSpeed.icns"
SCRATCH_PATH="$MACOS_DIR/.build/universal"
DEFAULT_VERSION="1.0.0"

usage() {
    cat <<EOF
Usage: $(basename "$0") [options]

Builds a universal (arm64 + x86_64) release of Agent Speed, assembles
"$APP_NAME.app", signs it ad hoc and packs it into $ZIP_NAME.

Options:
  --version X.Y.Z   Marketing version (default: contents of VERSION at the
                    repo root, else $DEFAULT_VERSION). A leading "v" is ignored.
  --build N         Bundle build number, CFBundleVersion (default: the version).
  --output DIR      Output directory (default: <repo>/dist).
  --binary PATH     Package an existing universal executable instead of
                    running swift build.
  --helper PATH     With --binary: the universal $HELPER executable
                    (default: beside the --binary executable).
  --relay-dir DIR   Folder with agent-speed-relay-linux-x86_64 and
                    agent-speed-relay-linux-aarch64 (default:
                    <repo>/linux/relay/dist, built by linux/relay/build-static.sh).
  -h, --help        Show this help.

Outputs:
  <output>/$APP_NAME.app
  <output>/$ZIP_NAME
EOF
}

if [[ -t 1 ]]; then BOLD=$'\033[1m' BLUE=$'\033[1;34m' RESET=$'\033[0m'; else BOLD="" BLUE="" RESET=""; fi
if [[ -t 2 ]]; then RED=$'\033[1;31m' ERESET=$'\033[0m'; else RED="" ERESET=""; fi

step() { printf '%s==>%s %s%s%s\n' "$BLUE" "$RESET" "$BOLD" "$*" "$RESET"; }
info() { printf '    %s\n' "$*"; }
die() {
    printf '%serror:%s %s\n' "$RED" "$ERESET" "$1" >&2
    [[ $# -gt 1 ]] && printf '       %s\n' "${@:2}" >&2
    exit 1
}

need_value() {
    [[ $# -ge 2 && -n "$2" && "$2" != --* ]] || die "$1 requires a value." "Run with --help for usage."
}

VERSION=""
BUILD_NUMBER=""
OUTPUT_DIR="$REPO_ROOT/dist"
PREBUILT_BINARY=""
PREBUILT_HELPER=""
RELAY_DIR="$REPO_ROOT/linux/relay/dist"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) need_value "$@"; VERSION="$2"; shift 2 ;;
        --version=*) VERSION="${1#*=}"; shift ;;
        --build) need_value "$@"; BUILD_NUMBER="$2"; shift 2 ;;
        --build=*) BUILD_NUMBER="${1#*=}"; shift ;;
        --output) need_value "$@"; OUTPUT_DIR="$2"; shift 2 ;;
        --output=*) OUTPUT_DIR="${1#*=}"; shift ;;
        --binary) need_value "$@"; PREBUILT_BINARY="$2"; shift 2 ;;
        --binary=*) PREBUILT_BINARY="${1#*=}"; shift ;;
        --helper) need_value "$@"; PREBUILT_HELPER="$2"; shift 2 ;;
        --helper=*) PREBUILT_HELPER="${1#*=}"; shift ;;
        --relay-dir) need_value "$@"; RELAY_DIR="$2"; shift 2 ;;
        --relay-dir=*) RELAY_DIR="${1#*=}"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "Unknown argument: $1" "Run with --help for usage." ;;
    esac
done

if [[ -z "$VERSION" && -f "$REPO_ROOT/VERSION" ]]; then
    VERSION="$(tr -d '[:space:]' < "$REPO_ROOT/VERSION")"
fi
VERSION="${VERSION:-$DEFAULT_VERSION}"
VERSION="${VERSION#v}"
[[ "$VERSION" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] \
    || die "Invalid version \"$VERSION\"." "Expected three numbers separated by dots, e.g. 1.2.3."

BUILD_NUMBER="${BUILD_NUMBER:-$VERSION}"
[[ "$BUILD_NUMBER" =~ ^[0-9]+(\.[0-9]+){0,2}$ ]] \
    || die "Invalid build number \"$BUILD_NUMBER\"." "Expected up to three numbers separated by dots, e.g. 42 or 1.2.3."

[[ "$(uname -s)" == "Darwin" ]] || die "This script must run on macOS."

RELAY_ARCHS=(x86_64 aarch64)
for arch in "${RELAY_ARCHS[@]}"; do
    relay="$RELAY_DIR/agent-speed-relay-linux-$arch"
    [[ -f "$relay" ]] \
        || die "Missing remote relay $relay." "Build it with linux/relay/build-static.sh $arch, or pass --relay-dir."
    [[ "$(head -c 4 "$relay" | od -An -tx1 | tr -d ' \n')" == "7f454c46" ]] \
        || die "$relay is not a Linux executable."
    LC_ALL=C grep -a -q -x -F "$VERSION" <(strings -a "$relay") \
        || die "$relay was not built from version $VERSION." "Rebuild it with linux/relay/build-static.sh $arch."
done
for tool in swift lipo vtool xcrun codesign plutil ditto xattr strings od; do
    command -v "$tool" >/dev/null 2>&1 || die "Required tool \"$tool\" was not found." "Install Xcode or the Xcode Command Line Tools."
done
[[ -f "$PLIST_TEMPLATE" ]] || die "Info.plist template not found at $PLIST_TEMPLATE."
[[ -f "$ICON_FILE" ]] || die "App icon not found at $ICON_FILE." "Generate it with: assets/icon/generate.sh"

mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd -- "$OUTPUT_DIR" && pwd -P)"
APP_DIR="$OUTPUT_DIR/$APP_NAME.app"
ZIP_PATH="$OUTPUT_DIR/$ZIP_NAME"

step "Agent Speed $VERSION (build $BUILD_NUMBER)"
info "Repository: $REPO_ROOT"
info "Output:     $OUTPUT_DIR"

if [[ -n "$PREBUILT_BINARY" ]]; then
    [[ -f "$PREBUILT_BINARY" ]] || die "Executable not found at $PREBUILT_BINARY."
    BINARY="$(cd -- "$(dirname -- "$PREBUILT_BINARY")" && pwd -P)/$(basename -- "$PREBUILT_BINARY")"
    HELPER_BINARY="${PREBUILT_HELPER:-$(dirname -- "$BINARY")/$HELPER}"
    [[ -f "$HELPER_BINARY" ]] || die "Status line helper not found at $HELPER_BINARY." "Pass it with --helper PATH."
    step "Using prebuilt executables"
    info "$BINARY"
    info "$HELPER_BINARY"
else
    SWIFT_FLAGS=(-c release --arch arm64 --arch x86_64 --package-path "$MACOS_DIR" --scratch-path "$SCRATCH_PATH")
    step "Building universal release (arm64 + x86_64)"
    swift build "${SWIFT_FLAGS[@]}" \
        || die "swift build failed." "Fix the compiler errors above and run the script again."
    BIN_DIR="$(swift build "${SWIFT_FLAGS[@]}" --show-bin-path)" \
        || die "Could not determine the build output directory."
    BINARY="$BIN_DIR/$EXECUTABLE"
    HELPER_BINARY="$BIN_DIR/$HELPER"
    [[ -f "$BINARY" ]] || die "Built executable not found at $BINARY."
    [[ -f "$HELPER_BINARY" ]] || die "Built status line helper not found at $HELPER_BINARY."
fi

step "Checking architectures"
for candidate in "$BINARY" "$HELPER_BINARY"; do
    info "$(lipo -info "$candidate")"
    ARCHS=" $(lipo -archs "$candidate" 2>/dev/null || true) "
    for arch in arm64 x86_64; do
        [[ "$ARCHS" == *" $arch "* ]] \
            || die "$(basename -- "$candidate") is not universal; it must contain both arm64 and x86_64." "Found:${ARCHS% }"
    done
done

# macOS 26+ shows the current system design (Liquid Glass) only to apps stamped with a recent SDK; an executable stamped
# with its deployment target as SDK runs in the compatibility appearance. Depending on the Xcode version, SwiftPM's
# linker step stamps the deployment target instead of the SDK it compiled against, so the stamp is set here.
step "Checking the SDK stamp"
EXPECTED_SDK="$(xcrun --sdk macosx --show-sdk-version 2>/dev/null || true)"
[[ -n "$EXPECTED_SDK" ]] || die "Could not determine the macOS SDK version." "Check the selected Xcode with: xcode-select -p"
MINOS="$(vtool -arch arm64 -show-build "$BINARY" 2>/dev/null | awk '$1 == "minos" { print $2; exit }')"
[[ -n "$MINOS" ]] || die "Could not read the minimum macOS version of the executable."
STAMP_DIR="$(mktemp -d)"
trap 'rm -rf "$STAMP_DIR"' EXIT
vtool -set-build-version macos "$MINOS" "$EXPECTED_SDK" -replace -output "$STAMP_DIR/$EXECUTABLE" "$BINARY" \
    || die "Could not stamp the SDK version into the executable."
BINARY="$STAMP_DIR/$EXECUTABLE"
for arch in arm64 x86_64; do
    STAMP="$(vtool -arch "$arch" -show-build "$BINARY" 2>/dev/null | awk '$1 == "minos" { minos = $2 } $1 == "sdk" { sdk = $2 } END { print minos " " sdk }')"
    read -r MINOS SDK <<< "$STAMP"
    [[ "${SDK%.0}" == "${EXPECTED_SDK%.0}" ]] \
        || die "The $arch slice is stamped with SDK ${SDK:-unknown}, expected $EXPECTED_SDK." "It would run in the macOS compatibility appearance."
    info "$arch: SDK $SDK, minimum macOS $MINOS"
done

step "Assembling $APP_NAME.app"
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources" "$APP_DIR/Contents/Helpers"

cp "$BINARY" "$APP_DIR/Contents/MacOS/$EXECUTABLE"
chmod 755 "$APP_DIR/Contents/MacOS/$EXECUTABLE"
# The Claude Code status line helper (QUOTAS 5.1); the app copies it to its data folder when the bridge is on.
cp "$HELPER_BINARY" "$APP_DIR/Contents/Helpers/$HELPER"
chmod 755 "$APP_DIR/Contents/Helpers/$HELPER"

sed -e "s/__VERSION__/$VERSION/g" -e "s/__BUILD__/$BUILD_NUMBER/g" \
    "$PLIST_TEMPLATE" > "$APP_DIR/Contents/Info.plist"
if grep -q '__[A-Z_]*__' "$APP_DIR/Contents/Info.plist"; then
    die "Info.plist still contains unresolved placeholders." "$(grep -o '__[A-Z_]*__' "$APP_DIR/Contents/Info.plist" | sort -u | tr '\n' ' ')"
fi
plutil -lint -s "$APP_DIR/Contents/Info.plist" || die "The generated Info.plist is invalid."

printf 'APPL????' > "$APP_DIR/Contents/PkgInfo"
cp "$ICON_FILE" "$APP_DIR/Contents/Resources/AgentSpeed.icns"
info "Contents/MacOS/$EXECUTABLE, Contents/Helpers/$HELPER, Info.plist, PkgInfo, Resources/AgentSpeed.icns"

# The local network permission prompt in the app's languages (DESIGN 14), from the macOS string table.
python3 - "$MACOS_DIR/Resources/strings.macos.json" "$APP_DIR/Contents/Resources" <<'PY' || die "Could not write InfoPlist.strings."
import json, pathlib, sys
texts = json.load(open(sys.argv[1], encoding="utf-8"))["mac.lan.usage"]
for language, text in texts.items():
    folder = pathlib.Path(sys.argv[2]) / f"{language}.lproj"
    folder.mkdir(parents=True, exist_ok=True)
    escaped = text.replace("\\", "\\\\").replace('"', '\\"')
    (folder / "InfoPlist.strings").write_text(f'"NSLocalNetworkUsageDescription" = "{escaped}";\n', encoding="utf-8")
PY
info "Resources/{en,zh-Hant,zh-Hans,ja}.lproj/InfoPlist.strings"

mkdir -p "$APP_DIR/Contents/Resources/relay"
for arch in "${RELAY_ARCHS[@]}"; do
    cp "$RELAY_DIR/agent-speed-relay-linux-$arch" "$APP_DIR/Contents/Resources/relay/"
    chmod 644 "$APP_DIR/Contents/Resources/relay/agent-speed-relay-linux-$arch"
done
info "Resources/relay/agent-speed-relay-linux-{${RELAY_ARCHS[*]// /,}}"

step "Signing (ad hoc)"
xattr -cr "$APP_DIR" 2>/dev/null || true
# Nested code first, then the bundle that seals it.
codesign --force --options runtime --timestamp=none --sign - "$APP_DIR/Contents/Helpers/$HELPER" \
    || die "codesign failed to sign Contents/Helpers/$HELPER."
codesign --force --options runtime --timestamp=none --sign - "$APP_DIR" \
    || die "codesign failed to sign $APP_DIR."
if ! VERIFY_LOG="$(codesign --verify --deep --strict --verbose=2 "$APP_DIR" 2>&1)"; then
    printf '%s\n' "$VERIFY_LOG" | sed 's/^/    /' >&2
    die "Code signature verification failed."
fi
printf '%s\n' "$VERIFY_LOG" | sed 's/^/    /'

step "Creating $ZIP_NAME"
rm -f "$ZIP_PATH"
# Extended attributes (e.g. com.apple.provenance) would land in the zip as ._ files and break the seal when unpacked.
ditto -c -k --norsrc --noextattr --noacl --keepParent "$APP_DIR" "$ZIP_PATH"

APP_SIZE="$(du -sh "$APP_DIR" | awk '{print $1}')"
ZIP_SIZE="$(du -h "$ZIP_PATH" | awk '{print $1}')"

step "Done"
info "Version: $VERSION (build $BUILD_NUMBER)"
info "App:     $APP_DIR ($APP_SIZE)"
info "Zip:     $ZIP_PATH ($ZIP_SIZE)"
info "Archs:   $(lipo -archs "$APP_DIR/Contents/MacOS/$EXECUTABLE")"
