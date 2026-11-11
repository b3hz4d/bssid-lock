<p align="center">
  <img src="assets/icon-1024.png" width="128" alt="BSSIDLock icon">
</p>

<h1 align="center">BSSIDLock</h1>

<p align="center">
  Keep your computer on the Wi-Fi access point <em>you</em> choose, not the one it roams to.<br>
  Native apps for <strong>macOS</strong> and <strong>Windows</strong>. Free and open source.
</p>

<p align="center">
  <a href="https://github.com/b3hz4d/bssid-lock/releases/latest"><b>Download</b></a> ·
  <a href="#first-run">First run</a> ·
  <a href="#is-it-safe">Is it safe?</a> ·
  <a href="#build-from-source">Build from source</a>
</p>

---

When one network name (SSID) is served by several access points, as in mesh systems, offices or homes with extenders, your computer decides which one to join. It often picks a far, slow one, or keeps hopping between them. Neither macOS nor Windows lets you pin a connection to a specific access point (BSSID).

BSSIDLock does:

- **See every access point** on your network, with signal quality, band and channel.
- **Lock to one access point**, or star a few **favorites** and let BSSIDLock use the best one by your priority order.
- **Name your access points** ("Living room", "Office") instead of memorizing addresses.
- **Stays locked.** It checks every few seconds and switches back if the system roams away.
- **Only touches the network you choose.** On any other Wi-Fi it does nothing.
- **Lives in the menu bar (macOS) or notification area (Windows).** Switch the lock target or turn the lock off from there.
- **Start at login**, quietly in the background.

## Download

