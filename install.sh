#!/usr/bin/env bash
# BSSIDLock installer for macOS. No Apple Developer certificate, no Gatekeeper warning.
#
#   curl -fsSL https://raw.githubusercontent.com/b3hz4d/bssid-lock/main/install.sh | bash
#
# Options (pass after "bash -s --"):
#   --version=1.2.0  install that version instead of the latest one
#   --source       build from source on this Mac instead of downloading the release
#   --uninstall    remove BSSIDLock and its settings
#   --dir=PATH     install somewhere other than /Applications
#
# Why there's no warning: Gatekeeper only checks files that a browser marked as "downloaded from the
# internet". Files fetched by curl, or built locally, don't carry that mark. This script is short;
# read it before running it.
set -euo pipefail

REPO="b3hz4d/bssid-lock"
BUNDLE_ID="io.github.b3hz4d.bssidlock"
BASE_URL="${BSSIDLOCK_BASE_URL:-https://github.com/$REPO/releases/latest/download}"
SOURCE_URL="${BSSIDLOCK_SOURCE_URL:-https://github.com/$REPO/archive/refs/heads/main.tar.gz}"
MODE=download
DEST=""
VERSION=""

say() { printf '\033[1m==>\033[0m %s\n' "$*"; }
die() { printf '\033[31mError:\033[0m %s\n' "$*" >&2; exit 1; }

for arg in "$@"; do
  case "$arg" in
    --source) MODE=source ;;
    --uninstall) MODE=uninstall ;;
    --dir=*) DEST="${arg#--dir=}" ;;
    --version=*) VERSION="${arg#--version=}"; VERSION="${VERSION#v}" ;;
    -h|--help) sed -n '2,13p' "$0" 2>/dev/null || true; exit 0 ;;
    *) die "Unknown option: $arg" ;;
  esac
done

if [[ -n "$VERSION" ]]; then
  [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "Version should look like 1.2.0 (got \"$VERSION\")."
  BASE_URL="${BSSIDLOCK_BASE_URL:-https://github.com/$REPO/releases/download/v$VERSION}"
  SOURCE_URL="${BSSIDLOCK_SOURCE_URL:-https://github.com/$REPO/archive/refs/tags/v$VERSION.tar.gz}"
fi

[[ "$(uname -s)" == "Darwin" ]] || die "This installer is for macOS. On Windows, use install.ps1 (see the README)."
major="$(sw_vers -productVersion | cut -d. -f1)"
(( major >= 13 )) || die "BSSIDLock needs macOS 13 Ventura or later (this Mac has $(sw_vers -productVersion))."

if [[ -z "$DEST" ]]; then
  if [[ -w /Applications ]]; then DEST="/Applications"; else DEST="$HOME/Applications"; fi
fi
TARGET="$DEST/BSSIDLock.app"

quit_running() {
  # Only quit the copy we're about to replace.
  pkill -f "$TARGET/Contents/MacOS/BSSIDLock" 2>/dev/null && sleep 1 || true
}

if [[ "$MODE" == "uninstall" ]]; then
  quit_running
  rm -rf "$TARGET"
  defaults delete "$BUNDLE_ID" 2>/dev/null || true
  while security delete-generic-password -s "$BUNDLE_ID.wifi" >/dev/null 2>&1; do :; done
  tccutil reset Location "$BUNDLE_ID" >/dev/null 2>&1 || true
  say "BSSIDLock has been removed."
  exit 0
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

if [[ "$MODE" == "download" ]]; then
  say "Downloading BSSIDLock ${VERSION:-(latest release)}…"
  curl -fsSL "$BASE_URL/BSSIDLock-macOS.zip" -o "$WORK/BSSIDLock-macOS.zip" \
    || die "Download failed.${VERSION:+ Is v$VERSION a published release? See https://github.com/$REPO/releases}"
  curl -fsSL "$BASE_URL/SHA256SUMS.txt" -o "$WORK/SHA256SUMS.txt" || die "Couldn't download the checksum file."

  expected="$(awk '$2 == "BSSIDLock-macOS.zip" { print $1 }' "$WORK/SHA256SUMS.txt")"
  actual="$(shasum -a 256 "$WORK/BSSIDLock-macOS.zip" | awk '{ print $1 }')"
  [[ -n "$expected" && "$expected" == "$actual" ]] || die "Checksum mismatch; the download may be corrupted. Nothing was installed."
  say "Checksum verified ($actual)"

  ditto -x -k "$WORK/BSSIDLock-macOS.zip" "$WORK/unzipped"
  APP_SRC="$WORK/unzipped/BSSIDLock.app"
else
  if ! xcode-select -p >/dev/null 2>&1; then
    xcode-select --install 2>/dev/null || true
    die "Building needs Apple's Command Line Tools. Finish the install dialog that just opened, then run this again."
  fi
  say "Downloading the source…"
  mkdir -p "$WORK/src"
  curl -fsSL "$SOURCE_URL" | tar -xz -C "$WORK/src" --strip-components 1
  say "Building (takes about a minute)…"
  ARCHS="$(uname -m)" "$WORK/src/macos/build.sh" >/dev/null 2>&1 || die "The build failed. Please open an issue at https://github.com/$REPO/issues"
  APP_SRC="$WORK/src/macos/build/BSSIDLock.app"
fi

[[ -d "$APP_SRC" ]] || die "Something went wrong: the app wasn't found after download/build."
codesign --verify --strict "$APP_SRC" || die "The app's code signature is invalid. Nothing was installed."

quit_running
mkdir -p "$DEST"
rm -rf "$TARGET"
ditto "$APP_SRC" "$TARGET"
say "Installed $TARGET"

if [[ -z "${BSSIDLOCK_NO_OPEN:-}" ]]; then
  open "$TARGET"
  say "BSSIDLock is starting. Click “Allow” when macOS asks for Location access."
fi
