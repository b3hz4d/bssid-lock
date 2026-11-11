// BSSIDLock: keep your Mac connected to the access point(s) you choose.
// https://github.com/b3hz4d/bssid-lock
import SwiftUI
import CoreWLAN
import CoreLocation
import Security
import ServiceManagement

// MARK: - Helpers

/// Normalize any BSSID spelling ("AA-BB-CC-DD-E-FF", "aa:bb:…") to "aa:bb:cc:dd:0e:ff".
func canonical(_ s: String) -> String {
    s.trimmingCharacters(in: .whitespaces).lowercased()
        .replacingOccurrences(of: "-", with: ":")
        .split(separator: ":", omittingEmptySubsequences: false)
        .map { $0.count == 1 ? "0\($0)" : String($0) }
        .joined(separator: ":")
}

func isValidBSSID(_ s: String) -> Bool {
    let parts = canonical(s).split(separator: ":", omittingEmptySubsequences: false)
    return parts.count == 6 && parts.allSatisfy { $0.count == 2 && $0.allSatisfy(\.isHexDigit) }
}

func bringToFront() {
    if #available(macOS 14, *) { NSApp.activate() } else { NSApp.activate(ignoringOtherApps: true) }
}

/// Wi-Fi passwords, stored in the login Keychain, one item per network name.
enum Keychain {
    static var service: String { (Bundle.main.bundleIdentifier ?? "BSSIDLock") + ".wifi" }
    private static func base(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }
    static func get(_ account: String) -> String? {
        guard !account.isEmpty else { return nil }
        var q = base(account)
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let d = out as? Data else { return nil }
        return String(data: d, encoding: .utf8)
    }
    @discardableResult
    static func set(_ value: String, for account: String) -> Bool {
        guard !account.isEmpty else { return false }
        SecItemDelete(base(account) as CFDictionary)
        guard !value.isEmpty else { return true }
        var add = base(account)
        add[kSecValueData as String] = Data(value.utf8)
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess
    }
}

struct AP: Identifiable, Hashable, Sendable {
    let bssid: String
    let rssi: Int
    let channel: Int
    let band: String
    var id: String { bssid }
}

struct WorkResult: Sendable {
    var aps: [AP]
    var scanned: Bool      // false if the scan itself failed or BSSIDs were hidden
    var message: String
    var ok: Bool
}

// MARK: - Engine

@MainActor
final class Engine: NSObject, ObservableObject, CLLocationManagerDelegate {
    private let defaults = UserDefaults.standard

    // Settings (persisted)
    /// The network (SSID) to manage. The app never touches any other network.
    @Published var ssid: String {
        didSet {
            guard ssid != oldValue else { return }
            defaults.set(ssid, forKey: "ssid")
            password = Keychain.get(ssid) ?? ""
            visible = []
            lastScan = nil
            retryAfter = .distantPast
            Task { await self.scan(); await self.tick() }
        }
    }
    @Published private(set) var password: String
    /// Allowed BSSIDs in priority order (first = preferred).
    @Published var allowed: [String] {
        didSet {
            defaults.set(allowed, forKey: "allowed")
            if let t = target, !allowed.contains(t) { target = nil }
        }
    }
    /// nil = any allowed BSSID (priority order); otherwise lock to exactly this BSSID.
    @Published var target: String? {
        didSet { defaults.set(target, forKey: "target"); retryAfter = .distantPast; kick() }
    }
    @Published var lockEnabled: Bool {
        didSet { defaults.set(lockEnabled, forKey: "lockEnabled"); retryAfter = .distantPast; kick() }
    }
    @Published var interval: Double { didSet { defaults.set(interval, forKey: "interval") } }

    // Live state
    @Published var currentSSID: String?
    @Published var currentBSSID: String?
    @Published var currentRSSI = 0
    @Published var visible: [AP] = []
    @Published var lastScan: Date?
    @Published var status = "Starting…"
    @Published var isLocked = false
    @Published var log: [String] = []
    @Published var locAuth: CLAuthorizationStatus = .notDetermined
    @Published var busy = false
    @Published var launchAtLogin = false

    private let lm = CLLocationManager()
    private var retryAfter = Date.distantPast
    private static let retryBackoff: TimeInterval = 60
    private static let timeFmt: DateFormatter = {
        let f = DateFormatter(); f.dateFormat = "HH:mm:ss"; return f
    }()

