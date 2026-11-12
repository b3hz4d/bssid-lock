#!/usr/bin/env bash
# Writes the winget manifests (https://github.com/microsoft/winget-pkgs) for a published GitHub release.
#
#   winget/make-manifests.sh 1.4.0 [OUT_DIR]
#
# The files land in OUT_DIR/manifests/b/b3hz4d/BSSIDLock/<version>/, the same layout as winget-pkgs.
# The installer checksum comes from the release's SHA256SUMS.txt and is checked against the real download.
set -euo pipefail

version="${1:?usage: make-manifests.sh VERSION [OUT_DIR]}"
version="${version#v}"
out="${2:-winget/out}"
repo="b3hz4d/bssid-lock"
id="b3hz4d.BSSIDLock"
schema="1.28.0"
base="https://github.com/$repo/releases/download/v$version"
url="$base/BSSIDLock.exe"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

curl -fsSL "$base/SHA256SUMS.txt" -o "$tmp/SHA256SUMS.txt"
sha="$(awk '$2 ~ /^\*?BSSIDLock\.exe$/ { print toupper($1) }' "$tmp/SHA256SUMS.txt")"
[ "${#sha}" -eq 64 ] || { echo "No checksum for BSSIDLock.exe in $base/SHA256SUMS.txt" >&2; exit 1; }

curl -fsSL "$url" -o "$tmp/BSSIDLock.exe"
if command -v sha256sum >/dev/null; then actual="$(sha256sum "$tmp/BSSIDLock.exe" | awk '{ print toupper($1) }')"
else actual="$(shasum -a 256 "$tmp/BSSIDLock.exe" | awk '{ print toupper($1) }')"; fi
[ "$actual" = "$sha" ] || { echo "Checksum mismatch: SHA256SUMS.txt says $sha, the download is $actual" >&2; exit 1; }

released="$(curl -fsSL "https://api.github.com/repos/$repo/releases/tags/v$version" \
  | grep -m1 '"published_at"' | sed -E 's/.*"([0-9]{4}-[0-9]{2}-[0-9]{2})T.*/\1/')"
[[ "$released" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]] || { echo "Couldn't read the release date of v$version" >&2; exit 1; }

dir="$out/manifests/b/b3hz4d/BSSIDLock/$version"
mkdir -p "$dir"

cat > "$dir/$id.yaml" <<YAML
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
YAML

cat > "$dir/$id.installer.yaml" <<YAML
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $version
MinimumOSVersion: 10.0.18362.0
InstallerType: portable
ReleaseDate: $released
Installers:
- Architecture: neutral
  InstallerUrl: $url
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: $schema
YAML

cat > "$dir/$id.locale.en-US.yaml" <<YAML
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $version
PackageLocale: en-US
Publisher: Behzad Fattahi
PublisherUrl: https://github.com/b3hz4d
PublisherSupportUrl: https://github.com/$repo/issues
Author: Behzad Fattahi
PackageName: BSSIDLock
PackageUrl: https://github.com/$repo
License: MIT
LicenseUrl: https://github.com/$repo/blob/main/LICENSE
Copyright: Copyright (c) 2026 Behzad Fattahi
ShortDescription: Keep your PC connected to the Wi-Fi access point (BSSID) you choose.
Description: |-
  When one Wi-Fi network name is served by several access points (mesh systems, offices, homes with extenders), Windows decides which one to join. It often picks a far, slow one, or keeps hopping between them.
  BSSIDLock keeps your PC on the access point you choose, or on a few favorites in your priority order, and moves it back within seconds when Windows roams away. Each network can have its own favorites, and BSSIDLock never touches networks you didn't pick.
  It runs in the notification area and needs no administrator rights.
Moniker: bssidlock
Tags:
- access-point
- bssid
- mesh
- roaming
- wi-fi
- wifi
- wireless
ReleaseNotesUrl: https://github.com/$repo/releases/tag/v$version
ManifestType: defaultLocale
ManifestVersion: $schema
YAML

echo "Wrote $dir"
