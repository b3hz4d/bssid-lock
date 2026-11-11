// BSSIDLock for macOS: Wi-Fi logic and app state.
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

// MARK: - Models

struct AP: Identifiable, Hashable, Sendable {
    let bssid: String
    let rssi: Int
    let channel: Int
    let band: String
    var id: String { bssid }
}

/// A row in the access point list: a favorite (maybe out of range) or a nearby access point.
struct APItem: Identifiable, Hashable {
    let bssid: String
    let ap: AP?
    let rank: Int?          // position in favorites, nil if not a favorite
    var id: String { bssid }
}

struct NetworkSummary: Identifiable, Hashable, Sendable {
    let ssid: String
    let rssi: Int
    let count: Int
    var id: String { ssid }
}

struct WorkResult: Sendable {
    var aps: [AP]
    var scanned: Bool      // false if the scan itself failed or BSSIDs were hidden
    var message: String
    var ok: Bool
}

enum SignalQuality {
    case excellent, good, fair, weak

    init(rssi: Int) {
        if rssi >= -55 { self = .excellent }
        else if rssi >= -67 { self = .good }
        else if rssi >= -75 { self = .fair }
        else { self = .weak }
    }
    var label: String {
        switch self {
        case .excellent: return "Excellent"
        case .good: return "Good"
        case .fair: return "Fair"
        case .weak: return "Weak"
        }
    }
    var bars: Int {
        switch self {
        case .excellent: return 4
        case .good: return 3
        case .fair: return 2
        case .weak: return 1
        }
    }
    var color: Color {
        switch self {
        case .excellent, .good: return .green
        case .fair: return .orange
        case .weak: return .red
        }
    }
}

enum LockState {
    case locked, working, attention, idle, off

    var color: Color {
        switch self {
        case .locked: return .green
        case .working: return .blue
        case .attention: return .orange
        case .idle, .off: return .gray
        }
    }
    var icon: String {
        switch self {
        case .locked: return "lock.fill"
        case .working: return "arrow.triangle.2.circlepath"
        case .attention: return "exclamationmark.triangle.fill"
        case .idle: return "pause.fill"
        case .off: return "lock.open.fill"
        }
    }
}

/// Recently scanned networks, so switching right after a scan doesn't need another 2–4 s scan.
/// Accessed only from the detached CoreWLAN work, guarded by a lock.
final class NetworkCache: @unchecked Sendable {
    static let shared = NetworkCache()
    private let lock = NSLock()
    private var entries: [String: (net: CWNetwork, ssid: String, time: Date)] = [:]

    func store(_ nets: [CWNetwork], ssid: String) {
        lock.lock(); defer { lock.unlock() }
        let now = Date()
        for n in nets { if let b = n.bssid { entries[canonical(b)] = (n, ssid, now) } }
    }

    func lookup(bssid: String, ssid: String, maxAge: TimeInterval) -> CWNetwork? {
        lock.lock(); defer { lock.unlock() }
        guard let e = entries[bssid], e.ssid == ssid, Date().timeIntervalSince(e.time) <= maxAge else { return nil }
        return e.net
    }
}

// MARK: - Engine

