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
                    HStack {
                        TextField("Network name", text: $ui.ssidDraft, prompt: Text("Network name"))
                            .labelsHidden()
                            .textFieldStyle(.roundedBorder)
                            .onSubmit(applySSID)
                        Menu("Nearby") {
                            if e.nearby.isEmpty { Text("No networks found") }
                            ForEach(e.nearby) { n in
                                Button(n.count > 1 ? "\(n.ssid)  (\(n.count) access points)" : n.ssid) { ui.ssidDraft = n.ssid }
                            }
                            Divider()
                            Button("Scan Again") { Task { await e.scanNetworks() } }
                        }
                        .fixedSize()
                    }
                    HStack {
                        Button("Use Current Network") {
                            ui.ssidDraft = e.currentSSID ?? ""
                            applySSID()
                        }
                        .disabled(e.currentSSID == nil || e.currentSSID == e.ssid)
                        Spacer()
                        Button("Apply", action: applySSID)
                            .disabled(draftSSID.isEmpty || draftSSID == e.ssid)
                    }
                } header: {
                    Text("Network")
                } footer: {
                    Text("BSSIDLock only acts while you're connected to this network.")
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
                    if !ui.manualBSSID.isEmpty && !isValidBSSID(ui.manualBSSID) {
                        Text("An address is 6 pairs of characters (0–9, a–f), like aa:bb:cc:dd:ee:ff.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                } header: {
                    Text("Add a favorite by address")
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
                    Text("Wi-Fi password (optional)")
                } footer: {
                    Text("Only needed if switching access points fails without it. Stored in your Keychain.")
                        .font(.caption).foregroundStyle(.secondary)
                }

                Section {
                    Toggle("Keep BSSIDLock up to date automatically", isOn: $updater.autoUpdate)
                    HStack {
                        Button(updater.available.map { "Install Version \($0)" } ?? "Check Now") {
                            Task { await updater.check(userInitiated: true) }
                        }
                        .disabled(updater.busy)
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
        .frame(width: 540, height: 660)
        .onAppear {
            ui.ssidDraft = e.ssid
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

    private func applySSID() {
        guard !draftSSID.isEmpty else { return }
        e.ssid = draftSSID
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
