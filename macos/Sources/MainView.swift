// The main window: onboarding, then the dashboard.
import SwiftUI

struct MainView: View {
    @EnvironmentObject var e: Engine
    @EnvironmentObject var ui: UIState

    var body: some View {
        Group {
            if !e.locationOK {
                LocationOnboarding()
            } else if e.ssid.isEmpty {
                NetworkOnboarding()
            } else {
                Dashboard()
            }
        }
        .frame(minWidth: 640, minHeight: 520)
        .sheet(isPresented: $ui.showSettings) {
            SettingsSheet().environmentObject(e).environmentObject(ui)
        }
        .sheet(isPresented: $ui.showActivity) {
            ActivitySheet().environmentObject(e).environmentObject(ui)
        }
        .alert("Name this access point", isPresented: renameBinding) {
            TextField("e.g. Living room", text: $ui.renameText)
            Button("Save") {
                if let b = ui.renaming { e.rename(b, to: ui.renameText) }
                ui.renaming = nil
            }
            Button("Cancel", role: .cancel) { ui.renaming = nil }
        } message: {
            Text("Give \(ui.renaming ?? "") a name you'll recognize. Leave it empty to show the address.")
        }
    }

    private var renameBinding: Binding<Bool> {
        Binding(get: { ui.renaming != nil }, set: { if !$0 { ui.renaming = nil } })
    }
}

// MARK: - Onboarding

