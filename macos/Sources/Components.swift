// Small reusable views.
import SwiftUI

/// UI-only state shared by the window and the menu bar.
/// (An ObservableObject instead of @State, because @State is a macro in recent SDKs
/// and the Command Line Tools ship without the SwiftUI macro plugin.)
final class UIState: ObservableObject {
    @Published var showSettings = false
    @Published var showActivity = false
    @Published var renaming: String?
    @Published var renameText = ""
    @Published var ssidDraft = ""
    @Published var pwDraft = ""
    @Published var showPassword = false
    @Published var manualBSSID = ""
    @Published var customInterval = false
    @Published var intervalText = ""

    func startRename(_ bssid: String, current: String?) {
        renameText = current ?? ""
        renaming = bssid
    }
}

/// Four signal bars, colored by quality. Grey when the access point is out of range.
struct SignalBars: View {
    let rssi: Int?

    var body: some View {
        let q = rssi.map(SignalQuality.init(rssi:))
        let filled = q?.bars ?? 0
        HStack(alignment: .bottom, spacing: 2) {
            ForEach(0..<4) { i in
                RoundedRectangle(cornerRadius: 1.5)
                    .fill(i < filled ? (q?.color ?? .gray) : Color.secondary.opacity(0.25))
                    .frame(width: 4, height: CGFloat(5 + i * 4))
            }
        }
        .frame(width: 22, height: 18, alignment: .bottom)
        .help(rssi.map { "\(SignalQuality(rssi: $0).label) signal (\($0) dBm)" } ?? "Out of range")
        .accessibilityLabel(rssi.map { "\(SignalQuality(rssi: $0).label) signal" } ?? "Out of range")
    }
}

struct Pill: View {
    let text: String
    let color: Color

    var body: some View {
        Text(text)
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 7)
            .padding(.vertical, 2)
            .background(color.opacity(0.15), in: Capsule())
            .foregroundStyle(color)
    }
}

/// Round status badge matching the current lock state.
struct StatusBadge: View {
    let state: LockState
    var size: CGFloat = 52

    var body: some View {
        ZStack {
            Circle().fill(state.color.opacity(0.15))
            Image(systemName: state.icon)
                .font(.system(size: size * 0.42, weight: .semibold))
                .foregroundStyle(state.color)
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}
