#!/bin/zsh
# Signs macos/build/BSSIDLock.app with the project's own self-signed certificate.
#
# Why: ad-hoc signatures change with every build, so macOS forgets the Location permission after each
# update. A stable (free, self-signed) certificate keeps the same identity across versions, and lets the
# in-app updater insist that updates are signed by the same certificate.
#
# It does NOT remove Gatekeeper's warning for browser downloads (only an Apple Developer ID does that);
# the one-line installer and the in-app updater never trigger that warning anyway.
#
# Needs: SELFSIGN_P12 (path to a .p12 with the certificate + key), SELFSIGN_PASSWORD.
# Uses rcodesign (https://github.com/indygreg/apple-platform-rs), pinned by version and SHA-256, because
# Apple's codesign refuses certificates that aren't trusted by the system.
set -euo pipefail
cd "$(dirname "$0")"
: "${SELFSIGN_P12:?set SELFSIGN_P12}" "${SELFSIGN_PASSWORD:?set SELFSIGN_PASSWORD}"

APP=build/BSSIDLock.app
BUNDLE_ID=$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "$APP/Contents/Info.plist")
RC_VERSION=0.29.0
RC_SHA256=d98372d5524226ccf9dc0eda03d4e4f5826182dabb2fc3f2bd303ed9113a748d

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
curl -fsSL "https://github.com/indygreg/apple-platform-rs/releases/download/apple-codesign/$RC_VERSION/apple-codesign-$RC_VERSION-macos-universal.tar.gz" -o "$TMP/rc.tar.gz"
echo "$RC_SHA256  $TMP/rc.tar.gz" | shasum -a 256 -c - >/dev/null
tar -xzf "$TMP/rc.tar.gz" -C "$TMP"
RC=$(find "$TMP" -name rcodesign -type f -perm -u+x | head -1)

# Designated requirement: this bundle ID, signed by this exact certificate.
CERT_SHA1=$(/usr/bin/openssl pkcs12 -in "$SELFSIGN_P12" -passin env:SELFSIGN_PASSWORD -nokeys -clcerts 2>/dev/null \
  | /usr/bin/openssl x509 -noout -fingerprint -sha1 | cut -d= -f2 | tr -d :)
[[ -n "$CERT_SHA1" ]] || { echo "Couldn't read the certificate from $SELFSIGN_P12" >&2; exit 1; }
# rcodesign wants a single requirement expression (blob 0xfade0c00), not a "designated => …" set.
csreq -r "=identifier \"$BUNDLE_ID\" and certificate leaf = H\"$CERT_SHA1\"" -b "$TMP/requirements.bin"

printf '%s' "$SELFSIGN_PASSWORD" > "$TMP/pw"
"$RC" sign --p12-file "$SELFSIGN_P12" --p12-password-file "$TMP/pw" \
  --code-requirements-path "$TMP/requirements.bin" --timestamp-url none "$APP" >/dev/null

codesign --verify --strict "$APP"
echo "Signed with certificate $CERT_SHA1:"
codesign -d -r- "$APP" 2>&1 | grep designated
