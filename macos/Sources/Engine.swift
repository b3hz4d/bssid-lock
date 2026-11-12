// BSSIDLock for macOS: Wi-Fi logic and app state.
import SwiftUI
import CoreWLAN
import CoreLocation
import Security
import ServiceManagement
import UserNotifications

/// System notifications (Notification Center), with buttons for the most useful next step.
enum Notifier {
    static let switchCategory = "switch", networkCategory = "network"
    static let stayAction = "stay", backAction = "back", manageAction = "manage"

    /// Set by the engine; called on the main thread when a notification button is clicked.
    @MainActor static var onAction: ((String, [String: String]) -> Void)?

    static func registerActions() {
        UNUserNotificationCenter.current().setNotificationCategories([
            UNNotificationCategory(identifier: switchCategory, actions: [
                UNNotificationAction(identifier: stayAction, title: "Lock to This One", options: []),
                UNNotificationAction(identifier: backAction, title: "Go Back", options: []),
            ], intentIdentifiers: [], options: []),
            UNNotificationCategory(identifier: networkCategory, actions: [
                UNNotificationAction(identifier: manageAction, title: "Manage This Network", options: []),
            ], intentIdentifiers: [], options: []),
        ])
    }
    static func requestPermission() {
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }
    static func post(title: String, body: String, category: String? = nil, info: [String: String] = [:]) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        if let category { content.categoryIdentifier = category }
        content.userInfo = info
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

/// One managed network's choices: favorite access points (in priority order) and an optional lock target.
struct NetworkSetup: Codable, Equatable {
    var favorites: [String] = []
    var target: String?
    /// The BSSIDs the lock accepts on this network.
    var wanted: [String] { target.map { [$0] } ?? favorites }
}

enum PauseLength {
    case minutes(Int)
    case untilNetworkChanges
}

struct WorkResult: Sendable {
    var aps: [AP]
    var nearby: [NetworkSummary] = []
    var scanned: Bool      // false if the scan itself failed or BSSIDs were hidden
    var message: String
    var ok: Bool
}

/// Activity messages, kept apart from the engine so new lines don't redraw the whole window.
/// Also saved to a file, so the history survives restarts and updates.
@MainActor
final class ActivityLog: ObservableObject {
    @Published private(set) var lines: [String] = []
    /// The newest message, without its time.
    private(set) var lastMessage = ""
    private var file: FileHandle?
    private static let shownLines = 300, keptLines = 2000
    private static let timeFmt: DateFormatter = {
        let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd HH:mm:ss"; return f
    }()

    static let fileURL: URL = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("BSSIDLock", isDirectory: true)
        .appendingPathComponent("activity.log")

    init() {
        let fm = FileManager.default
        let url = Self.fileURL
        try? fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        if let text = try? String(contentsOf: url, encoding: .utf8) {
            var all = text.split(separator: "\n").map(String.init)
            if all.count > Self.keptLines {   // keep the file small
                all = Array(all.suffix(Self.keptLines))
                try? (all.joined(separator: "\n") + "\n").write(to: url, atomically: true, encoding: .utf8)
            }
            lines = Array(all.suffix(Self.shownLines))
        }
        if !fm.fileExists(atPath: url.path) { fm.createFile(atPath: url.path, contents: nil) }
        file = try? FileHandle(forWritingTo: url)
        _ = try? file?.seekToEnd()
    }

