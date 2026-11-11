#!/bin/zsh
# Notarizes and staples macos/build/BSSIDLock.app (build it first with SIGN_IDENTITY set),
# then writes macos/build/BSSIDLock-macOS.zip.
#
# Needs: APPLE_ID, APPLE_TEAM_ID, APPLE_APP_PASSWORD (an app-specific password from account.apple.com)
set -euo pipefail
cd "$(dirname "$0")/build"

: "${APPLE_ID:?set APPLE_ID}" "${APPLE_TEAM_ID:?set APPLE_TEAM_ID}" "${APPLE_APP_PASSWORD:?set APPLE_APP_PASSWORD}"

rm -f notarize.zip
ditto -c -k --keepParent BSSIDLock.app notarize.zip
xcrun notarytool submit notarize.zip --wait \
  --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD"
xcrun stapler staple BSSIDLock.app
rm -f notarize.zip

spctl --assess --type execute --verbose BSSIDLock.app
rm -f BSSIDLock-macOS.zip
ditto -c -k --keepParent BSSIDLock.app BSSIDLock-macOS.zip
echo "Notarized: macos/build/BSSIDLock-macOS.zip"
