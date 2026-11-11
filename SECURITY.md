# Security policy

## Reporting a vulnerability

Please report security issues privately through **[GitHub Security Advisories](https://github.com/b3hz4d/bssid-lock/security/advisories/new)**, not in public issues. You'll get a reply within a few days.

## What BSSIDLock does and doesn't do

- **No network communication.** No telemetry, analytics or auto-update.
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