    func add(_ s: String) {
        let line = "\(Self.timeFmt.string(from: Date()))  \(s)"
        lastMessage = s
        lines.append(line)
        if lines.count > Self.shownLines { lines.removeFirst(lines.count - Self.shownLines) }
        try? file?.write(contentsOf: Data((line + "\n").utf8))
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
    /// The networks (SSIDs) BSSIDLock manages, each with its own favorites. It never touches any other network.
    @Published private(set) var networks: [String] { didSet { defaults.set(networks, forKey: "networks") } }
    /// Favorites and lock target of every managed network.
    private var setups: [String: NetworkSetup] {
        didSet { defaults.set(try? JSONEncoder().encode(setups), forKey: "setups") }
    }
    /// The network shown in the window. Follows the Mac to whichever managed network it joins.
    @Published var ssid: String {
        didSet {
            guard ssid != oldValue else { return }
            defaults.set(ssid, forKey: "ssid")
            password = Keychain.get(ssid) ?? ""
            loadSetup()
        }
    }
    @Published private(set) var password: String
    /// Favorite BSSIDs of the shown network, in priority order (first = preferred).
    @Published var favorites: [String] {
        didSet {
            defaults.set(favorites, forKey: "allowed")   // also what version 1.3 and earlier read
            guard !loadingSetup else { return }
            if let t = target, !favorites.contains(t) { target = nil }
            storeSetup()
        }
    }
    /// For the shown network: nil = any favorite (priority order); otherwise lock to exactly this BSSID.
    @Published var target: String? {
        didSet {
            guard target != oldValue else { return }
            defaults.set(target, forKey: "target")
            guard !loadingSetup else { return }
            storeSetup()
            restartLock()
        }
    }
    @Published var lockEnabled: Bool {
        didSet {
            guard lockEnabled != oldValue else { return }
            defaults.set(lockEnabled, forKey: "lockEnabled")
            clearPause()
            restartLock()
        }
    }
    @Published var interval: Double { didSet { defaults.set(interval, forKey: "interval") } }
    /// Let go of a chosen access point whose signal drops below this many dBm. 0 = always stay locked.
    @Published var weakThreshold: Int {
        didSet {
            guard weakThreshold != oldValue else { return }
            defaults.set(weakThreshold, forKey: "weakThreshold")
            restartLock()
        }
    }
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
    /// While paused the lock doesn't move you. nil = not paused; .distantFuture = until the network changes.
    @Published private(set) var pausedUntil: Date? { didSet { defaults.set(pausedUntil, forKey: "pausedUntil") } }
    /// For "until I switch networks": the network the pause started on ("" = not connected then).
    private var pauseNetwork: String? { didSet { defaults.set(pauseNetwork, forKey: "pauseNetwork") } }

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
    private var loadingSetup = false
    /// The chosen access points were too weak, so macOS is choosing until one recovers.
    private var letGo = false
    private static let retryBackoff: TimeInterval = 60
    /// Extra signal needed before moving back after letting go, so it doesn't flip-flop.
    private static let weakMargin = 5
    private static let clockFmt: DateFormatter = {
        let f = DateFormatter(); f.timeStyle = .short; f.dateStyle = .none; return f
    }()

    override init() {
        let d = UserDefaults.standard
        func clean(_ list: [String]) -> [String] {
            var seen = Set<String>()
            return list.filter(isValidBSSID).map(canonical).filter { seen.insert($0).inserted }
        }
        let legacySSID = d.string(forKey: "ssid") ?? ""
        var stored = d.data(forKey: "setups").flatMap { try? JSONDecoder().decode([String: NetworkSetup].self, from: $0) } ?? [:]
        let nets: [String]
        if let saved = d.stringArray(forKey: "networks") {
            nets = saved
        } else {
            // Version 1.3 and earlier managed one network.
            nets = legacySSID.isEmpty ? [] : [legacySSID]
            if !legacySSID.isEmpty {
                stored[legacySSID] = NetworkSetup(favorites: d.stringArray(forKey: "allowed") ?? [], target: d.string(forKey: "target"))
            }
        }
        var cleaned: [String: NetworkSetup] = [:]
        for n in nets {
            var s = stored[n] ?? NetworkSetup()
            s.favorites = clean(s.favorites)
            s.target = s.target.map(canonical).flatMap { s.favorites.contains($0) ? $0 : nil }
            cleaned[n] = s
        }
        let shown = nets.contains(legacySSID) ? legacySSID : nets.first ?? ""
        networks = nets
        setups = cleaned
        ssid = shown
        favorites = cleaned[shown]?.favorites ?? []
        target = cleaned[shown]?.target
        lockEnabled = d.object(forKey: "lockEnabled") as? Bool ?? true
        interval = d.object(forKey: "interval") as? Double ?? 10
        weakThreshold = d.object(forKey: "weakThreshold") as? Int ?? 0
        names = d.dictionary(forKey: "names") as? [String: String] ?? [:]
        notifyChanges = d.object(forKey: "notifyChanges") as? Bool ?? true
        knownSSIDs = d.dictionary(forKey: "ssids") as? [String: String] ?? [:]
        pausedUntil = d.object(forKey: "pausedUntil") as? Date
        pauseNetwork = d.string(forKey: "pauseNetwork")
        password = Keychain.get(shown) ?? ""
        super.init()

        // Save the (possibly migrated) setup in the current format.
        defaults.set(networks, forKey: "networks")
        defaults.set(try? JSONEncoder().encode(setups), forKey: "setups")

        launchAtLogin = SMAppService.mainApp.status == .enabled
        updater.log = { [weak self] message in self?.addLog(message) }
        Notifier.registerActions()
        Notifier.onAction = { [weak self] action, info in self?.handleNotificationAction(action, info) }
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

    // MARK: Networks

    /// The managed network the Mac is on right now (nil when offline or on another network).
    var activeSSID: String? { currentSSID.flatMap { networks.contains($0) ? $0 : nil } }

    /// A managed network's favorites and target.
    func setup(for network: String) -> NetworkSetup {
        network == ssid ? NetworkSetup(favorites: favorites, target: target) : setups[network] ?? NetworkSetup()
    }

    private var activeSetup: NetworkSetup? { activeSSID.map(setup(for:)) }

    /// The BSSIDs the lock currently accepts (on the network the Mac is on).
    var wanted: [String] { activeSetup?.wanted ?? [] }

    /// Favorites of every managed network (all of them are shown in scans, even on another network name).
    private var allFavorites: Set<String> { Set(setups.values.flatMap(\.favorites)).union(favorites) }

    /// "“MEDIANA”", "“MEDIANA” and “PbLove”", "“A”, “B” and “C”".
    var networkList: String {
        let q = networks.map { "“\($0)”" }
        guard q.count > 1, let last = q.last else { return q.first ?? "" }
        return q.dropLast().joined(separator: ", ") + " and " + last
    }

    /// Start managing a network (or show it, if it's already managed).
    func addNetwork(_ raw: String) {
        let s = raw.trimmingCharacters(in: .whitespaces)
        guard !s.isEmpty else { return }
        if !networks.contains(s) {
            if setups[s] == nil { setups[s] = NetworkSetup() }
            networks.append(s)
            addLog("Now managing “\(s)”")
        }
        ssid = s
        letGo = false
        retryAfter = .distantPast
        Task { await self.scan(); await self.tick() }
    }

    /// Stop managing a network; forgets its favorites and saved password.
    func removeNetwork(_ s: String) {
        guard networks.contains(s) else { return }
        networks.removeAll { $0 == s }
        setups[s] = nil
        Keychain.set("", for: s)
        addLog("Stopped managing “\(s)”")
        if ssid == s { ssid = activeSSID ?? networks.first ?? "" }
        restartLock()
    }

    private func loadSetup() {
        let s = setups[ssid] ?? NetworkSetup()
        loadingSetup = true
        favorites = s.favorites
        target = s.target
        loadingSetup = false
    }

    private func storeSetup() {
        guard networks.contains(ssid) else { return }
        let s = NetworkSetup(favorites: favorites, target: target)
        if setups[ssid] != s { setups[ssid] = s }
    }

    // MARK: Pause

    var isPaused: Bool { pausedUntil.map { $0 > Date() } ?? false }

    /// "until 14:30" or "until you switch networks".
    var pauseText: String {
        guard let until = pausedUntil else { return "" }
        return pauseNetwork != nil ? "until you switch networks" : "until " + Self.clockFmt.string(from: until)
    }

    func pause(_ length: PauseLength) {
        switch length {
        case .minutes(let m):
            pauseNetwork = nil
            pausedUntil = Date().addingTimeInterval(TimeInterval(m * 60))
            addLog("Lock paused for \(m < 60 ? "\(m) minutes" : m == 60 ? "1 hour" : "\(m / 60) hours")")
        case .untilNetworkChanges:
            pauseNetwork = currentSSID ?? ""
            pausedUntil = .distantFuture
            addLog("Lock paused until you switch networks")
        }
        kick()
    }

    func resume() {
        guard pausedUntil != nil else { return }
        clearPause()
        addLog("Lock resumed")
        restartLock()
    }

    private func clearPause() {
        if pausedUntil != nil { pausedUntil = nil }
        if pauseNetwork != nil { pauseNetwork = nil }
    }

    /// End the pause when its time is up, or when the Mac joins another network.
    private func checkPause() {
        guard let until = pausedUntil else { return }
        if until <= Date() {
            clearPause()
            addLog("The pause is over; the lock is on again")
        } else if let n = pauseNetwork, let cur = currentSSID, cur != n {
            clearPause()
            addLog("You switched networks, so the pause is over")
        } else { return }
        letGo = false
        retryAfter = .distantPast
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
    /// Access points of the shown network that aren't favorites, strongest first.
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

    /// Signal of an access point on a network: live for the one we're on, else the latest scan.
    private func signal(_ b: String, on network: String) -> Int? {
        if b == currentBSSID, currentRSSI != 0 { return currentRSSI }
        return visible.first { $0.bssid == b && $0.ssid == network }?.rssi
    }

    /// Remember which network each access point belongs to (shown even when it's out of range).
    private func remember(_ pairs: [(String, String)]) {
        var updated = knownSSIDs
        for (b, s) in pairs where !s.isEmpty { updated[b] = s }
        if updated != knownSSIDs { knownSSIDs = updated }
    }

    /// The network an access point is on, from the latest scan or what we saw before.
    func networkName(_ b: String) -> String? { liveAP(b).flatMap { $0.ssid.isEmpty ? nil : $0.ssid } ?? knownSSIDs[b] }

    // MARK: Notification buttons

    private func handleNotificationAction(_ action: String, _ info: [String: String]) {
        switch action {
        case Notifier.stayAction:
            guard let b = info["bssid"], let net = info["ssid"], networks.contains(net) else { return }
            if ssid != net { ssid = net }
            addLog("Locking to \(label(for: b)) (from a notification)")
            lockTo(b)
        case Notifier.backAction:
            guard let b = info["previous"], let net = info["ssid"], currentSSID == net else { return }
            addLog("Going back to \(label(for: b)) (from a notification)")
            Task { await self.connect(order: [b], on: net) }
        case Notifier.manageAction:
            guard let net = info["ssid"] else { return }
            addNetwork(net)
        default:
            break
        }
    }

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
        if currentSSID != s {
            currentSSID = s
            letGo = false
            // Follow the Mac to whichever managed network it joins.
            if let s, networks.contains(s), s != ssid { ssid = s }
        }
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
        var category: String?
        var info = ["ssid": s]
        if s != prev.ssid {
            title = "Connected to “\(s)”"
            if networks.contains(s) {
                body = "BSSIDLock is managing this network" + (b.map { " · " + label(for: $0) } ?? "") + "."
            } else {
                body = networks.isEmpty ? "Wi-Fi network changed." : "BSSIDLock doesn't manage this network, so it's idle here."
                category = Notifier.networkCategory
            }
        } else if let b {
            title = "Switched to \(label(for: b))"
            body = s
            if rssi != 0 { body += " · \(SignalQuality(rssi: rssi).label) (\(rssi) dBm)" }
            if Date().timeIntervalSince(lastSwitchByUs) < 20 {
                body += " · moved by BSSIDLock"
            } else {
                body += " · macOS roamed"
                if networks.contains(s), let p = prev.bssid {
                    category = Notifier.switchCategory
                    info["bssid"] = b
                    info["previous"] = p
                }
            }
        } else { return }

        addLog("\(title). \(body)")
        if notifyChanges { Notifier.post(title: title, body: body, category: category, info: info) }
    }

    /// First run: if no network is configured yet, use the one the Mac is on now.
    @discardableResult
    private func adoptCurrentNetworkIfUnset() -> Bool {
        guard networks.isEmpty, let cur = currentSSID, !cur.isEmpty else { return false }
        addNetwork(cur)
        return true
    }

    private func kick() { Task { await tick() } }

    /// Something about the lock changed: forget earlier back-off and decide again now.
    private func restartLock() {
        letGo = false
        retryAfter = .distantPast
        kick()
    }

    private func loop() async {
        refresh()
        if locationOK && !adoptCurrentNetworkIfUnset() { await scan() }
        while true {
            await tick()
            await refreshSignalsIfDue()
            try? await Task.sleep(for: .seconds(max(3, interval)))
        }
    }

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
        checkPause()
        guard lockEnabled else {
            setStatus(.off, "Lock is off", "macOS picks the access point. " + connectionDetail); return
        }
        if isPaused {
            setStatus(.idle, "Paused \(pauseText)", "macOS picks the access point for now. " + connectionDetail); return
        }
        guard locationOK else {
            setStatus(.attention, "Location access needed", "macOS hides access point addresses without it."); return
        }
        guard !networks.isEmpty else {
            setStatus(.attention, "Choose a network", "Pick the Wi-Fi network BSSIDLock should manage."); return
        }
        guard let cur = currentSSID else {
            setStatus(.idle, "Not connected to Wi-Fi", "Waiting for \(networkList)."); return
        }
        guard let setup = activeSetup else {
            setStatus(.idle, "Idle on “\(cur)”", "BSSIDLock only acts on \(networkList)."); return
        }
        let wanted = setup.wanted
        guard !wanted.isEmpty else {
            setStatus(.attention, "Pick an access point",
                      cur == ssid ? "Press Lock on one below, or star a few as favorites." : "Choose favorites for “\(cur)”."); return
        }
        guard let b = currentBSSID else {
            setStatus(.attention, "Can't read the access point", "Check Location access for BSSIDLock."); return
        }
        if weakThreshold < 0 {
            await tickLettingGoWhenWeak(network: cur, setup: setup, current: b); return
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
        setStatus(.working, "Switching…", "Moving from \(label(for: b)) to \(setup.target.map(label(for:)) ?? "your favorite").")
        if !(await connect(order: wanted, on: cur)) {
            retryAfter = Date().addingTimeInterval(Self.retryBackoff)
            setStatus(.attention, "Couldn't switch", activity.lastMessage)
        }
    }

    /// The lock with "let go when the signal is weak": stay on a chosen access point while its signal is good
    /// enough; otherwise move to a chosen one (or another favorite) that's strong, or let macOS choose until
    /// one recovers.
    private func tickLettingGoWhenWeak(network: String, setup: NetworkSetup, current b: String) async {
        let floor = weakThreshold, margin = Self.weakMargin
        let wanted = setup.wanted
        let onWanted = wanted.contains(b)
        if onWanted && currentRSSI >= floor {
            letGo = false
            setStatus(.locked, "Locked to \(label(for: b))", connectionDetail)
            return
        }

        let wait = retryAfter.timeIntervalSinceNow
        if wait <= 0 { await refreshForDecision() }
        func strength(_ x: String) -> Int { signal(x, on: network) ?? -200 }
        // Moving back after letting go needs a bit more signal than the floor.
        let need = letGo ? floor + margin : floor
        var order = wanted.filter { $0 != b && strength($0) >= need && (!onWanted || strength($0) >= currentRSSI + margin) }
        let onGoodFavorite = setup.favorites.contains(b) && currentRSSI >= floor
        if order.isEmpty && !onGoodFavorite {
            order = setup.favorites.filter { !wanted.contains($0) && $0 != b && strength($0) >= floor + margin }
        }

        if order.isEmpty {
            // Nothing better in range: stay where we are.
            if onWanted {
                letGo = false
                setStatus(.locked, "Locked to \(label(for: b))", "Weak signal (\(currentRSSI) dBm), and nothing stronger is in range.")
            } else {
                if !letGo {
                    letGo = true
                    addLog("Your chosen access point is weak or out of range, so macOS chooses until it's back")
                }
                setStatus(.attention, "On \(label(for: b))",
                          "Your chosen access point is weak or out of range, so macOS chooses for now. BSSIDLock moves back when the signal recovers.")
            }
            return
        }
        if wait > 0 {
            setStatus(.attention, "On \(label(for: b))", "Couldn't switch. Trying again in \(Int(wait.rounded(.up))) s.")
            return
        }

        let to = label(for: order[0])
        if onWanted { addLog("Weak signal on \(label(for: b)) (\(currentRSSI) dBm); moving to a stronger access point") }
        setStatus(.working, "Switching…",
                  onWanted ? "The signal on \(label(for: b)) is weak; moving to \(to)." : "Moving from \(label(for: b)) to \(to).")
        if await connect(order: order, on: network, minRSSI: floor) {
            letGo = !(currentBSSID.map { wanted.contains($0) } ?? false)
        } else {
            retryAfter = Date().addingTimeInterval(Self.retryBackoff)
            setStatus(.attention, "Couldn't switch", activity.lastMessage)
        }
    }

    /// Before deciding whether to move: fresher readings of the other access points. Mostly macOS's own
    /// recent results; a real radio scan at most every 2 minutes.
    private func refreshForDecision() async {
        if let last = lastScan, Date().timeIntervalSince(last) < 8 { return }
        await scan(quiet: true, active: Date().timeIntervalSince(lastActiveScan) >= 120)
    }

    // MARK: Actions

    /// Join the first access point of `order` that's in range on `network` (and at least `minRSSI` strong).
    @discardableResult
    func connect(order: [String], on network: String, minRSSI: Int? = nil) async -> Bool {
        guard !busy, !network.isEmpty else { return false }
        busy = true
        defer { busy = false }
        let pw = network == ssid ? password : Keychain.get(network) ?? ""
        let (managed, favs, shown) = (Set(networks), allFavorites, ssid)
        let r = await Task.detached {
            Engine.doWork(shown: shown, managed: managed, favorites: favs, join: network, order: order, minRSSI: minRSSI, password: pw)
        }.value
        apply(r)
        addLog(r.message)
        refresh()
        if r.ok { lastSwitchByUs = Date() }
        if let b = currentBSSID, wanted.contains(b) {
            setStatus(.locked, "Locked to \(label(for: b))", connectionDetail)
        }
        return r.ok
    }

    /// One scan updates everything: the managed networks' access points, favorites on any network,
    /// and the list of nearby network names. `quiet` scans (periodic refreshes) only log problems.
    func scan(quiet: Bool = false, active: Bool = true) async {
        guard !busy, locationOK else { return }
        busy = true
        let (managed, favs, shown) = (Set(networks), allFavorites, ssid)
        let r = await Task.detached {
            Engine.doWork(shown: shown, managed: managed, favorites: favs, join: nil, order: [], minRSSI: nil, password: "", active: active)
        }.value
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

    /// Lock to one BSSID of the shown network (adds it to favorites if needed) and switch to it now.
    func lockTo(_ b: String) {
        if !favorites.contains(b) { favorites.append(b) }
        if !lockEnabled { lockEnabled = true }
        if isPaused { clearPause() }
        if target == b { restartLock() } else { target = b }
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

    /// Scan (one pass over all channels) and, if `join` is set, join the first access point of `order`
    /// that's in range on that network and at least `minRSSI` strong. Only managed networks are ever joined.
    nonisolated static func doWork(shown: String, managed: Set<String>, favorites: Set<String>, join: String?, order: [String],
                                   minRSSI: Int?, password: String, active: Bool = true) -> WorkResult {
        guard let iface = CWWiFiClient.shared().interface() else {
            return .init(aps: [], scanned: false, message: "No Wi-Fi interface", ok: false)
        }
        func strongEnough(_ n: CWNetwork) -> Bool { minRSSI.map { n.rssiValue >= $0 } ?? true }
        // Fast path: the top choice was seen in the last 30 s, so join it straight away (no new scan).
        if let join, let first = order.first,
           let cached = NetworkCache.shared.lookup(bssid: first, ssid: join, maxAge: 30), strongEnough(cached),
           (try? iface.associate(to: cached, password: password.isEmpty ? nil : password)) != nil {
            return .init(aps: [], scanned: false, message: "Connected to \(first) (\(cached.rssiValue) dBm)", ok: true)
        }

        // Periodic refreshes use the results macOS already has (no radio scan) when they exist.
        let cached = iface.cachedScanResults() ?? []
        let nets: Set<CWNetwork>
        if !active && join == nil && !cached.isEmpty {
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

        // Keep the managed networks' access points plus favorites on any network; one row per BSSID.
        var best: [String: AP] = [:]
        for n in withB {
            let b = canonical(n.bssid!), s = n.ssid ?? ""
            guard managed.contains(s) || favorites.contains(b) else { continue }
            let ap = AP(bssid: b, ssid: s, rssi: n.rssiValue, channel: n.wlanChannel?.channelNumber ?? 0, band: bandName(n.wlanChannel))
            if let old = best[b], old.rssi >= ap.rssi { continue }
            best[b] = ap
        }
        let aps = best.values.sorted { $0.rssi > $1.rssi }
        let nearby = summarize(nets)
        guard let join else {
            let count = aps.filter { $0.ssid == shown }.count
            return .init(aps: aps, nearby: nearby, scanned: true,
                         message: shown.isEmpty ? "Scan: found \(nearby.count) network(s)" : "Scan: found \(count) access point(s) on “\(shown)”", ok: true)
        }

        var lastErr = "", skippedWeak = false
        for w in order {
            guard let net = withB.filter({ canonical($0.bssid!) == w && $0.ssid == join }).max(by: { $0.rssiValue < $1.rssiValue }) else { continue }
            guard strongEnough(net) else { skippedWeak = true; continue }
            do {
                try iface.associate(to: net, password: password.isEmpty ? nil : password)
                return .init(aps: aps, nearby: nearby, scanned: true, message: "Connected to \(w) (\(net.rssiValue) dBm)", ok: true)
            } catch {
                lastErr = "Connecting to \(w) failed: \(error.localizedDescription). If this keeps happening, save the Wi-Fi password in Settings."
            }
        }
        let msg = !lastErr.isEmpty ? lastErr
            : skippedWeak ? "Your chosen access points are too weak right now"
            : "None of your chosen access points are in range"
        return .init(aps: aps, nearby: nearby, scanned: true, message: msg, ok: false)
    }
}
