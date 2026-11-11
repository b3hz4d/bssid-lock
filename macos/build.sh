#!/bin/zsh
# Builds build/BSSIDLock.app. Needs only the Xcode Command Line Tools (xcode-select --install).
set -euo pipefail
cd "$(dirname "$0")"

APP=build/BSSIDLock.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

swiftc BSSIDLock.swift -parse-as-library -swift-version 5 -O -o "$APP/Contents/MacOS/BSSIDLock"
cp Info.plist "$APP/Contents/Info.plist"

# Build the .icns from the 1024px source icon if it isn't there yet.
if [[ ! -f AppIcon.icns ]]; then
  ICONSET=$(mktemp -d)/AppIcon.iconset
  mkdir -p "$ICONSET"
  for sz in 16 32 128 256 512; do
    sips -z $sz $sz ../assets/icon-1024.png --out "$ICONSET/icon_${sz}x${sz}.png" >/dev/null
    sips -z $((sz*2)) $((sz*2)) ../assets/icon-1024.png --out "$ICONSET/icon_${sz}x${sz}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o AppIcon.icns
fi
cp AppIcon.icns "$APP/Contents/Resources/"

# Ad-hoc signature: enough to run locally and to get a Location permission entry.
codesign --force --deep --sign - "$APP"
echo "Built macos/$APP"
