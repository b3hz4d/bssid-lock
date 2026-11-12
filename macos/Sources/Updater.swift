// Automatic updates from this project's GitHub Releases.
//
// Once a day (if enabled) it asks GitHub for the latest release. A newer version is downloaded, checked
// against the release's SHA-256 checksum, its bundle ID and version, and its code signature. When this
// copy is signed with a certificate, the update must be signed by the same certificate. It is then swapped
// in by a tiny helper script after this copy quits, and relaunched in the menu bar.
// Nothing is sent beyond a normal HTTPS request to api.github.com / github.com.
import SwiftUI
import CryptoKit
import Security

struct UpdateError: LocalizedError {
    let message: String
    init(_ m: String) { message = m }
    var errorDescription: String? { message }
}

struct GitHubRelease: Decodable, Sendable {
    struct Asset: Decodable, Sendable {
        let name: String
        let browserDownloadUrl: URL
    }
    let tagName: String
    let assets: [Asset]

    func asset(_ name: String) -> URL? { assets.first { $0.name == name }?.browserDownloadUrl }
}

@MainActor
final class Updater: ObservableObject {
    nonisolated static let repo = "b3hz4d/bssid-lock"
    nonisolated static var currentVersion: String { Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.0.0" }

    @Published var autoUpdate: Bool { didSet { UserDefaults.standard.set(autoUpdate, forKey: "autoUpdate") } }
    @Published private(set) var status = ""
    @Published private(set) var available: String?
    @Published private(set) var busy = false
    /// Set once the new version is ready and the app is restarting; blocks a second install.
    @Published private(set) var installing = false

    /// Where to write activity messages (set by the engine).
    var log: (String) -> Void = { _ in }

    init() {
        autoUpdate = UserDefaults.standard.object(forKey: "autoUpdate") as? Bool ?? true
        Task { await loop() }
    }

    var actionTitle: String {
        installing ? "Restarting to Update…" : busy ? "Checking for Updates…" : available.map { "Install Version \($0)" } ?? "Check for Updates…"
    }

    private func loop() async {
        try? await Task.sleep(for: .seconds(60))
        while true {
            let last = UserDefaults.standard.object(forKey: "lastUpdateCheck") as? Date ?? .distantPast
            if autoUpdate && Date().timeIntervalSince(last) > 24 * 3600 { await check(userInitiated: false) }
            try? await Task.sleep(for: .seconds(3600))
        }
    }

    /// Check now. Installs right away when automatic updates are on, or when the user asked.
    func check(userInitiated: Bool) async {
        guard !busy, !installing else { return }
        busy = true
        defer { if !installing { busy = false } }
        status = "Checking for updates…"
        do {
            let release = try await Self.fetchLatest()
            UserDefaults.standard.set(Date(), forKey: "lastUpdateCheck")
            let latest = release.tagName.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
            guard Self.isNewer(latest, than: Self.currentVersion) else {
                available = nil
                status = "BSSIDLock is up to date (version \(Self.currentVersion))."
                return
            }
            available = latest
            if autoUpdate || userInitiated {
                try await install(release, version: latest, userInitiated: userInitiated)
            } else {
                status = "Version \(latest) is available."
                log(status)
            }
        } catch {
            status = "Couldn't update: \(error.localizedDescription)"
            log(status)
        }
    }

    // MARK: Steps

    nonisolated static func fetchLatest() async throws -> GitHubRelease {
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(repo)/releases/latest")!, timeoutInterval: 30)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("BSSIDLock/\(currentVersion) (+https://github.com/\(repo))", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: req)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw UpdateError("GitHub didn't return the latest release.") }
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return try decoder.decode(GitHubRelease.self, from: data)
    }

    nonisolated static func isNewer(_ a: String, than b: String) -> Bool {
        func parts(_ s: String) -> [Int] {
            let p = s.split(separator: ".").map { Int($0.prefix { $0.isNumber }) ?? 0 }
            return p + Array(repeating: 0, count: max(0, 3 - p.count))
        }
        return parts(a).lexicographicallyPrecedes(parts(b)) == false && parts(a) != parts(b)
    }

    private func install(_ release: GitHubRelease, version: String, userInitiated: Bool) async throws {
        guard let zipURL = release.asset("BSSIDLock-macOS.zip"), let sumsURL = release.asset("SHA256SUMS.txt") else {
            throw UpdateError("The release is missing its files.")
        }
        let bundle = Bundle.main.bundleURL
        guard FileManager.default.isWritableFile(atPath: bundle.deletingLastPathComponent().path) else {
            status = "Version \(version) is available. Run the install command again to update (no write access to \(bundle.deletingLastPathComponent().path))."
            log(status)
            return
        }

        status = "Downloading version \(version)…"
        let work = FileManager.default.temporaryDirectory.appendingPathComponent("bssidlock-update-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
        let zip = work.appendingPathComponent("BSSIDLock-macOS.zip")
        let (downloaded, _) = try await URLSession.shared.download(from: zipURL)
        try FileManager.default.moveItem(at: downloaded, to: zip)
        let (sums, _) = try await URLSession.shared.data(from: sumsURL)

        // 1. Checksum published with the release
        let digest = SHA256.hash(data: try Data(contentsOf: zip)).map { String(format: "%02x", $0) }.joined()
        let expected = String(decoding: sums, as: UTF8.self).split(separator: "\n").compactMap { line -> String? in
            let fields = line.split(whereSeparator: { $0 == " " || $0 == "\t" })
            return fields.count >= 2 && fields.last?.trimmingCharacters(in: CharacterSet(charactersIn: "*")) == "BSSIDLock-macOS.zip"
                ? fields.first.map(String.init) : nil
        }.first
        guard expected?.lowercased() == digest else { throw UpdateError("The download didn't match the release checksum.") }

        // 2. Unpack and check identity, version and signature
        try await Self.run("/usr/bin/ditto", ["-x", "-k", zip.path, work.appendingPathComponent("new").path])
        let newApp = work.appendingPathComponent("new/BSSIDLock.app")
        try Self.verify(newApp, version: version)

        // 3. Swap after we quit, then relaunch. A lock makes sure only one helper ever swaps the app.
        //    Background updates come back quietly in the menu bar; when you pressed Install, the window reopens.
        let pid = ProcessInfo.processInfo.processIdentifier
        func q(_ s: String) -> String { "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }
        let script = """
        while /bin/kill -0 \(pid) 2>/dev/null; do /bin/sleep 0.2; done
        OLD=\(q(bundle.path)); NEW=\(q(newApp.path)); BAK="$OLD.previous"; LOCK="$OLD.updating"
        /usr/bin/find "$LOCK" -maxdepth 0 -mmin +10 -exec /bin/rmdir {} \\; 2>/dev/null
        /bin/mkdir "$LOCK" 2>/dev/null || { /bin/rm -rf \(q(work.path)); exit 0; }
        if [ -d "$NEW" ]; then
          /bin/rm -rf "$BAK"
          if /bin/mv "$OLD" "$BAK" && /usr/bin/ditto "$NEW" "$OLD"; then /bin/rm -rf "$BAK"; else /bin/rm -rf "$OLD"; /bin/mv "$BAK" "$OLD"; fi
        fi
        /bin/rmdir "$LOCK"
        /usr/bin/open "$OLD"\(userInitiated ? "" : " --args --updated")
        /bin/rm -rf \(q(work.path))
        """
        let helper = Process()
        helper.executableURL = URL(fileURLWithPath: "/bin/sh")
        helper.arguments = ["-c", script]
        try helper.run()

        installing = true
        status = "Restarting to finish the update to version \(version)…"
        log("Installing version \(version) and restarting")
        try? await Task.sleep(for: .milliseconds(300))
        Self.quitForUpdate()
    }

    /// Quit so the helper can swap the app. macOS refuses to quit while a sheet (like Settings) is open,
    /// so close sheets first, and make sure we really exit even if something else holds the app open.
    static func quitForUpdate() {
        for window in NSApp.windows {
            if let sheet = window.attachedSheet { window.endSheet(sheet) }
        }
        NSApp.terminate(nil)
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) {
            UserDefaults.standard.synchronize()
            exit(0)
        }
    }

    nonisolated static func run(_ tool: String, _ args: [String]) async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            let p = Process()
            p.executableURL = URL(fileURLWithPath: tool)
            p.arguments = args
            p.terminationHandler = { proc in
                proc.terminationStatus == 0 ? cont.resume() : cont.resume(throwing: UpdateError("\(tool) failed (\(proc.terminationStatus))"))
            }
            do { try p.run() } catch { cont.resume(throwing: error) }
        }
    }

    /// The new app must be BSSIDLock at the expected version, with a valid signature. If this copy is signed
    /// with a certificate (not ad-hoc), the update must satisfy this copy's designated requirement, i.e. be
    /// signed by the same certificate.
    nonisolated static func verify(_ app: URL, version: String) throws {
        guard let info = Bundle(url: app)?.infoDictionary,
              info["CFBundleIdentifier"] as? String == Bundle.main.bundleIdentifier,
              info["CFBundleShortVersionString"] as? String == version else {
            throw UpdateError("The downloaded app isn't BSSIDLock \(version).")
        }
        var staticCode: SecStaticCode?
        guard SecStaticCodeCreateWithPath(app as CFURL, [], &staticCode) == errSecSuccess, let code = staticCode else {
            throw UpdateError("Couldn't read the downloaded app's signature.")
        }
        let requirement = ownCertificateRequirement()
        let flags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSStrictValidate | kSecCSCheckNestedCode)
        guard SecStaticCodeCheckValidity(code, flags, requirement) == errSecSuccess else {
            throw UpdateError(requirement == nil ? "The downloaded app's signature is invalid."
                                                 : "The downloaded app isn't signed by the same certificate as this copy.")
        }
    }

    /// This app's designated requirement, if it is certificate-based (ad-hoc builds have none worth pinning).
    nonisolated static func ownCertificateRequirement() -> SecRequirement? {
        var me: SecCode?
        var meStatic: SecStaticCode?
        var req: SecRequirement?
        var text: CFString?
        guard SecCodeCopySelf([], &me) == errSecSuccess, let me,
              SecCodeCopyStaticCode(me, [], &meStatic) == errSecSuccess, let meStatic,
              SecCodeCopyDesignatedRequirement(meStatic, [], &req) == errSecSuccess, let req,
              SecRequirementCopyString(req, [], &text) == errSecSuccess,
              (text as String?)?.contains("certificate") == true else { return nil }
        return req
    }
}