    override init() {
        let d = UserDefaults.standard
        let s = d.string(forKey: "ssid") ?? ""
        ssid = s
        let a = (d.stringArray(forKey: "allowed") ?? []).filter(isValidBSSID).map(canonical)
        allowed = a
        target = d.string(forKey: "target").map(canonical).flatMap { a.contains($0) ? $0 : nil }
        lockEnabled = d.object(forKey: "lockEnabled") as? Bool ?? true
        interval = d.object(forKey: "interval") as? Double ?? 10
        password = Keychain.get(s) ?? ""
        super.init()

        launchAtLogin = SMAppService.mainApp.status == .enabled
        lm.delegate = self
        locAuth = lm.authorizationStatus
        lm.requestWhenInUseAuthorization()
        lm.requestLocation()   // one-shot; makes sure the permission prompt appears
        Task { await loop() }
    }

    // MARK: Location

    nonisolated func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
        let s = manager.authorizationStatus
        Task { @MainActor in
            let wasOK = self.locationOK
            self.locAuth = s
            if !wasOK && self.locationOK {
                self.addLog("Location access granted")
                self.refresh()
                if !self.adoptCurrentNetworkIfUnset() { await self.scan() }
                await self.tick()
            }
        }
    }
    nonisolated func locationManager(_ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]) {}
    nonisolated func locationManager(_ manager: CLLocationManager, didFailWithError error: Error) {}

    var locationOK: Bool { locAuth == .authorizedAlways || locAuth == .authorized }

    // MARK: Loop

    func addLog(_ s: String) {
        log.append("\(Self.timeFmt.string(from: Date()))  \(s)")
        if log.count > 300 { log.removeFirst(log.count - 300) }
    }

    func refresh() {
        let i = CWWiFiClient.shared().interface()
        currentSSID = i?.ssid()
        currentBSSID = i?.bssid().map(canonical)
        currentRSSI = i?.rssiValue() ?? 0
    }

    /// First run: if no network is configured yet, use the one the Mac is on now.
    /// Returns true if it set one (which also triggers a scan).
    @discardableResult
    private func adoptCurrentNetworkIfUnset() -> Bool {
        guard ssid.isEmpty, let cur = currentSSID, !cur.isEmpty else { return false }
        addLog("Managing “\(cur)” (your current Wi-Fi). You can change this in Settings.")
        ssid = cur
        return true
    }

    private func kick() { Task { await tick() } }

    private func loop() async {
        refresh()
        if locationOK && !adoptCurrentNetworkIfUnset() { await scan() }
        while true {
            await tick()
            try? await Task.sleep(for: .seconds(max(3, interval)))
        }
    }

    /// The BSSIDs the lock currently accepts.
    var wanted: [String] { target.map { [$0] } ?? allowed }

    func tick() async {
        refresh()
        guard !busy else { return }
        isLocked = false
        guard lockEnabled else { status = "Lock is off"; return }
        guard !ssid.isEmpty else {
            status = locationOK ? "Choose a network in Settings" : "Location access needed to read Wi-Fi details"
            return
        }
        guard let cur = currentSSID else {
            status = locationOK ? "Not connected to Wi-Fi" : "Location access needed to read the network"
            return
        }
        guard cur == ssid else { status = "On “\(cur)”: idle (only acts on “\(ssid)”)"; return }
        guard !wanted.isEmpty else { status = "Allow at least one access point below"; return }
        guard let b = currentBSSID else { status = "Location access needed to read the BSSID"; return }
        if wanted.contains(b) { isLocked = true; status = "Locked to \(b)"; return }

        let wait = retryAfter.timeIntervalSinceNow
        if wait > 0 { status = "On \(b) (not allowed): retry in \(Int(wait.rounded(.up))) s"; return }
        status = "On \(b) (not allowed): switching…"
        if !(await connect(order: wanted)) { retryAfter = Date().addingTimeInterval(Self.retryBackoff) }
    }

    // MARK: Actions

    @discardableResult
    func connect(order: [String]) async -> Bool {
        guard !busy, !ssid.isEmpty else { return false }
        busy = true
        defer { busy = false }
        let (s, pw) = (ssid, password)
        let r = await Task.detached { Engine.doWork(ssid: s, order: order, password: pw, associate: true) }.value
        if r.scanned { visible = r.aps; lastScan = Date() }
        addLog(r.message)
        refresh()
        if let b = currentBSSID, wanted.contains(b) { isLocked = true; status = "Locked to \(b)" }
        return r.ok
    }

    func scan() async {
        guard !busy, !ssid.isEmpty else { return }
        busy = true
        defer { busy = false }
        let s = ssid
        let r = await Task.detached { Engine.doWork(ssid: s, order: [], password: "", associate: false) }.value
        if r.scanned { visible = r.aps; lastScan = Date() }
        addLog(r.message)
        refresh()
    }

    /// Lock to one BSSID (adds it to the allowed list if needed) and switch to it now.
    func lockTo(_ b: String?) {
        if let b, !allowed.contains(b) { allowed.append(b) }
        if !lockEnabled { lockEnabled = true }
        target = b
    }

    func addAllowed(_ raw: String) {
        guard isValidBSSID(raw) else { addLog("Invalid BSSID: \(raw)"); return }
        let b = canonical(raw)
        if !allowed.contains(b) { allowed.append(b) }
    }

    func savePassword(_ p: String) {
        guard !ssid.isEmpty else { addLog("Set the network name first"); return }
        password = p
        addLog(Keychain.set(p, for: ssid) ? "Password for “\(ssid)” saved to Keychain" : "Could not save password to Keychain")
    }

    func setLaunchAtLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch { addLog("Launch at login failed: \(error.localizedDescription)") }
        launchAtLogin = SMAppService.mainApp.status == .enabled
    }

    // MARK: CoreWLAN (runs off the main thread; scans take a few seconds)

    nonisolated static func doWork(ssid: String, order: [String], password: String, associate: Bool) -> WorkResult {
        guard let iface = CWWiFiClient.shared().interface() else {
            return .init(aps: [], scanned: false, message: "No Wi-Fi interface", ok: false)
        }
        let nets: Set<CWNetwork>
        do { nets = try iface.scanForNetworks(withSSID: ssid.data(using: .utf8)) }
        catch { return .init(aps: [], scanned: false, message: "Scan failed: \(error.localizedDescription)", ok: false) }

        let withB = nets.filter { $0.bssid != nil }
        if withB.isEmpty {
            if nets.isEmpty { return .init(aps: [], scanned: true, message: "Scan: no “\(ssid)” access points in range", ok: false) }
            return .init(aps: [], scanned: false, message: "Scan: BSSIDs hidden, Location access needed", ok: false)
        }
        // One row per BSSID (a scan can list the same AP twice); keep the strongest reading.
        var best: [String: AP] = [:]
        for n in withB {
            let ch = n.wlanChannel
            let band: String
            switch ch?.channelBand.rawValue ?? 0 {
            case 1: band = "2.4 GHz"
            case 2: band = "5 GHz"
            case 3: band = "6 GHz"
            default: band = "?"
            }
            let ap = AP(bssid: canonical(n.bssid!), rssi: n.rssiValue, channel: ch?.channelNumber ?? 0, band: band)
            if let old = best[ap.bssid], old.rssi >= ap.rssi { continue }
            best[ap.bssid] = ap
        }
        let aps = best.values.sorted { $0.rssi > $1.rssi }
        guard associate else {
            return .init(aps: aps, scanned: true, message: "Scan: found \(aps.count) access point(s)", ok: true)
        }

        var lastErr = ""
        for w in order {
            guard let net = withB.filter({ canonical($0.bssid!) == w }).max(by: { $0.rssiValue < $1.rssiValue }) else { continue }
            do {
                try iface.associate(to: net, password: password.isEmpty ? nil : password)
                return .init(aps: aps, scanned: true, message: "Connected to \(w) (\(net.rssiValue) dBm)", ok: true)
            } catch {
                lastErr = "Connect to \(w) failed: \(error.localizedDescription)"
            }
        }
        let msg = lastErr.isEmpty
            ? "None of the wanted access points are in range (seen: \(aps.map(\.bssid).joined(separator: ", ")))"
            : lastErr
        return .init(aps: aps, scanned: true, message: msg, ok: false)
    }
}

