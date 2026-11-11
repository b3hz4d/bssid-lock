// BSSIDLock for macOS: Wi-Fi logic and app state.
import SwiftUI
import CoreWLAN
import CoreLocation
import Security
import ServiceManagement
import UserNotifications

/// System notifications (Notification Center).
enum Notifier {
    static func requestPermission() {
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }
    static func post(title: String, body: String) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        // One identifier, so a newer change replaces the previous notification instead of stacking.
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: "network-change", content: content, trigger: nil))
    }
}

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
    let ssid: String
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
    var nearby: [NetworkSummary] = []
    var scanned: Bool      // false if the scan itself failed or BSSIDs were hidden
    var message: String
    var ok: Bool
}

/// Activity messages, kept apart from the engine so new lines don't redraw the whole window.
@MainActor
final class ActivityLog: ObservableObject {
    @Published private(set) var lines: [String] = []
    private static let timeFmt: DateFormatter = {
        let f = DateFormatter(); f.dateFormat = "HH:mm:ss"; return f
    }()

    func add(_ s: String) {
        lines.append("\(Self.timeFmt.string(from: Date()))  \(s)")
        if lines.count > 300 { lines.removeFirst(lines.count - 300) }
    }
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

    func store(_ nets: [CWNetwork]) {
        lock.lock(); defer { lock.unlock() }
        let now = Date()
        for n in nets { if let b = n.bssid { entries[canonical(b)] = (n, n.ssid ?? "", now) } }
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
    /// Show a notification when the Wi-Fi network or access point changes.
    @Published var notifyChanges: Bool {
        didSet {
            defaults.set(notifyChanges, forKey: "notifyChanges")
            if notifyChanges { Notifier.requestPermission() }
        }
    }
    /// The network (SSID) each access point was last seen on, keyed by BSSID.
    @Published private(set) var knownSSIDs: [String: String] { didSet { defaults.set(knownSSIDs, forKey: "ssids") } }
    /// Friendly names for access points, keyed by BSSID.
    @Published var names: [String: String] { didSet { defaults.set(names, forKey: "names") } }

    // Live state
    @Published var currentSSID: String?
    @Published var currentBSSID: String?
    @Published var currentRSSI = 0
    @Published var currentChannel = 0
    @Published var currentBand = ""
    @Published var visible: [AP] = []
    @Published var nearby: [NetworkSummary] = []
    @Published var lastScan: Date?
    @Published var state: LockState = .idle
    @Published var statusTitle = "Starting…"
    @Published var statusDetail = ""
    @Published var isLocked = false
    let activity = ActivityLog()
    var log: [String] { activity.lines }
    @Published var locAuth: CLAuthorizationStatus = .notDetermined
    @Published var busy = false
    @Published var launchAtLogin = false

    let updater = Updater()
    private let lm = CLLocationManager()
    private var retryAfter = Date.distantPast
    private var pendingCheck: Task<Void, Never>?
    private var announced: (ssid: String?, bssid: String?)?
    private var changeCheck: Task<Void, Never>?
    private var lastSwitchByUs = Date.distantPast
    private var windowOpen = false, popoverOpen = false
    private var rescanRequested = false
    private var lastActiveScan = Date.distantPast
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
        notifyChanges = d.object(forKey: "notifyChanges") as? Bool ?? true
        knownSSIDs = d.dictionary(forKey: "ssids") as? [String: String] ?? [:]
        password = Keychain.get(s) ?? ""
        super.init()

        launchAtLogin = SMAppService.mainApp.status == .enabled
        updater.log = { [weak self] message in self?.addLog(message) }
        if notifyChanges { Notifier.requestPermission() }
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
        favorites.enumerated().map { i, b in APItem(bssid: b, ap: liveAP(b), rank: i) }
    }
    /// Access points of the managed network that aren't favorites, strongest first.
    var otherItems: [APItem] {
        var items = visible.filter { $0.ssid == ssid && !favorites.contains($0.bssid) }.map { APItem(bssid: $0.bssid, ap: liveAP($0.bssid), rank: nil) }
        // The access point we're on, even if the last scan missed it.
        if let b = currentBSSID, currentSSID == ssid, !favorites.contains(b), !items.contains(where: { $0.bssid == b }) {
            items.insert(APItem(bssid: b, ap: liveAP(b), rank: nil), at: 0)
        }
        return items
    }

    /// Freshest reading for an access point: live values for the one we're connected to, else the last scan.
    func liveAP(_ b: String) -> AP? {
        let scanned = visible.first { $0.bssid == b }
        guard b == currentBSSID, currentRSSI != 0 else { return scanned }
        return AP(bssid: b, ssid: currentSSID ?? scanned?.ssid ?? "", rssi: currentRSSI,
                  channel: currentChannel != 0 ? currentChannel : scanned?.channel ?? 0,
                  band: currentBand.isEmpty ? scanned?.band ?? "?" : currentBand)
    }

    /// Remember which network each access point belongs to (shown even when it's out of range).
    private func remember(_ pairs: [(String, String)]) {
        var updated = knownSSIDs
        for (b, s) in pairs where !s.isEmpty { updated[b] = s }
        if updated != knownSSIDs { knownSSIDs = updated }
    }

