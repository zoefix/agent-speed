import AgentSpeedStatusLineKit
import Foundation

/// The Claude CLI account's limits from the helper's quota file (QUOTAS 5.5, *Reader*). The app checks the file's
/// modification time every 5 s while the bridge is on and reads it only when it changed.
public struct ClaudeCliQuotaReader: Sendable {
    public let file: URL
    private var stamp: (size: Int64, modifiedNs: Int64)?
    public private(set) var snapshot: QuotaSnapshot?
    public private(set) var receivedMs: Int64?

    public init(file: URL) {
        self.file = file
    }

    /// Returns true when the snapshot changed.
    @discardableResult
    public mutating func refresh() -> Bool {
        guard let stat = FileStat.lstat(file.path), stat.isRegularFile else {
            let changed = snapshot != nil || stamp != nil
            stamp = nil
            snapshot = nil
            receivedMs = nil
            return changed
        }
        if let stamp, stamp.size == stat.size, stamp.modifiedNs == stat.modifiedNs { return false }
        stamp = (stat.size, stat.modifiedNs)
        let before = snapshot
        let stored = (try? Data(contentsOf: file)).flatMap(StatusLineQuota.init(data:))
        receivedMs = stored?.receivedMs
        snapshot = stored.flatMap(Self.snapshot)
        return snapshot != before
    }

    /// Each stored window as `fiveHour` / `sevenDay`; `observedMs` is `receivedMs`.
    public static func snapshot(_ stored: StatusLineQuota) -> QuotaSnapshot? {
        guard let received = stored.receivedMs else { return nil }
        var windows: [QuotaWindow] = []
        for (field, kind) in [("five_hour", QuotaWindowKind.fiveHour), ("seven_day", .sevenDay)] {
            guard let window = stored.windows[field] else { continue }
            windows.append(QuotaWindow(kind: kind, usedPercent: window.usedPercentage, resetsAtMs: window.resetsAtMs))
        }
        return QuotaSnapshot(source: .claudeCode, observedMs: received, windows: windows)
    }
}