@MainActor
final class Engine: NSObject, ObservableObject, CLLocationManagerDelegate, CWEventDelegate {
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
            if !ssid.isEmpty { addLog("Now managing “\(ssid)”") }
            Task { await self.scan(); await self.tick() }
        }
    }
    @Published private(set) var password: String
    /// Favorite BSSIDs in priority order (first = preferred).
    @Published var favorites: [String] {
        didSet {
            defaults.set(favorites, forKey: "allowed")
            if let t = target, !favorites.contains(t) { target = nil }
        }
    }
    /// nil = any favorite (priority order); otherwise lock to exactly this BSSID.
    @Published var target: String? {
        didSet {
            guard target != oldValue else { return }
            defaults.set(target, forKey: "target")
            retryAfter = .distantPast
            kick()
        }
    }
    @Published var lockEnabled: Bool {
        didSet {
            guard lockEnabled != oldValue else { return }
            defaults.set(lockEnabled, forKey: "lockEnabled")
            retryAfter = .distantPast
            kick()
        }
    }
    @Published var interval: Double { didSet { defaults.set(interval, forKey: "interval") } }
    /// Friendly names for access points, keyed by BSSID.
    @Published var names: [String: String] { didSet { defaults.set(names, forKey: "names") } }

    // Live state
    @Published var currentSSID: String?
    @Published var currentBSSID: String?
    @Published var currentRSSI = 0
    @Published var visible: [AP] = []
    @Published var nearby: [NetworkSummary] = []
    @Published var lastScan: Date?
    @Published var state: LockState = .idle
    @Published var statusTitle = "Starting…"
    @Published var statusDetail = ""
    @Published var isLocked = false
    @Published var log: [String] = []
    @Published var locAuth: CLAuthorizationStatus = .notDetermined
    @Published var busy = false
    @Published var launchAtLogin = false

    let updater = Updater()
    private let lm = CLLocationManager()
    private var retryAfter = Date.distantPast
    private var pendingCheck: Task<Void, Never>?
    private static let retryBackoff: TimeInterval = 60
    private static let timeFmt: DateFormatter = {
        let f = DateFormatter(); f.dateFormat = "HH:mm:ss"; return f
    }()

    override init() {
        let d = UserDefaults.standard
        let s = d.string(forKey: "ssid") ?? ""
        ssid = s
        let favs = (d.stringArray(forKey: "allowed") ?? []).filter(isValidBSSID).map(canonical)
        favorites = favs
        target = d.string(forKey: "target").map(canonical).flatMap { favs.contains($0) ? $0 : nil }
        lockEnabled = d.object(forKey: "lockEnabled") as? Bool ?? true
        interval = d.object(forKey: "interval") as? Double ?? 10
        names = d.dictionary(forKey: "names") as? [String: String] ?? [:]
        password = Keychain.get(s) ?? ""
        super.init()

        launchAtLogin = SMAppService.mainApp.status == .enabled
        updater.log = { [weak self] message in self?.addLog(message) }
        if CommandLine.arguments.contains("--updated") { addLog("Updated to version \(Updater.currentVersion)") }
        lm.delegate = self
        locAuth = lm.authorizationStatus
        if locAuth == .notDetermined {
            lm.requestWhenInUseAuthorization()
            lm.requestLocation()   // one-shot; makes sure the permission prompt appears
        }

        // React the moment macOS roams or reconnects, instead of waiting for the next periodic check.
        let client = CWWiFiClient.shared()
        client.delegate = self
        for event in [CWEventType.ssidDidChange, .bssidDidChange, .linkDidChange] {
            try? client.startMonitoringEvent(with: event)
        }
        Task { await loop() }
    }

    // MARK: Wi-Fi events (delivered on a CoreWLAN queue)

    nonisolated func ssidDidChangeForWiFiInterface(withName interfaceName: String) { wifiChanged() }
    nonisolated func bssidDidChangeForWiFiInterface(withName interfaceName: String) { wifiChanged() }
    nonisolated func linkDidChangeForWiFiInterface(withName interfaceName: String) { wifiChanged() }

    nonisolated private func wifiChanged() {
        Task { @MainActor in self.scheduleCheck() }
    }

    /// Check soon, coalescing bursts of events (a roam fires several) into one check.
    private func scheduleCheck(after seconds: Double = 1.5) {
        pendingCheck?.cancel()
        pendingCheck = Task { [weak self] in
            try? await Task.sleep(for: .seconds(seconds))
            guard !Task.isCancelled else { return }
            await self?.tick()
        }
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

    func requestLocationAccess() {
        if locAuth == .notDetermined {
            lm.requestWhenInUseAuthorization()
            lm.requestLocation()
        } else {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_LocationServices")!)
        }
    }

    // MARK: Names and favorites

    func name(for b: String) -> String? {
        guard let n = names[b]?.trimmingCharacters(in: .whitespaces), !n.isEmpty else { return nil }
        return n
    }
    func label(for b: String) -> String { name(for: b) ?? b }

    func rename(_ b: String, to newName: String) {
        let t = newName.trimmingCharacters(in: .whitespaces)
        names[b] = t.isEmpty ? nil : t
        kick()
    }

    func toggleFavorite(_ b: String) {
        if favorites.contains(b) { favorites.removeAll { $0 == b } } else { favorites.append(b) }
        kick()
    }

    func moveFavorite(_ b: String, by delta: Int) {
        guard let i = favorites.firstIndex(of: b) else { return }
        let j = i + delta
        guard favorites.indices.contains(j) else { return }
        favorites.swapAt(i, j)
    }

    func addFavorite(_ raw: String) -> Bool {
        guard isValidBSSID(raw) else { addLog("Not a valid BSSID: \(raw)"); return false }
        let b = canonical(raw)
        if !favorites.contains(b) { favorites.append(b) }
        return true
    }

    /// Favorites in priority order (including ones out of range).
    var favoriteItems: [APItem] {
        favorites.enumerated().map { i, b in APItem(bssid: b, ap: visible.first { $0.bssid == b }, rank: i) }
    }
    /// Access points in range that aren't favorites, strongest first.
    var otherItems: [APItem] {
        visible.filter { !favorites.contains($0.bssid) }.map { APItem(bssid: $0.bssid, ap: $0, rank: nil) }
    }

    // MARK: Loop

    func addLog(_ s: String) {
        log.append("\(Self.timeFmt.string(from: Date()))  \(s)")
        if log.count > 300 { log.removeFirst(log.count - 300) }
    }

    /// Read the current connection. Only publishes real changes, so SwiftUI doesn't redraw for nothing.
    func refresh() {
        let i = CWWiFiClient.shared().interface()
        let s = i?.ssid()
        let b = i?.bssid().map(canonical)
        let r = i?.rssiValue() ?? 0
        if currentSSID != s { currentSSID = s }
        if currentBSSID != b { currentBSSID = b }
        if abs(currentRSSI - r) >= 3 || (currentRSSI == 0) != (r == 0) { currentRSSI = r }   // ignore ±2 dBm jitter
    }

    /// First run: if no network is configured yet, use the one the Mac is on now.
    @discardableResult
    private func adoptCurrentNetworkIfUnset() -> Bool {
        guard ssid.isEmpty, let cur = currentSSID, !cur.isEmpty else { return false }
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
    var wanted: [String] { target.map { [$0] } ?? favorites }

    private func setStatus(_ s: LockState, _ title: String, _ detail: String) {
        if state != s { state = s }
        if statusTitle != title { statusTitle = title }
        if statusDetail != detail { statusDetail = detail }
        let locked = s == .locked
        if isLocked != locked { isLocked = locked }
    }

    /// One line describing the current connection.
    var connectionDetail: String {
        guard let b = currentBSSID else { return currentSSID ?? "Not connected to Wi-Fi" }
        let q = SignalQuality(rssi: currentRSSI)
        var parts = [currentSSID ?? ""]
        if name(for: b) != nil { parts.append(b) }
        parts.append("\(q.label) signal (\(currentRSSI) dBm)")
        return parts.filter { !$0.isEmpty }.joined(separator: " · ")
    }

    func tick() async {
        refresh()
        guard !busy else { return }
        guard lockEnabled else {
            setStatus(.off, "Lock is off", "macOS picks the access point. " + connectionDetail); return
        }
        guard locationOK else {
            setStatus(.attention, "Location access needed", "macOS hides access point addresses without it."); return
        }
        guard !ssid.isEmpty else {
            setStatus(.attention, "Choose a network", "Pick the Wi-Fi network BSSIDLock should manage."); return
        }
        guard let cur = currentSSID else {
            setStatus(.idle, "Not connected to Wi-Fi", "Waiting for “\(ssid)”."); return
        }
        guard cur == ssid else {
            setStatus(.idle, "Idle on “\(cur)”", "BSSIDLock only acts on “\(ssid)”."); return
        }
        guard !wanted.isEmpty else {
            setStatus(.attention, "Pick an access point", "Press Lock on one below, or star a few as favorites."); return
        }
        guard let b = currentBSSID else {
            setStatus(.attention, "Can't read the access point", "Check Location access for BSSIDLock."); return
        }
        if wanted.contains(b) {
            setStatus(.locked, "Locked to \(label(for: b))", connectionDetail)
            return
        }

        let wait = retryAfter.timeIntervalSinceNow
        if wait > 0 {
            setStatus(.attention, "On \(label(for: b))",
                      "Your chosen access point isn't in range. Trying again in \(Int(wait.rounded(.up))) s.")
            return
        }
        setStatus(.working, "Switching…", "Moving from \(label(for: b)) to \(target.map(label(for:)) ?? "your favorite").")
        if !(await connect(order: wanted)) {
            retryAfter = Date().addingTimeInterval(Self.retryBackoff)
            setStatus(.attention, "Couldn't switch", log.last.map { String($0.dropFirst(10)) } ?? "")
        }
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
        if let b = currentBSSID, wanted.contains(b) {
            setStatus(.locked, "Locked to \(label(for: b))", connectionDetail)
        }
        return r.ok
    }

    /// Scan for access points of the managed network.
    func scan() async {
        guard !busy, !ssid.isEmpty, locationOK else { return }
        busy = true
        defer { busy = false }
        let s = ssid
        let r = await Task.detached { Engine.doWork(ssid: s, order: [], password: "", associate: false) }.value
        if r.scanned { visible = r.aps; lastScan = Date() }
        addLog(r.message)
        refresh()
    }

    /// Scan for all network names nearby (for choosing which network to manage).
    func scanNetworks() async {
        guard !busy, locationOK else { return }
        busy = true
        defer { busy = false }
        nearby = await Task.detached { Engine.scanAll() }.value
        refresh()
    }

    /// Lock to one BSSID (adds it to favorites if needed) and switch to it now.
    func lockTo(_ b: String) {
        if !favorites.contains(b) { favorites.append(b) }
        if !lockEnabled { lockEnabled = true }
        if target == b { kick() } else { target = b }
    }

    func savePassword(_ p: String) {
        guard !ssid.isEmpty else { return }
        password = p
        addLog(Keychain.set(p, for: ssid) ? "Password for “\(ssid)” saved to Keychain" : "Could not save the password to Keychain")
    }

    func setLaunchAtLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch { addLog("Launch at login failed: \(error.localizedDescription)") }
        launchAtLogin = SMAppService.mainApp.status == .enabled
    }

    // MARK: CoreWLAN (runs off the main thread; scans take a few seconds)

    nonisolated static func bandName(_ ch: CWChannel?) -> String {
        switch ch?.channelBand.rawValue ?? 0 {
        case 1: return "2.4 GHz"
        case 2: return "5 GHz"
        case 3: return "6 GHz"
        default: return "?"
        }
    }

    nonisolated static func scanAll() -> [NetworkSummary] {
        guard let iface = CWWiFiClient.shared().interface(),
              let nets = try? iface.scanForNetworks(withName: nil) else { return [] }
        var best: [String: (rssi: Int, count: Int)] = [:]
        for n in nets {
            guard let s = n.ssid, !s.isEmpty else { continue }
            let old = best[s]
            best[s] = (max(old?.rssi ?? -200, n.rssiValue), (old?.count ?? 0) + 1)
        }
        return best.map { NetworkSummary(ssid: $0.key, rssi: $0.value.rssi, count: $0.value.count) }
            .sorted { $0.rssi > $1.rssi }
    }

    nonisolated static func doWork(ssid: String, order: [String], password: String, associate: Bool) -> WorkResult {
        guard let iface = CWWiFiClient.shared().interface() else {
            return .init(aps: [], scanned: false, message: "No Wi-Fi interface", ok: false)
        }
        // Fast path: the top choice was seen in the last 30 s, so join it straight away (no new scan).
        if associate, let first = order.first,
           let cached = NetworkCache.shared.lookup(bssid: first, ssid: ssid, maxAge: 30),
           (try? iface.associate(to: cached, password: password.isEmpty ? nil : password)) != nil {
            return .init(aps: [], scanned: false, message: "Connected to \(first) (\(cached.rssiValue) dBm)", ok: true)
        }

        let nets: Set<CWNetwork>
        do { nets = try iface.scanForNetworks(withSSID: ssid.data(using: .utf8)) }
        catch { return .init(aps: [], scanned: false, message: "Scan failed: \(error.localizedDescription)", ok: false) }

        let withB = nets.filter { $0.bssid != nil }
        NetworkCache.shared.store(Array(withB), ssid: ssid)
        if withB.isEmpty {
            if nets.isEmpty { return .init(aps: [], scanned: true, message: "Scan: no “\(ssid)” access points in range", ok: false) }
            return .init(aps: [], scanned: false, message: "Scan: access point addresses hidden (Location access needed)", ok: false)
        }
        // One row per BSSID (a scan can list the same access point twice); keep the strongest reading.
        var best: [String: AP] = [:]
        for n in withB {
            let ap = AP(bssid: canonical(n.bssid!), rssi: n.rssiValue,
                        channel: n.wlanChannel?.channelNumber ?? 0, band: bandName(n.wlanChannel))
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
                lastErr = "Connecting to \(w) failed: \(error.localizedDescription). If this keeps happening, save the Wi-Fi password in Settings."
            }
        }
        let msg = lastErr.isEmpty ? "None of your chosen access points are in range" : lastErr
        return .init(aps: aps, scanned: true, message: msg, ok: false)
    }
}
