#!/bin/zsh
# Builds macos/build/BSSIDLock.app. Needs only the Xcode Command Line Tools (xcode-select --install).
#
# Signing:
#   default                     ad-hoc signature (fine for running on your own Mac)
#   SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" ./macos/build.sh
#                               Developer ID + hardened runtime, ready for notarize.sh
set -euo pipefail
cd "$(dirname "$0")"

APP=build/BSSIDLock.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

ARCHS=(${=ARCHS:-arm64 x86_64})
BINS=()
for arch in $ARCHS; do
  swiftc Sources/*.swift -parse-as-library -swift-version 5 -O \
    -target "$arch-apple-macos13.0" -o "build/BSSIDLock-$arch"
  BINS+=("build/BSSIDLock-$arch")
done
lipo -create $BINS -output "$APP/Contents/MacOS/BSSIDLock"
rm -f $BINS

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

if [[ -n "${SIGN_IDENTITY:-}" ]]; then
  codesign --force --options runtime --timestamp \
    --entitlements BSSIDLock.entitlements --sign "$SIGN_IDENTITY" "$APP"
  echo "Signed with: $SIGN_IDENTITY"
else
  # Ad-hoc signature: enough to run locally and to get a Location permission entry.
  codesign --force --sign - "$APP"
fi
codesign --verify --strict "$APP"
echo "Built macos/$APP ($(lipo -archs "$APP/Contents/MacOS/BSSIDLock"))"
