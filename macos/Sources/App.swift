// BSSIDLock: keep your Mac connected to the Wi-Fi access point(s) you choose.
// https://github.com/b3hz4d/bssid-lock
import SwiftUI

@main
struct BSSIDLockApp: App {
    @StateObject private var engine = Engine()
    @StateObject private var ui = UIState()

    var body: some Scene {
        Window("BSSIDLock", id: "main") {
            MainView()
                .environmentObject(engine)
                .environmentObject(ui)
        }
        .defaultSize(width: 720, height: 600)
        .commands {
            CommandGroup(replacing: .appSettings) {
                Button("Settings…") { ui.showSettings = true }
                    .keyboardShortcut(",", modifiers: .command)
            }
            CommandMenu("Lock") {
                Toggle("Lock Enabled", isOn: $engine.lockEnabled)
                    .keyboardShortcut("l", modifiers: .command)
                Button("Scan Now") { Task { await engine.scan() } }
                    .keyboardShortcut("r", modifiers: .command)
                Divider()
                Button("Show Activity") { ui.showActivity = true }
            }
        }

        MenuBarExtra {
            MenuBarView()
                .environmentObject(engine)
                .environmentObject(ui)
        } label: {
            Image(systemName: engine.isLocked ? "lock.fill" : "lock.open")
        }
        .menuBarExtraStyle(.window)
    }
}

/// The popover shown from the menu bar icon.
struct MenuBarView: View {
    @EnvironmentObject var e: Engine
    @EnvironmentObject var ui: UIState
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                StatusBadge(state: e.state, size: 36)
                VStack(alignment: .leading, spacing: 2) {
                    Text(e.statusTitle).font(.headline).lineLimit(1)
                    Text(e.statusDetail).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                }
                Spacer(minLength: 8)
                Toggle("Lock", isOn: $e.lockEnabled)
                    .toggleStyle(.switch)
                    .labelsHidden()
                    .controlSize(.small)
                    .help(e.lockEnabled ? "Turn the lock off" : "Turn the lock on")
            }

            if e.locationOK && !e.ssid.isEmpty {
                Divider()
                Text("LOCK TO").font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
                ChoiceRow(title: "Any favorite",
                          subtitle: e.favorites.isEmpty ? "Star access points in the main window" : "Prefers the top of your list",
                          rssi: nil, showBars: false,
                          selected: e.lockEnabled && e.target == nil) {
                    e.lockEnabled = true
                    e.target = nil
                }
                .disabled(e.favorites.isEmpty)
                ForEach(quickItems) { item in
                    ChoiceRow(title: e.label(for: item.bssid),
                              subtitle: item.ap.map { "\($0.band) · \(SignalQuality(rssi: $0.rssi).label)" } ?? "Out of range",
                              rssi: item.ap?.rssi, showBars: true,
                              selected: e.lockEnabled && e.target == item.bssid) {
                        e.lockTo(item.bssid)
                    }
                }
            }

            Divider()
            HStack(spacing: 14) {
                Button("Open BSSIDLock") { show() }
                Spacer()
                Button { Task { await e.scan() } } label: { Image(systemName: "arrow.clockwise") }
                    .help("Scan").disabled(e.busy)
                Button { show(); ui.showSettings = true } label: { Image(systemName: "gearshape") }
                    .help("Settings")
                Button { NSApp.terminate(nil) } label: { Image(systemName: "power") }
                    .help("Quit BSSIDLock")
            }
            .buttonStyle(.borderless)
        }
        .padding(14)
        .frame(width: 330)
    }

    /// Favorites first, then the strongest other access points; at most 8 rows.
    private var quickItems: [APItem] { Array((e.favoriteItems + e.otherItems).prefix(8)) }

    private func show() {
        openWindow(id: "main")
        bringToFront()
    }
}

struct ChoiceRow: View {
    let title: String
    let subtitle: String
    let rssi: Int?
    let showBars: Bool
    let selected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                    .foregroundStyle(selected ? Color.accentColor : Color.secondary)
                if showBars { SignalBars(rssi: rssi) }
                VStack(alignment: .leading, spacing: 1) {
                    Text(title).lineLimit(1)
                    Text(subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer()
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}