// MARK: - Views

struct SignalView: View {
    let rssi: Int
    var body: some View {
        HStack(spacing: 4) {
            Image(systemName: "wifi", variableValue: min(1, max(0, Double(rssi + 90) / 50)))
            Text("\(rssi) dBm").font(.caption).monospacedDigit().foregroundStyle(.secondary)
        }
    }
}

/// View-local form state. (An ObservableObject instead of @State, because @State is a macro in
/// recent SDKs and the Command Line Tools ship without the SwiftUI macro plugin.)
final class FormState: ObservableObject {
    @Published var manual = ""
    @Published var ssidDraft = ""
    @Published var pwDraft = ""
    @Published var showPassword = false
}

struct ContentView: View {
    @EnvironmentObject var e: Engine
    @StateObject private var form = FormState()

    var body: some View {
        List {
            statusSection
            if !e.locationOK { locationSection }
            availableSection
            allowedSection
            connectionSection
            settingsSection
            logSection
        }
        .listStyle(.inset)
        .frame(minWidth: 620, minHeight: 700)
        .onReceive(e.$ssid) { form.ssidDraft = $0 }
        .onReceive(e.$password) { form.pwDraft = $0 }
    }

    // Status + lock target picker
    private var statusSection: some View {
        Section {
            HStack(spacing: 12) {
                Image(systemName: e.isLocked ? "lock.fill" : (e.lockEnabled ? "lock.trianglebadge.exclamationmark" : "lock.open"))
                    .font(.title)
                    .foregroundStyle(e.isLocked ? .green : (e.lockEnabled ? .orange : .secondary))
                    .frame(width: 36)
                VStack(alignment: .leading, spacing: 2) {
                    Text(e.status).font(.headline)
                    Text(e.currentSSID.map { "Wi-Fi: \($0)" } ?? "No Wi-Fi connection")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                if e.busy { ProgressView().controlSize(.small) }
                Toggle("Lock", isOn: $e.lockEnabled).toggleStyle(.switch)
            }
            .padding(.vertical, 4)
            Picker("Lock to", selection: $e.target) {
                Text("Any allowed access point (priority order)").tag(String?.none)
                ForEach(e.allowed, id: \.self) { b in
                    Text(b).font(.system(.body, design: .monospaced)).tag(String?.some(b))
                }
            }
        }
    }

    private var locationSection: some View {
        Section {
            HStack {
                Image(systemName: "location.slash").foregroundStyle(.orange)
                Text("Location access is required for macOS to reveal BSSIDs.").font(.callout)
                Spacer()
                Button("Open Settings") {
                    NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_LocationServices")!)
                }
            }
        }
    }

    // Every access point broadcasting the network name, not just the allowed ones
    private var availableSection: some View {
        Section {
            if e.ssid.isEmpty {
                Text("Set the network name in Settings below, or connect to it and press “Use current”.")
                    .foregroundStyle(.secondary)
            } else if e.visible.isEmpty {
                Text(e.locationOK ? "No results yet. Press Scan." : "Grant Location access, then press Scan.")
                    .foregroundStyle(.secondary)
            }
            ForEach(e.visible) { ap in
                let isCurrent = ap.bssid == e.currentBSSID
                let rank = e.allowed.firstIndex(of: ap.bssid)
                HStack(spacing: 10) {
                    Image(systemName: isCurrent ? "checkmark.circle.fill" : "circle")
                        .foregroundStyle(isCurrent ? .green : .secondary)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(ap.bssid).font(.system(.body, design: .monospaced))
                        Text(
                            ["ch \(ap.channel)", ap.band,
                             rank.map { "allowed #\($0 + 1)" } ?? "not allowed",
                             isCurrent ? "connected" : nil].compactMap { $0 }.joined(separator: " · ")
                        )
                        .font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    SignalView(rssi: ap.rssi)
                    if rank == nil { Button("Allow") { e.addAllowed(ap.bssid) } }
                    if e.target == ap.bssid {
                        Label("Locked", systemImage: "lock.fill").foregroundStyle(.green)
                    } else {
                        Button("Lock here") { e.lockTo(ap.bssid) }.disabled(e.busy)
                    }
                }
            }
        } header: {
            HStack {
                Text(e.ssid.isEmpty ? "Available access points" : "Available access points for “\(e.ssid)”")
                Spacer()
                if let t = e.lastScan {
                    Text("scanned \(t, style: .relative) ago").font(.caption).foregroundStyle(.secondary)
                }
                Button { Task { await e.scan() } } label: { Label("Scan", systemImage: "arrow.clockwise") }
                    .disabled(e.busy || e.ssid.isEmpty)
            }
        }
    }

    private var allowedSection: some View {
        Section {
            if e.allowed.isEmpty {
                Text("None yet. Allow access points from the list above, or add a BSSID manually.")
                    .foregroundStyle(.secondary)
            }
            ForEach(Array(e.allowed.enumerated()), id: \.element) { idx, b in
                HStack(spacing: 10) {
                    Image(systemName: "line.3.horizontal").foregroundStyle(.tertiary)
                    Text("\(idx + 1)").monospacedDigit().foregroundStyle(.secondary)
                    Text(b).font(.system(.body, design: .monospaced))
                    if b == e.currentBSSID {
                        Image(systemName: "checkmark.circle.fill").foregroundStyle(.green).help("Connected")
                    }
                    Spacer()
                    if let ap = e.visible.first(where: { $0.bssid == b }) {
                        SignalView(rssi: ap.rssi)
                    } else {
                        Text("not in range").font(.caption).foregroundStyle(.secondary)
                    }
                    if e.target == b {
                        Label("Locked", systemImage: "lock.fill").foregroundStyle(.green)
                    } else {
                        Button("Lock here") { e.lockTo(b) }.disabled(e.busy)
                    }
                    Button { e.allowed.removeAll { $0 == b } } label: { Image(systemName: "trash") }
                        .buttonStyle(.borderless).help("Remove")
                }
            }
            .onMove { e.allowed.move(fromOffsets: $0, toOffset: $1) }
            HStack {
                TextField("Add BSSID manually (aa:bb:cc:dd:ee:ff)", text: $form.manual)
                    .font(.system(.body, design: .monospaced))
                    .onSubmit { addManual() }
                Button("Add") { addManual() }.disabled(!isValidBSSID(form.manual))
            }
        } header: {
            Text("Allowed access points: drag to reorder; #1 is preferred in “Any allowed” mode")
        }
    }

    private var connectionSection: some View {
        Section("Current connection") {
            LabeledContent("Network", value: e.currentSSID ?? "—")
            LabeledContent("BSSID") {
                Text(e.currentBSSID ?? "—").font(.system(.body, design: .monospaced)).textSelection(.enabled)
            }
            LabeledContent("Signal") {
                if e.currentBSSID != nil { SignalView(rssi: e.currentRSSI) } else { Text("—") }
            }
        }
    }

    private var settingsSection: some View {
        Section("Settings") {
            HStack {
                TextField("Network name (SSID)", text: $form.ssidDraft)
                    .onSubmit { applySSID() }
                Button("Use current") { form.ssidDraft = e.currentSSID ?? ""; applySSID() }
                    .disabled(e.currentSSID == nil || e.currentSSID == e.ssid)
                Button("Apply") { applySSID() }
                    .disabled(form.ssidDraft.trimmingCharacters(in: .whitespaces) == e.ssid)
            }
            HStack {
                Group {
                    if form.showPassword { TextField("Wi-Fi password (optional)", text: $form.pwDraft) }
                    else { SecureField("Wi-Fi password (optional, stored in Keychain)", text: $form.pwDraft) }
                }
                .onSubmit { e.savePassword(form.pwDraft) }
                Button { form.showPassword.toggle() } label: { Image(systemName: form.showPassword ? "eye.slash" : "eye") }
                    .buttonStyle(.borderless)
                Button("Save") { e.savePassword(form.pwDraft) }
                    .disabled(form.pwDraft == e.password || e.ssid.isEmpty)
            }
            Stepper("Check every \(Int(e.interval)) s", value: $e.interval, in: 3...120, step: 1)
            Toggle("Launch at login", isOn: Binding(get: { e.launchAtLogin }, set: { e.setLaunchAtLogin($0) }))
        }
    }

    private var logSection: some View {
        Section {
            DisclosureGroup("Log") {
                ForEach(Array(e.log.suffix(60).enumerated().reversed()), id: \.offset) { _, line in
                    Text(line).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                }
            }
        }
    }

    private func applySSID() {
        e.ssid = form.ssidDraft.trimmingCharacters(in: .whitespaces)
    }

    private func addManual() {
        guard isValidBSSID(form.manual) else { return }
        e.addAllowed(form.manual)
        form.manual = ""
    }
}

struct MenuView: View {
    @EnvironmentObject var e: Engine
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Text(e.status)
        if let b = e.currentBSSID { Text("Current: \(b)") }
        Divider()
        Toggle("Lock enabled", isOn: $e.lockEnabled)
        Picker("Lock to", selection: $e.target) {
            Text("Any allowed (priority order)").tag(String?.none)
            ForEach(e.allowed, id: \.self) { b in Text(b).tag(String?.some(b)) }
        }
        Button("Scan") { Task { await e.scan() } }.disabled(e.busy || e.ssid.isEmpty)
        Divider()
        Button("Open BSSIDLock…") { openWindow(id: "main"); bringToFront() }
        Button("Quit BSSIDLock") { NSApp.terminate(nil) }.keyboardShortcut("q")
    }
}

@main
struct BSSIDLockApp: App {
    @StateObject private var engine = Engine()

    var body: some Scene {
        Window("BSSIDLock", id: "main") {
            ContentView().environmentObject(engine)
        }
        MenuBarExtra {
            MenuView().environmentObject(engine)
        } label: {
            Image(systemName: engine.isLocked ? "lock.fill" : "lock.open")
        }
    }
}
