<p align="center">
  <img src="assets/icon-1024.png" width="128" alt="BSSIDLock icon">
</p>

<h1 align="center">BSSIDLock</h1>

<p align="center">
  Keep your Mac connected to the Wi-Fi access point <em>you</em> choose, not the one macOS roams to.
</p>

---

When one network name (SSID) is served by several access points, as in mesh systems, offices or homes with extenders, macOS decides which one to join. It often picks a far, slow one, or keeps hopping between them. macOS has no setting to pin a connection to a specific access point (BSSID), and the old `airport` command-line tool that people used for this was removed in macOS 14.4.

BSSIDLock is a small native macOS app that does it for you:

- **See every access point** broadcasting your network, with signal strength, channel and band.
- **Lock to one access point**, or allow several and let BSSIDLock pick the first one in range by your priority order.
- **Stays locked.** It checks every few seconds and switches back if macOS roams away.
- **Only touches the network you configure.** On any other Wi-Fi it does nothing.
- **Menu bar control.** You can see the status, switch the lock target, or turn the lock off without opening the window.
- **Stores your Wi-Fi password in the macOS Keychain.** The password is optional.
- **Launch at login.**

> **Status:** early (v1.0). macOS works; a Windows version is planned.

## Requirements

- macOS 13 Ventura or later (Apple silicon or Intel)
- To build: the Xcode Command Line Tools (`xcode-select --install`). Full Xcode isn't required.

## Build and run

```bash
git clone https://github.com/b3hz4d/bssid-lock.git
cd bssid-lock
./macos/build.sh
open macos/build/BSSIDLock.app
```

Optionally, copy `macos/build/BSSIDLock.app` to `/Applications`. Do this before you turn on **Launch at login**.

## First run

1. **Allow Location access** when macOS asks. macOS treats access point addresses (BSSIDs) as location data and hides them from apps without this permission. BSSIDLock never reads, stores or sends your actual location. If you dismissed the prompt, enable BSSIDLock in **System Settings → Privacy & Security → Location Services**.
2. **Choose your network.** If you're already connected to it, BSSIDLock picks it up automatically. Otherwise type the name in **Settings** or press **Use current**.
3. **Scan.** Every access point broadcasting that network appears under **Available access points**.
4. **Pick access points:**
   - **Lock here** pins your Mac to that one access point.
   - **Allow** adds it to the allowed list. With **Lock to: Any allowed access point**, BSSIDLock accepts any of them and prefers them in the order you drag them into.
5. *(Optional)* Enter the Wi-Fi password and press **Save**. It's needed only if switching fails without it.

The lock icon in the menu bar is filled when you're on an allowed access point.

## How it works

BSSIDLock uses Apple's CoreWLAN framework:

1. It reads the current network and BSSID every *N* seconds (10 by default).
2. If you're on your configured network but not on an allowed access point, it scans for that network and associates directly with the specific access point you chose (`CWInterface.associate(to:password:)`).
3. If none of the allowed access points is in range, it waits 60 seconds before trying again, so it doesn't keep scanning and slowing your connection.

It doesn't change system network settings, install anything privileged, or run any background daemon.

## Limitations

- **macOS can still roam in between checks.** BSSIDLock moves you back on the next check. Lower the interval if you need a tighter lock.
- **Location permission is required.** Without it macOS returns empty BSSIDs and the app can't tell the access points apart.
- **The build is ad-hoc signed and not notarized.** macOS treats each rebuild as a new app, so you'll be asked for Location access again after rebuilding. If you download a prebuilt copy instead of building it, Gatekeeper may block it. In that case, open it once via right-click → **Open**, or **System Settings → Privacy & Security → Open Anyway**.
- **Enterprise (802.1X/EAP) networks are untested.**

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| “BSSIDs hidden, Location access needed” | Enable BSSIDLock under Location Services. If that doesn't help, run `tccutil reset Location io.github.b3hz4d.bssidlock` and relaunch. |
| No permission prompt appears | Launch the app with `open` or from Finder rather than running the binary inside the bundle from Terminal. |
| “Connect to … failed” | Save the Wi-Fi password in Settings, and check that the access point is in range. |
| Status says “idle” | You're on a different network than the one configured. That's intentional. |

## Project layout

```
assets/        Icon source (icon-1024.png) and the script that draws it
macos/         macOS app: BSSIDLock.swift (single-file SwiftUI app), Info.plist, build.sh
```

## Roadmap

- [ ] Windows version (`windows/`, using the Native Wi-Fi API's BSSID-targeted connect)
- [ ] Signed and notarized macOS release builds
- [ ] Per-network allowed lists

## Related projects

- [`airport-bssid`](https://github.com/qpSHiNqp/airport-bssid) ([fork](https://github.com/braineo/airport-bssid)) is a command-line tool that joins a specific BSSID once. BSSIDLock adds a GUI, keeps the lock in place, and handles the Location permission that recent macOS versions require.

## Contributing

Issues and pull requests are welcome. The whole macOS app is one file, `macos/BSSIDLock.swift`, and `./macos/build.sh` rebuilds it in a few seconds.

## License

[MIT](LICENSE)
