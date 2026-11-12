# Security policy

## Reporting a vulnerability

Please report security issues privately through **[GitHub Security Advisories](https://github.com/b3hz4d/bssid-lock/security/advisories/new)**, not in public issues. You'll get a reply within a few days.

## What BSSIDLock does and doesn't do

- **Network: update checks only.** No telemetry or analytics.
  - Once a day, if automatic updates are on (the default), the app asks `api.github.com` for this repository's latest release. That's a plain HTTPS request with a `BSSIDLock/<version>` user agent and no personal data.
  - Turn it off in Settings → Updates. "Check now" still works on demand.
- **How updates are verified before installing:**
  - The file must match the SHA-256 checksum published with the release.
  - It must report the right product name or bundle ID and the expected version.
  - **macOS:** the code signature must be valid. Once releases are signed with the project's certificate, an update must be signed by that same certificate, so a tampered release can't be installed.
  - Updates replace only the app itself, in place: `/Applications` or wherever you installed it on macOS, and `%LOCALAPPDATA%\Programs\BSSIDLock` on Windows. They need no administrator rights.
  - **Installed with winget:** the app doesn't replace itself. Once the new version is in winget's catalog (it asks `api.github.com` whether `microsoft/winget-pkgs` has it), it exits and runs `winget upgrade b3hz4d.BSSIDLock`, so winget does the download and its own checks, then starts again.
- **Activity log:** what the app did (network changes, switches, errors) is saved on your computer only: `~/Library/Application Support/BSSIDLock/activity.log` on macOS, `%APPDATA%\BSSIDLock\activity.log` on Windows. It's never sent anywhere.
- **No elevated privileges.** No administrator rights, drivers, services or daemons.
- **System access is limited to:**
  - Reading Wi-Fi status and scan results, which requires location permission on modern macOS and Windows.
  - Asking the OS to join an access point the user chose.
  - Its own settings file or preferences.
  - Optionally, a "start at login" entry: a Login Item on macOS, or a per-user Run key on Windows.
- **Location data:** BSSIDLock never requests coordinates. It needs location permission only because the operating system gates access point addresses behind it.
- **Passwords:** on macOS, the optional Wi-Fi password is stored in the user's login Keychain. On Windows, the app reuses the system's saved Wi-Fi profile and never sees the password.

## Verifying release files

Every release file is built by GitHub Actions from a tagged commit and carries a build provenance attestation:

```
gh attestation verify <file> -R b3hz4d/bssid-lock
```