struct LocationOnboarding: View {
    @EnvironmentObject var e: Engine

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: "location.circle.fill")
                .font(.system(size: 56))
                .foregroundStyle(.tint)
            Text("Allow Location access")
                .font(.title2.weight(.semibold))
            Text("macOS treats Wi-Fi access point addresses as location data and hides them from apps without Location access. BSSIDLock needs them to tell your access points apart.\n\nIt never reads, stores or sends your actual location.")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                .frame(maxWidth: 460)
            Button(e.locAuth == .notDetermined ? "Allow Location Access" : "Open Location Settings") {
                e.requestLocationAccess()
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            if e.locAuth == .denied || e.locAuth == .restricted {
                Text("Turn on **BSSIDLock** in Privacy & Security → Location Services. This window updates by itself.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(40)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

struct NetworkOnboarding: View {
    @EnvironmentObject var e: Engine
    @EnvironmentObject var ui: UIState

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: "wifi.circle.fill")
                .font(.system(size: 56))
                .foregroundStyle(.tint)
            Text("Which Wi-Fi network should BSSIDLock manage?")
                .font(.title2.weight(.semibold))
            Text("It only acts while you're on this network and never touches any other.")
                .foregroundStyle(.secondary)

            if let cur = e.currentSSID {
                Button {
                    e.ssid = cur
                } label: {
                    Label("Use “\(cur)”, the network you're on", systemImage: "checkmark.circle")
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
            }

            GroupBox {
                if e.nearby.isEmpty {
                    HStack {
                        if e.busy { ProgressView().controlSize(.small) }
                        Text(e.busy ? "Looking for networks…" : "No networks found yet.")
                            .foregroundStyle(.secondary)
                        Spacer()
                    }
                    .padding(6)
                } else {
                    ScrollView {
                        VStack(spacing: 0) {
                            ForEach(e.nearby) { n in
                                HStack(spacing: 10) {
                                    SignalBars(rssi: n.rssi)
                                    Text(n.ssid).fontWeight(n.ssid == e.currentSSID ? .semibold : .regular)
                                    if n.count > 1 {
                                        Text("\(n.count) access points").font(.caption).foregroundStyle(.secondary)
                                    }
                                    Spacer()
                                    Button("Choose") { e.ssid = n.ssid }
                                }
                                .padding(.vertical, 6)
                                .padding(.horizontal, 6)
                                Divider()
                            }
                        }
                    }
                    .frame(maxHeight: 200)
                }
            } label: {
                HStack {
                    Text("Nearby networks")
                    Spacer()
                    Button { Task { await e.scanNetworks() } } label: { Image(systemName: "arrow.clockwise") }
                        .buttonStyle(.borderless)
                        .disabled(e.busy)
                        .help("Scan again")
                }
            }
            .frame(maxWidth: 460)

            HStack {
                TextField("Or type a network name", text: $ui.ssidDraft)
                    .onSubmit(choose)
                Button("Continue", action: choose)
                    .disabled(ui.ssidDraft.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            .frame(maxWidth: 460)
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onAppear { if e.nearby.isEmpty { Task { await e.scanNetworks() } } }
    }

    private func choose() {
        let s = ui.ssidDraft.trimmingCharacters(in: .whitespaces)
        if !s.isEmpty { e.ssid = s }
    }
}

// MARK: - Dashboard

struct Dashboard: View {
    @EnvironmentObject var e: Engine
    @EnvironmentObject var ui: UIState

    var body: some View {
        VStack(spacing: 0) {
            StatusHeader()
                .padding(16)
            Divider()
            AccessPointList()
            Divider()
            FooterBar()
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
        }
        .toolbar {
            ToolbarItemGroup {
                Button { Task { await e.scan() } } label: { Label("Scan", systemImage: "arrow.clockwise") }
                    .disabled(e.busy)
                    .help("Scan for access points")
                Button { ui.showActivity = true } label: { Label("Activity", systemImage: "list.bullet.rectangle") }
                    .help("Show what BSSIDLock has done")
                Button { ui.showSettings = true } label: { Label("Settings", systemImage: "gearshape") }
                    .help("Settings")
            }
        }
    }
}

struct StatusHeader: View {
    @EnvironmentObject var e: Engine

    var body: some View {
        HStack(alignment: .center, spacing: 14) {
            StatusBadge(state: e.state)
            VStack(alignment: .leading, spacing: 3) {
                Text(e.statusTitle)
                    .font(.title3.weight(.semibold))
                    .lineLimit(1)
                Text(e.statusDetail)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }
            Spacer(minLength: 16)
            VStack(alignment: .trailing, spacing: 8) {
                Toggle(e.lockEnabled ? "Lock on" : "Lock off", isOn: $e.lockEnabled)
                    .toggleStyle(.switch)
                    .help("When on, BSSIDLock keeps you on the access point(s) you chose")
                Picker("Lock to", selection: $e.target) {
                    Text("Any favorite").tag(String?.none)
                    if !e.favorites.isEmpty { Divider() }
                    ForEach(e.favorites, id: \.self) { b in
                        Text(e.label(for: b)).tag(String?.some(b))
                    }
                }
                .frame(width: 240)
                .disabled(!e.lockEnabled)
                .help("“Any favorite” accepts any starred access point and prefers the top one")
            }
        }
    }
}

struct AccessPointList: View {
    @EnvironmentObject var e: Engine

    var body: some View {
        List {
            Section {
                if e.favorites.isEmpty {
                    Text("Star (☆) the access points you're happy to use, or press **Lock** to stick to just one.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .padding(.vertical, 4)
                }
                ForEach(e.favoriteItems) { item in
                    APRow(item: item)
                }
                .onMove { e.favorites.move(fromOffsets: $0, toOffset: $1) }
            } header: {
                HStack {
                    Text("Favorites")
                    if e.favorites.count > 1 {
                        Text("drag to set priority").font(.caption).foregroundStyle(.secondary)
                    }
                }
            }

            Section {
                if e.otherItems.isEmpty {
                    Text(e.visible.isEmpty
                         ? (e.busy ? "Scanning…" : "No access points found yet. Press Scan.")
                         : "Every access point in range is already a favorite.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .padding(.vertical, 4)
                }
                ForEach(e.otherItems) { item in
                    APRow(item: item)
                }
            } header: {
                Text("Other access points on “\(e.ssid)”")
            }
        }
        .listStyle(.inset(alternatesRowBackgrounds: false))
    }
}

struct APRow: View {
    @EnvironmentObject var e: Engine
    @EnvironmentObject var ui: UIState
    let item: APItem

    var body: some View {
        let b = item.bssid
        let name = e.name(for: b)
        let isCurrent = b == e.currentBSSID
        let isTarget = b == e.target
        let isFav = item.rank != nil

        HStack(spacing: 12) {
            if let rank = item.rank {
                Text("\(rank + 1)")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                    .frame(width: 14)
            }
            SignalBars(rssi: item.ap?.rssi)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(name ?? b)
                        .font(name == nil ? .body.monospaced() : .body.weight(.medium))
                    if isCurrent { Pill(text: "Connected", color: .green) }
                    if isTarget { Pill(text: "Locked", color: .accentColor) }
                }
                Text(detail(name: name))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button {
                e.toggleFavorite(b)
            } label: {
                Image(systemName: isFav ? "star.fill" : "star")
                    .foregroundStyle(isFav ? Color.yellow : Color.secondary)
            }
            .buttonStyle(.borderless)
            .help(isFav ? "Remove from favorites" : "Add to favorites")

            if isTarget {
                Button("Unlock") { e.target = nil }
                    .help("Stop locking to this one; accept any favorite again")
            } else {
                Button("Lock") { e.lockTo(b) }
                    .disabled(e.busy)
                    .help("Stick to this access point only")
            }
        }
        .padding(.vertical, 3)
        .contentShape(Rectangle())
        .contextMenu {
            Button("Lock to This Access Point") { e.lockTo(b) }
            Button(isFav ? "Remove from Favorites" : "Add to Favorites") { e.toggleFavorite(b) }
            Button("Rename…") { ui.startRename(b, current: name) }
            if isFav {
                Divider()
                Button("Move Up") { e.moveFavorite(b, by: -1) }.disabled(item.rank == 0)
                Button("Move Down") { e.moveFavorite(b, by: 1) }.disabled(item.rank == e.favorites.count - 1)
            }
            Divider()
            Button("Copy Address") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(b, forType: .string)
            }
        }
    }

    private func detail(name: String?) -> String {
        var parts: [String] = []
        if name != nil { parts.append(item.bssid) }
        if let ap = item.ap {
            parts.append("\(ap.band) · channel \(ap.channel)")
            parts.append("\(SignalQuality(rssi: ap.rssi).label) (\(ap.rssi) dBm)")
        } else {
            parts.append("Out of range")
        }
        return parts.joined(separator: " · ")
    }
}

struct FooterBar: View {
    @EnvironmentObject var e: Engine

    var body: some View {
        HStack(spacing: 8) {
            if e.busy {
                ProgressView().controlSize(.small)
                Text("Working…")
            } else if let t = e.lastScan {
                Text("Last scan \(t, style: .relative) ago")
            } else {
                Text("Not scanned yet")
            }
            Spacer()
            Text("Checks every \(Int(e.interval)) s · Right-click an access point for more options")
        }
        .font(.caption)
        .foregroundStyle(.secondary)
    }
}
