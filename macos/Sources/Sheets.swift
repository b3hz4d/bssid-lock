// Settings and Activity sheets.
import SwiftUI

struct SettingsSheet: View {
    @EnvironmentObject var e: Engine
    @EnvironmentObject var ui: UIState
    @EnvironmentObject var updater: Updater

    private static let intervalPresets = [5, 10, 15, 30, 60]
    private static let customTag = -1

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    ForEach(e.networks, id: \.self) { n in
                        HStack(spacing: 10) {
                            Image(systemName: "wifi").foregroundStyle(.secondary)
                            VStack(alignment: .leading, spacing: 1) {
                                Text(n)
                                Text(summary(of: n)).font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Button("Remove…") { ui.removingNetwork = n }
                        }
                    }
                    HStack {
                        TextField("Network name", text: $ui.ssidDraft, prompt: Text("Add a network"))
                            .labelsHidden()
                            .textFieldStyle(.roundedBorder)
                            .onSubmit(addSSID)
                        Menu("Nearby") {
                            if e.nearby.isEmpty { Text("No networks found") }
                            ForEach(e.nearby) { n in
                                Button(n.count > 1 ? "\(n.ssid)  (\(n.count) access points)" : n.ssid) { ui.ssidDraft = n.ssid }
                            }
                            Divider()
                            Button("Scan Again") { e.requestScan() }
                        }
                        .fixedSize()
                        Button("Add", action: addSSID)
                            .disabled(draftSSID.isEmpty || e.networks.contains(draftSSID))
                    }
                    if let cur = e.currentSSID, !e.networks.contains(cur) {
                        Button("Add “\(cur)”, the Network You're On") { e.addNetwork(cur) }
                    }
                } header: {
                    Text("Networks")
                } footer: {
                    Text("BSSIDLock only acts while you're connected to one of these, and follows you from one to the next. Each network has its own favorites.")
                        .font(.caption).foregroundStyle(.secondary)
                }

                Section {
                    Picker("Safety check", selection: intervalChoice) {
                        ForEach(Self.intervalPresets, id: \.self) { Text("Every \($0) seconds").tag($0) }
                        Divider()
                        Text("Custom…").tag(Self.customTag)
                    }
                    .pickerStyle(.menu)
                    if intervalChoice.wrappedValue == Self.customTag {
                        HStack {
                            Text("Check every")
                            Spacer()
                            TextField("Seconds", text: $ui.intervalText, prompt: Text("3–120"))
                                .labelsHidden()
                                .textFieldStyle(.roundedBorder)
                                .multilineTextAlignment(.trailing)
                                .frame(width: 70)
                            Text("seconds")
                        }
                        if !customIntervalValid {
                            Text("Enter a whole number from 3 to 120.").font(.caption).foregroundStyle(.red)
                        }
                    }
                    Picker("When the signal is weak", selection: $e.weakThreshold) {
                        Text("Stay locked").tag(0)
                        Divider()
                        ForEach([-70, -75, -80], id: \.self) { Text("Let go below \($0) dBm").tag($0) }
                    }
                    .pickerStyle(.menu)
                    Text("“Let go” moves you to a stronger favorite (or lets macOS choose) while your chosen access point is weak, and comes back once it recovers.")
                        .font(.caption).foregroundStyle(.secondary)
                    Toggle("Open at login", isOn: Binding(get: { e.launchAtLogin }, set: { e.setLaunchAtLogin($0) }))
                    Toggle("Notify me when the Wi-Fi network or access point changes", isOn: $e.notifyChanges)
                } header: {
                    Text("Behavior")
                } footer: {
                    Text("Wi-Fi changes are handled instantly. The safety check is only a backup.")
                        .font(.caption).foregroundStyle(.secondary)
                }

