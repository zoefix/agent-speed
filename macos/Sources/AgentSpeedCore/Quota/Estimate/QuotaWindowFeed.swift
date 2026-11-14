import Foundation

/// How a Claude Code snapshot reaches the estimate (QUOTAS 5.6, *Estimate*): window by window at each window's own
/// time, only when that time is later than the window's last one, so an older cache value never lands after a newer
/// bridge value. Each returned group is one snapshot for the estimate, oldest first.
struct QuotaWindowFeed: Sendable {
    private var last: [String: Int64] = [:]

    mutating func take(_ snapshot: QuotaSnapshot) -> [(observedMs: Int64, windows: [QuotaWindow])] {
        var groups: [Int64: [QuotaWindow]] = [:]
        for window in snapshot.windows {
            let time = snapshot.time(of: window)
            let key = QuotaEstimateWindow.id(kind: window.kind, name: window.name, minutes: window.minutes ?? 0)
            if let previous = last[key], time <= previous { continue }
            last[key] = time
            var plain = window
            plain.observedMs = nil
            groups[time, default: []].append(plain)
        }
        return groups.keys.sorted().map { ($0, groups[$0] ?? []) }
    }
}
