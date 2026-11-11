// Settings and Activity sheets.
import SwiftUI

struct SettingsSheet: View {
    @EnvironmentObject var e: Engine
    @EnvironmentObject var ui: UIState

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    HStack {
                        TextField("Network name", text: $ui.ssidDraft)
                            .onSubmit(applySSID)
                        Menu("Nearby") {
                            if e.nearby.isEmpty { Text("No networks found") }
                            ForEach(e.nearby) { n in
                                Button(n.count > 1 ? "\(n.ssid)  (\(n.count) access points)" : n.ssid) {
                                    ui.ssidDraft = n.ssid
                                }
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
                    HStack {
                        Group {
                            if ui.showPassword {
                                TextField("Password", text: $ui.pwDraft)
                            } else {
                                SecureField("Password", text: $ui.pwDraft)
                            }
                        }
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

                Section("Behavior") {
                    Stepper("Check every \(Int(e.interval)) seconds", value: $e.interval, in: 3...120, step: 1)
                    Toggle("Open at login", isOn: Binding(get: { e.launchAtLogin }, set: { e.setLaunchAtLogin($0) }))
                }

                Section {
                    HStack {
                        TextField("aa:bb:cc:dd:ee:ff", text: $ui.manualBSSID)
                            .font(.body.monospaced())
                            .onSubmit(addManual)
                        Button("Add", action: addManual)
                            .disabled(!isValidBSSID(ui.manualBSSID))
                    }
                } header: {
                    Text("Add a favorite by address")
                } footer: {
                    Text("Useful for an access point that's out of range right now.")
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
        .frame(width: 500, height: 560)
        .onAppear {
            ui.ssidDraft = e.ssid
            ui.pwDraft = e.password
            if e.nearby.isEmpty { Task { await e.scanNetworks() } }
        }
        .onReceive(e.$password) { ui.pwDraft = $0 }
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

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Activity").font(.headline)
                Spacer()
                Button("Copy All") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(e.log.joined(separator: "\n"), forType: .string)
                }
                .disabled(e.log.isEmpty)
                Button("Done") { ui.showActivity = false }
                    .keyboardShortcut(.defaultAction)
            }
            if e.log.isEmpty {
                Text("Nothing yet.").foregroundStyle(.secondary)
                Spacer()
            } else {
                List(Array(e.log.reversed().enumerated()), id: \.offset) { _, line in
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