    /// The network an access point is on, from the latest scan or what we saw before.
    func networkName(_ b: String) -> String? { liveAP(b).flatMap { $0.ssid.isEmpty ? nil : $0.ssid } ?? knownSSIDs[b] }

    // MARK: Viewing (signal readings refresh only while someone is looking)

    var viewing: Bool { windowOpen || popoverOpen }

    func setViewing(window: Bool? = nil, popover: Bool? = nil) {
        let was = viewing
        if let window { windowOpen = window }
        if let popover { popoverOpen = popover }
        if viewing && !was { Task { await refreshSignalsIfDue(staleAfter: 5) } }
    }

    /// While the window or menu bar popover is open, rescan once per interval so signal strengths stay current.
    private func refreshSignalsIfDue(staleAfter: TimeInterval? = nil) async {
        guard viewing, locationOK, !busy else { return }
        let every = staleAfter ?? max(10, interval) - 0.5
        if let last = lastScan, Date().timeIntervalSince(last) < every { return }
        // macOS's own recent results most of the time; a real radio scan at most every 30 s.
        let active = Date().timeIntervalSince(lastActiveScan) >= max(30, interval)
        await scan(quiet: true, active: active)
    }

    /// The Scan / refresh buttons: always does something. If a scan is already running, another follows it.
    func requestScan() {
        if busy { rescanRequested = true } else { Task { await scan() } }
    }

    // MARK: Loop

    func addLog(_ s: String) { activity.add(s) }

    /// Read the current connection. Only publishes real changes, so SwiftUI doesn't redraw for nothing.
    func refresh() {
        let i = CWWiFiClient.shared().interface()
        let s = i?.ssid()
        let b = i?.bssid().map(canonical)
        let r = i?.rssiValue() ?? 0
        let ch = i?.wlanChannel()
        let chNum = ch?.channelNumber ?? 0
        let band = ch.map { Engine.bandName($0) } ?? ""
        if currentSSID != s { currentSSID = s }
        if currentBSSID != b { currentBSSID = b }
        if currentChannel != chNum { currentChannel = chNum }
        if currentBand != band { currentBand = band }
        if let b, let s, !s.isEmpty, knownSSIDs[b] != s { remember([(b, s)]) }
        trackChange(ssid: s, bssid: b)
        if abs(currentRSSI - r) >= 3 || (currentRSSI == 0) != (r == 0) { currentRSSI = r }   // ignore ±2 dBm jitter
    }

    // MARK: Change notifications

    /// Notice a new network / access point; announce it only if it's still the same ~4 s later,
    /// so a roam that BSSIDLock immediately corrects doesn't produce two notifications.
    private func trackChange(ssid s: String?, bssid b: String?) {
        guard let prev = announced else {
            if s != nil { announced = (s, b) }   // starting point, not a change
            return
        }
        guard s != nil, s != prev.ssid || b != prev.bssid, changeCheck == nil else { return }
        changeCheck = Task { [weak self] in
            try? await Task.sleep(for: .seconds(4))
            self?.confirmChange()
        }
    }

    private func confirmChange() {
        changeCheck = nil
        let i = CWWiFiClient.shared().interface()
        guard let s = i?.ssid(), let prev = announced else { return }
        let b = i?.bssid().map(canonical)
        let rssi = i?.rssiValue() ?? 0
        guard s != prev.ssid || b != prev.bssid else { return }
        announced = (s, b)

        let title: String
        var body: String
        if s != prev.ssid {
            title = "Connected to “\(s)”"
            if s == ssid { body = "BSSIDLock is managing this network" + (b.map { " · " + label(for: $0) } ?? "") + "." }
            else if !ssid.isEmpty { body = "BSSIDLock only acts on “\(ssid)”, so it's idle here." }
            else { body = "Wi-Fi network changed." }
        } else if let b {
            title = "Switched to \(label(for: b))"
            body = s
            if rssi != 0 { body += " · \(SignalQuality(rssi: rssi).label) (\(rssi) dBm)" }
            body += Date().timeIntervalSince(lastSwitchByUs) < 20 ? " · moved by BSSIDLock" : " · macOS roamed"
        } else { return }

        addLog("\(title). \(body)")
        if notifyChanges { Notifier.post(title: title, body: body) }
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
            await refreshSignalsIfDue()
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
        let (s, pw, favs) = (ssid, password, favorites)
        let r = await Task.detached { Engine.doWork(ssid: s, favorites: favs, order: order, password: pw, associate: true) }.value
        apply(r)
        addLog(r.message)
        refresh()
        if r.ok { lastSwitchByUs = Date() }
        if let b = currentBSSID, wanted.contains(b) {
            setStatus(.locked, "Locked to \(label(for: b))", connectionDetail)
        }
        return r.ok
    }

