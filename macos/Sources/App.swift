// BSSIDLock: keep your Mac connected to the Wi-Fi access point(s) you choose.
// https://github.com/b3hz4d/bssid-lock
import SwiftUI
import UserNotifications

/// Closing the main window leaves BSSIDLock running in the menu bar only, with no Dock icon.
final class AppDelegate: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    /// Show notifications as banners even while BSSIDLock is the active app.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                            withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list])
    }

    /// A notification button was clicked ("Lock to This One", "Go Back", "Manage This Network").
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                            withCompletionHandler completionHandler: @escaping () -> Void) {
        let action = response.actionIdentifier
        let info = response.notification.request.content.userInfo as? [String: String] ?? [:]
        Task { @MainActor in Notifier.onAction?(action, info) }
        completionHandler()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        UNUserNotificationCenter.current().delegate = self
        // After an automatic update, come back quietly in the menu bar.
        if CommandLine.arguments.contains("--updated") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                for w in NSApp.windows where Self.isDocumentWindow(w) { w.close() }
                NSApp.setActivationPolicy(.accessory)
            }
        }
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: nil, queue: .main) { note in
            guard let closing = note.object as? NSWindow, Self.isDocumentWindow(closing) else { return }
            DispatchQueue.main.async {
                let stillOpen = NSApp.windows.contains { $0 !== closing && $0.isVisible && Self.isDocumentWindow($0) }
                if !stillOpen { NSApp.setActivationPolicy(.accessory) }
            }
        }
    }

    /// A normal titled window (not the menu bar popover, which is a panel).
    private static func isDocumentWindow(_ w: NSWindow) -> Bool {
        w.styleMask.contains(.titled) && !(w is NSPanel) && w.sheetParent == nil
    }
}

@main
struct BSSIDLockApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var engine = Engine()
    @StateObject private var ui = UIState()

    var body: some Scene {
        Window("BSSIDLock", id: "main") {
            MainView()
                .environmentObject(engine)
                .environmentObject(ui)
                .environmentObject(engine.updater)
        }
        .defaultSize(width: 720, height: 600)
        .commands {
            CommandGroup(after: .appInfo) {
                Button(engine.updater.actionTitle) { Task { await engine.updater.check(userInitiated: true) } }
                    .disabled(engine.updater.busy || engine.updater.installing)
            }
            CommandGroup(replacing: .appSettings) {
                Button("Settings…") { ui.showSettings = true }
                    .keyboardShortcut(",", modifiers: .command)
            }
            CommandMenu("Lock") {
                Toggle("Lock Enabled", isOn: $engine.lockEnabled)
                    .keyboardShortcut("l", modifiers: .command)
                Menu("Pause") { PauseMenuItems(e: engine, prefix: "") }
                    .disabled(!engine.lockEnabled || engine.isPaused)
                Button("Resume") { engine.resume() }
                    .disabled(!engine.isPaused)
                Divider()
                Button("Scan Now") { engine.requestScan() }
                    .keyboardShortcut("r", modifiers: .command)
                Divider()
                Button("Show Activity") { ui.showActivity = true }
            }
        }

        MenuBarExtra {
            MenuBarView()
                .environmentObject(engine)
                .environmentObject(ui)
                .environmentObject(engine.updater)
        } label: {
            Image(systemName: engine.isPaused ? "pause.circle" : engine.isLocked ? "lock.fill" : "lock.open")
        }
        .menuBarExtraStyle(.window)
    }
}

/// The popover shown from the menu bar icon.
struct MenuBarView: View {
    @EnvironmentObject var e: Engine
    @EnvironmentObject var ui: UIState
    @EnvironmentObject var updater: Updater
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

            if e.isPaused {
                Button { e.resume() } label: { Label("Resume the Lock", systemImage: "play.circle.fill") }
                    .buttonStyle(.borderless)
            }

            if e.locationOK && !e.ssid.isEmpty {
                Divider()
                HStack {
                    Text("LOCK TO").font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
                    Spacer()
                    Text(e.ssid).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                }
                ChoiceRow(title: "Any favorite",
                          subtitle: e.favorites.isEmpty ? "Star access points in the main window" : "Prefers the top of your list",
                          rssi: nil, showBars: false,
                          selected: e.lockEnabled && e.target == nil) {
                    e.lockEnabled = true
                    e.target = nil
                    e.resume()
                }
                .disabled(e.favorites.isEmpty)
                ForEach(quickItems) { item in
                    ChoiceRow(title: e.label(for: item.bssid),
                              subtitle: item.ap.map { ($0.ssid.isEmpty ? "" : "\($0.ssid) · ") + "\($0.band) · \(SignalQuality(rssi: $0.rssi).label) (\($0.rssi) dBm)" } ?? "Out of range",
                              rssi: item.ap?.rssi, showBars: true,
                              selected: e.lockEnabled && e.target == item.bssid) {
                        e.lockTo(item.bssid)
                    }
                }
            }

            if let v = updater.available {
                Divider()
                Button { Task { await updater.check(userInitiated: true) } } label: {
                    Label(updater.installing ? "Restarting…" : updater.busy ? "Updating…" : "Install version \(v)", systemImage: "arrow.down.circle.fill")
                }
                .buttonStyle(.borderless)
                .disabled(updater.busy || updater.installing)
            }

            Divider()
            HStack(spacing: 14) {
                Button("Open BSSIDLock") { show() }
                Text("v\(Updater.currentVersion)").font(.caption).foregroundStyle(.tertiary)
                Spacer()
                Menu { PauseMenuItems(e: e, prefix: "Pause ") } label: { Image(systemName: "pause.circle") }
                    .menuStyle(.borderlessButton)
                    .menuIndicator(.hidden)
                    .fixedSize()
                    .disabled(!e.lockEnabled || e.isPaused)
                    .help("Pause the lock for a while")
                Button { e.requestScan() } label: {
                    if e.busy { ProgressView().controlSize(.small) } else { Image(systemName: "arrow.clockwise") }
                }
                .help("Scan now")
                Button { show(); ui.showSettings = true } label: { Image(systemName: "gearshape") }
                    .help("Settings")
                Button { NSApp.terminate(nil) } label: { Image(systemName: "power") }
                    .help("Quit BSSIDLock")
            }
            .buttonStyle(.borderless)
        }
        .padding(14)
        .frame(width: 330)
        .onAppear { e.setViewing(popover: true) }
        .onDisappear { e.setViewing(popover: false) }
    }

    /// Favorites first, then the strongest other access points; at most 8 rows.
    private var quickItems: [APItem] { Array((e.favoriteItems + e.otherItems).prefix(8)) }

    private func show() {
        NSApp.setActivationPolicy(.regular)   // bring the Dock icon back while the window is open
        openWindow(id: "main")
        bringToFront()
    }
}

/// "For 15 Minutes", "For 1 Hour", "Until I Switch Networks" (with an optional "Pause " in front).
struct PauseMenuItems: View {
    let e: Engine
    let prefix: String

    var body: some View {
        Button(prefix + (prefix.isEmpty ? "For" : "for") + " 15 Minutes") { e.pause(.minutes(15)) }
        Button(prefix + (prefix.isEmpty ? "For" : "for") + " 1 Hour") { e.pause(.minutes(60)) }
        Button(prefix + "Until I Switch Networks") { e.pause(.untilNetworkChanges) }
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