Get the latest version from **[Releases](https://github.com/b3hz4d/bssid-lock/releases/latest)**:

| Platform | File | Requirements |
| --- | --- | --- |
| macOS | `BSSIDLock-macOS.zip` | macOS 13 Ventura or later, Apple silicon or Intel |
| Windows | `BSSIDLock.exe` | Windows 10 (1903+) or 11, nothing to install |

**macOS:** unzip the file, move **BSSIDLock** to Applications, and open it.

**Windows:** run `BSSIDLock.exe` from anywhere, for example a folder in your user profile. It doesn't install anything or need administrator rights.

> The first time you open a downloaded build, macOS or Windows may warn you about an app from an unknown developer. That's normal for a new open-source app; see [Is it safe?](#is-it-safe) for why it happens and how to check the file.

## First run

1. **Allow location access.** Both macOS and Windows treat access point addresses as location data and hide them from apps without location permission. BSSIDLock never reads, stores or sends your actual location.
   - **macOS:** click **Allow** when asked. If you missed it, turn on BSSIDLock in System Settings → Privacy & Security → Location Services.
   - **Windows 11:** turn on **Location services** and **Let desktop apps access your location** in Settings → Privacy & security → Location. BSSIDLock shows a button that takes you there.
2. **Choose your network.** If you're connected to it, BSSIDLock picks it up automatically. Otherwise choose it from the list.
3. **Pick access points.** All access points on your network are listed with their signal strength.
   - **Lock** sticks to just that access point.
   - **★ (favorite)** marks the access points you're happy to use. Set **Lock to** to **Any favorite**, and BSSIDLock accepts any of them and prefers the top of the list. Drag (macOS) or use ↑/↓ (Windows) to reorder.
   - **Rename** gives an access point a friendly name. On macOS, right-click it; on Windows, press F2.

Leave BSSIDLock running. Closing the window keeps it working from the menu bar or notification area.

## Is it safe?

Yes, and you can check that yourself:

- **The code is open.** Both apps are small and readable: [`macos/Sources`](macos/Sources) and [`windows/BSSIDLock`](windows/BSSIDLock).
- **Release files are built by GitHub, not on someone's laptop.** The [Release workflow](.github/workflows/release.yml) builds both apps from the tagged source. Each release lists SHA-256 checksums and carries a signed [build provenance attestation](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations). To prove a file came from this repository, run:
  ```
  gh attestation verify BSSIDLock.exe -R b3hz4d/bssid-lock
  ```
- **It does very little.**
  - No network access, telemetry or updater.
  - No administrator rights, drivers or background services.
  - It only reads Wi-Fi status and asks the system to join an access point you picked.
  - Settings stay on your computer. The optional macOS Wi-Fi password goes in your Keychain.
- **The Windows app is plain .NET Framework,** with no packer, bundler or obfuscation. Antivirus engines can read exactly what it does.

**About the "unknown developer" warning:** operating systems warn about any app that isn't signed with a paid code-signing certificate. Windows also warns about apps that don't have download reputation yet. The warning isn't a malware detection.
- **Windows:** in the SmartScreen prompt, choose **More info** → **Run anyway**.
- **macOS:** open the app once, then go to System Settings → Privacy & Security and click **Open Anyway**.
- **Avoid the warning entirely:** [build it yourself](#build-from-source). It takes about a minute.

Signed builds are on the [roadmap](#roadmap). Maintainers can see [docs/SIGNING.md](docs/SIGNING.md).

## Build from source

```bash
git clone https://github.com/b3hz4d/bssid-lock.git
cd bssid-lock
```

**macOS app.** Needs the Xcode Command Line Tools (`xcode-select --install`):
```bash
./macos/build.sh                 # → macos/build/BSSIDLock.app (universal)
open macos/build/BSSIDLock.app
```

**Windows app.** Needs the [.NET SDK](https://dot.net) 8 or later, and builds on Windows, macOS or Linux:
```bash
./windows/build.sh               # macOS / Linux → windows/out/BSSIDLock.exe
.\windows\build.ps1              # Windows (PowerShell)
```

## How it works

Every few seconds (10 by default), BSSIDLock reads which network and access point you're on. If you're on your chosen network but not on an allowed access point, it scans and asks the system to join the specific access point you picked:

- **macOS:** CoreWLAN, `CWInterface.associate(to:password:)` with the scanned network for that BSSID.
- **Windows:** Native Wi-Fi API, `WlanConnect` with your saved Wi-Fi profile and a one-entry desired-BSSID list.

If none of your choices is in range, it waits 60 seconds before trying again, so it doesn't keep scanning and slowing your connection.

## Limitations

- **The system can still roam in between checks.** BSSIDLock moves you back on the next check, so lower the interval for a tighter lock.
- **Location permission is required** on macOS and on Windows 11 24H2 and later. Without it, access points can't be told apart.
- **Windows needs the network saved.** Connect to it normally once first. BSSIDLock reuses the saved profile and never asks for the password.
- **Some Wi-Fi drivers ignore the access point request.** Check the Activity log if switching doesn't stick.
- **Enterprise (802.1X/EAP) networks are untested.**

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| "Location access needed" | Grant location access; see [First run](#first-run). On macOS you can also run `tccutil reset Location io.github.b3hz4d.bssidlock` and relaunch. |
| macOS: no permission prompt | Open the app from Finder or with `open`, not by running the binary inside the bundle. |
| "Couldn't switch" | Check that the access point is in range. On macOS, save the Wi-Fi password in Settings. On Windows, make sure you've connected to the network once. |
| Status says "Idle on …" | You're on a different network than the one chosen. That's intentional. |
| Windows: app "disappears" when closed | It's still running in the notification area (tray). Right-click its icon → Exit to quit. |

## Project layout

```
assets/                  Shared icon (icon-1024.png) and the scripts that draw it / build the .ico
macos/                   macOS app: SwiftUI sources, Info.plist, build.sh, notarize.sh
windows/                 Windows app: WinForms on .NET Framework 4.8, build.sh / build.ps1
docs/SIGNING.md          How releases get signed (for maintainers)
.github/workflows/       CI builds and the release pipeline
```

## Roadmap

- [x] macOS app
- [x] Windows app
- [ ] Signed and notarized macOS builds; signed Windows builds
- [ ] `winget` and Homebrew packages
- [ ] Separate favorites for each network
- [ ] Linux (NetworkManager already supports `bssid` per connection; a tray app could wrap it)

## Related projects

- [`airport-bssid`](https://github.com/qpSHiNqp/airport-bssid) ([fork](https://github.com/braineo/airport-bssid)) is a macOS command-line tool that joins a specific BSSID once.
- [WifiInfoView](https://www.nirsoft.net/utils/wifi_information_view.html) is a Windows freeware Wi-Fi scanner that can connect to a chosen access point.

BSSIDLock adds a friendly GUI on both platforms and keeps the lock in place.

## Contributing

Issues and pull requests are welcome. Both apps rebuild in seconds with the scripts above. Please describe your OS version and Wi-Fi hardware in bug reports; drivers matter here.

## License

[MIT](LICENSE)