    /// One scan updates everything: the managed network's access points, favorites on any network,
    /// and the list of nearby network names. `quiet` scans (periodic refreshes) only log problems.
    func scan(quiet: Bool = false, active: Bool = true) async {
        guard !busy, locationOK else { return }
        busy = true
        let (s, favs) = (ssid, favorites)
        let r = await Task.detached { Engine.doWork(ssid: s, favorites: favs, order: [], password: "", associate: false, active: active) }.value
        if active && r.scanned { lastActiveScan = Date() }
        apply(r)
        if !quiet || !r.ok { addLog(r.message) }
        refresh()
        busy = false
        if rescanRequested {
            rescanRequested = false
            await scan()
        }
    }

    /// Scan for nearby network names (same scan as above).
    func scanNetworks() async { await scan() }

    private func apply(_ r: WorkResult) {
        guard r.scanned else { return }
        if visible != r.aps { visible = r.aps }
        remember(r.aps.map { ($0.bssid, $0.ssid) })
        if !r.nearby.isEmpty && nearby != r.nearby { nearby = r.nearby }
        lastScan = Date()
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

    nonisolated static func summarize(_ nets: Set<CWNetwork>) -> [NetworkSummary] {
        var best: [String: (rssi: Int, count: Int)] = [:]
        for n in nets {
            guard let s = n.ssid, !s.isEmpty else { continue }
            let old = best[s]
            best[s] = (max(old?.rssi ?? -200, n.rssiValue), (old?.count ?? 0) + 1)
        }
        return best.map { NetworkSummary(ssid: $0.key, rssi: $0.value.rssi, count: $0.value.count) }
            .sorted { $0.rssi > $1.rssi }
    }

    /// Scan (one pass over all channels), and optionally join the first wanted access point in range.
    /// Only access points of the managed network are ever joined.
    nonisolated static func doWork(ssid: String, favorites: [String], order: [String], password: String, associate: Bool, active: Bool = true) -> WorkResult {
        guard let iface = CWWiFiClient.shared().interface() else {
            return .init(aps: [], scanned: false, message: "No Wi-Fi interface", ok: false)
        }
        // Fast path: the top choice was seen in the last 30 s, so join it straight away (no new scan).
        if associate, let first = order.first,
           let cached = NetworkCache.shared.lookup(bssid: first, ssid: ssid, maxAge: 30),
           (try? iface.associate(to: cached, password: password.isEmpty ? nil : password)) != nil {
            return .init(aps: [], scanned: false, message: "Connected to \(first) (\(cached.rssiValue) dBm)", ok: true)
        }

        // Periodic refreshes use the results macOS already has (no radio scan) when they exist.
        let cached = iface.cachedScanResults() ?? []
        let nets: Set<CWNetwork>
        if !active && !associate && !cached.isEmpty {
            nets = cached
        } else {
            do { nets = try iface.scanForNetworks(withName: nil) }
            catch {
                // macOS may refuse back-to-back scans ("busy"); fall back to its recent results.
                guard !cached.isEmpty else {
                    return .init(aps: [], scanned: false, message: "Scan failed: \(error.localizedDescription)", ok: false)
                }
                nets = cached
            }
        }

        let withB = nets.filter { $0.bssid != nil }
        NetworkCache.shared.store(Array(withB))
        if withB.isEmpty {
            if nets.isEmpty { return .init(aps: [], scanned: true, message: "Scan: no Wi-Fi networks in range", ok: false) }
            return .init(aps: [], scanned: false, message: "Scan: access point addresses hidden (Location access needed)", ok: false)
        }

        // Keep the managed network's access points plus favorites on any network; one row per BSSID.
        let favSet = Set(favorites)
        var best: [String: AP] = [:]
        for n in withB {
            let b = canonical(n.bssid!), s = n.ssid ?? ""
            guard s == ssid || favSet.contains(b) else { continue }
            let ap = AP(bssid: b, ssid: s, rssi: n.rssiValue, channel: n.wlanChannel?.channelNumber ?? 0, band: bandName(n.wlanChannel))
            if let old = best[b], old.rssi >= ap.rssi { continue }
            best[b] = ap
        }
        let aps = best.values.sorted { $0.rssi > $1.rssi }
        let nearby = summarize(nets)
        guard associate else {
            let count = aps.filter { $0.ssid == ssid }.count
            return .init(aps: aps, nearby: nearby, scanned: true,
                         message: ssid.isEmpty ? "Scan: found \(nearby.count) network(s)" : "Scan: found \(count) access point(s) on “\(ssid)”", ok: true)
        }

        var lastErr = ""
        for w in order {
            guard let net = withB.filter({ canonical($0.bssid!) == w && $0.ssid == ssid }).max(by: { $0.rssiValue < $1.rssiValue }) else { continue }
            do {
                try iface.associate(to: net, password: password.isEmpty ? nil : password)
                return .init(aps: aps, nearby: nearby, scanned: true, message: "Connected to \(w) (\(net.rssiValue) dBm)", ok: true)
            } catch {
                lastErr = "Connecting to \(w) failed: \(error.localizedDescription). If this keeps happening, save the Wi-Fi password in Settings."
            }
        }
        let msg = lastErr.isEmpty ? "None of your chosen access points are in range" : lastErr
        return .init(aps: aps, nearby: nearby, scanned: true, message: msg, ok: false)
    }
}