                Section {
                    HStack {
                        TextField("Access point address", text: $ui.manualBSSID, prompt: Text("aa:bb:cc:dd:ee:ff"))
                            .labelsHidden()
                            .textFieldStyle(.roundedBorder)
                            .font(.body.monospaced())
                            .onSubmit(addManual)
                        Button("Add", action: addManual)
                            .disabled(!isValidBSSID(ui.manualBSSID))
                    }
                    .disabled(e.ssid.isEmpty)
                    if !ui.manualBSSID.isEmpty && !isValidBSSID(ui.manualBSSID) {
                        Text("An address is 6 pairs of characters (0–9, a–f), like aa:bb:cc:dd:ee:ff.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                } header: {
                    Text(e.ssid.isEmpty ? "Add a favorite by address" : "Add a favorite to “\(e.ssid)” by address")
                } footer: {
                    Text("Useful for an access point that's out of range right now.")
                        .font(.caption).foregroundStyle(.secondary)
                }

                Section {
                    HStack {
                        Group {
                            if ui.showPassword {
                                TextField("Password", text: $ui.pwDraft, prompt: Text("Wi-Fi password"))
                            } else {
                                SecureField("Password", text: $ui.pwDraft, prompt: Text("Wi-Fi password"))
                            }
                        }
                        .labelsHidden()
                        .textFieldStyle(.roundedBorder)
                        .onSubmit { e.savePassword(ui.pwDraft) }
                        Button { ui.showPassword.toggle() } label: {
                            Image(systemName: ui.showPassword ? "eye.slash" : "eye")
                        }
                        .buttonStyle(.borderless)
                        .help(ui.showPassword ? "Hide password" : "Show password")
                        Button("Save") { e.savePassword(ui.pwDraft) }
                            .disabled(ui.pwDraft == e.password || e.ssid.isEmpty)
                    }
                } header: {
                    Text(e.ssid.isEmpty ? "Wi-Fi password (optional)" : "Wi-Fi password for “\(e.ssid)” (optional)")
                } footer: {
                    Text("Only needed if switching access points fails without it. Stored in your Keychain.")
                        .font(.caption).foregroundStyle(.secondary)
                }

                Section {
                    Toggle("Keep BSSIDLock up to date automatically", isOn: $updater.autoUpdate)
                    HStack {
                        Button(updater.installing ? "Restarting…" : updater.available.map { "Install Version \($0)" } ?? "Check Now") {
                            Task { await updater.check(userInitiated: true) }
                        }
                        .disabled(updater.busy || updater.installing)
                        if updater.busy { ProgressView().controlSize(.small) }
                        Text(updater.status).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    }
                } header: {
                    Text("Updates")
                } footer: {
                    Text("Version \(Updater.currentVersion). Updates come only from this project's GitHub releases and are verified before installing.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            .formStyle(.grouped)

            Divider()
            HStack {
                Spacer()
                Button("Done") { ui.showSettings = false }
                    .keyboardShortcut(.defaultAction)
            }
            .padding(12)
        }
        .frame(width: 560, height: 700)
        .alert("Stop managing “\(ui.removingNetwork ?? "")”?", isPresented: removeBinding) {
            Button("Remove", role: .destructive) {
                if let n = ui.removingNetwork { e.removeNetwork(n) }
                ui.removingNetwork = nil
            }
            Button("Cancel", role: .cancel) { ui.removingNetwork = nil }
        } message: {
            Text("BSSIDLock stops acting on it and forgets its favorites and saved password.")
        }
        .onAppear {
            ui.ssidDraft = ""
            ui.pwDraft = e.password
            ui.intervalText = String(Int(e.interval))
            ui.customInterval = !Self.intervalPresets.contains(Int(e.interval))
            if e.nearby.isEmpty { Task { await e.scanNetworks() } }
        }
        .onReceive(e.$password) { ui.pwDraft = $0 }
        // Only hex digits and separators in the address field.
        .onReceive(ui.$manualBSSID) { value in
            let cleaned = String(value.lowercased().filter { $0.isHexDigit || $0 == ":" || $0 == "-" }.prefix(17))
            if cleaned != value { ui.manualBSSID = cleaned }
        }
        // Custom interval: digits only; apply as soon as it's a valid number.
        .onReceive(ui.$intervalText) { value in
            let digits = String(value.filter(\.isNumber).prefix(3))
            if digits != value { ui.intervalText = digits; return }
            if let n = Int(digits), (3...120).contains(n), ui.customInterval { e.interval = Double(n) }
        }
    }

    private var intervalChoice: Binding<Int> {
        Binding(
            get: { ui.customInterval ? Self.customTag : Int(e.interval) },
            set: { value in
                if value == Self.customTag {
                    ui.customInterval = true
                    ui.intervalText = String(Int(e.interval))
                } else {
                    ui.customInterval = false
                    e.interval = Double(value)
                }
            })
    }

    private var customIntervalValid: Bool {
        guard let n = Int(ui.intervalText) else { return false }
        return (3...120).contains(n)
    }

    private var draftSSID: String { ui.ssidDraft.trimmingCharacters(in: .whitespaces) }

    private func addSSID() {
        guard !draftSSID.isEmpty, !e.networks.contains(draftSSID) else { return }
        e.addNetwork(draftSSID)
        ui.ssidDraft = ""
    }

    private var removeBinding: Binding<Bool> {
        Binding(get: { ui.removingNetwork != nil }, set: { if !$0 { ui.removingNetwork = nil } })
    }

    /// "Connected · 3 favorites · locked to Living room"
    private func summary(of n: String) -> String {
        let s = e.setup(for: n)
        var parts: [String] = []
        if n == e.currentSSID { parts.append("Connected") }
        parts.append(s.favorites.isEmpty ? "No favorites yet" : s.favorites.count == 1 ? "1 favorite" : "\(s.favorites.count) favorites")
        if let t = s.target { parts.append("locked to \(e.label(for: t))") }
        return parts.joined(separator: " · ")
    }

    private func addManual() {
        if e.addFavorite(ui.manualBSSID) { ui.manualBSSID = "" }
    }
}

struct ActivitySheet: View {
    @EnvironmentObject var e: Engine
    @EnvironmentObject var ui: UIState
    @ObservedObject var activity: ActivityLog

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Activity").font(.headline)
                Spacer()
                Button("Show in Finder") {
                    NSWorkspace.shared.activateFileViewerSelecting([ActivityLog.fileURL])
                }
                .help(ActivityLog.fileURL.path)
                Button("Copy All") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(activity.lines.joined(separator: "\n"), forType: .string)
                }
                .disabled(activity.lines.isEmpty)
                Button("Done") { ui.showActivity = false }
                    .keyboardShortcut(.defaultAction)
            }
            if activity.lines.isEmpty {
                Text("Nothing yet.").foregroundStyle(.secondary)
                Spacer()
            } else {
                List(Array(activity.lines.reversed().enumerated()), id: \.offset) { _, line in
                    Text(line)
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                }
                .listStyle(.inset(alternatesRowBackgrounds: true))
            }
        }
        .padding(16)
        .frame(width: 600, height: 420)
    }
}
