import Foundation

/// The Claude Code snapshot (QUOTAS 5.6, *Joining the routes*): the status line bridge's windows and those of Claude
/// Code's usage cache, window by window, each joined window with its own `observedMs`.
public enum ClaudeCodeQuota {
    /// Two resets this close belong to the same window (QUOTAS 5.5 step 2).
    static let sameWindowMs: Int64 = 60_000

    /// With one part, that part unchanged; with both, every window carries the time of its own value.
    public static func join(bridge: QuotaSnapshot?, cache: QuotaSnapshot?) -> QuotaSnapshot? {
        guard let bridge, let cache else { return bridge ?? cache }
        let bridgeMs = bridge.observedMs, cacheMs = cache.observedMs
        var joined: [QuotaWindow] = []
        var taken: [Int] = []
        for window in bridge.windows {
            guard let index = cache.windows.firstIndex(where: { $0.kind == window.kind && $0.name == window.name }) else {
                joined.append(window.at(bridgeMs))
                continue
            }
            taken.append(index)
            let cached = cache.windows[index]
            if sameWindow(window, cached) {
                joined.append((cached.usedPercent > window.usedPercent ? cached : window).at(max(bridgeMs, cacheMs)))
            } else if newerPeriod(window, cached) {
                joined.append(cached.at(cacheMs))
            } else {
                joined.append(window.at(bridgeMs))
            }
        }
        for (index, window) in cache.windows.enumerated() where !taken.contains(index) {
            joined.append(window.at(cacheMs))
        }
        let observed = joined.compactMap(\.observedMs).max() ?? max(bridgeMs, cacheMs)
        return QuotaSnapshot(source: .claudeCode, observedMs: observed, windows: joined)
    }

    /// Resets within 60 s, or both unknown.
    static func sameWindow(_ a: QuotaWindow, _ b: QuotaWindow) -> Bool {
        switch (a.resetsAtMs, b.resetsAtMs) {
        case (nil, nil): true
        case let (a?, b?): abs(b - a) <= sameWindowMs
        default: false
        }
    }

    /// `new` resets more than 60 s after `old`; an unknown reset is earlier than any.
    static func newerPeriod(_ old: QuotaWindow, _ new: QuotaWindow) -> Bool {
        guard let next = new.resetsAtMs else { return false }
        return old.resetsAtMs.map { next - $0 > sameWindowMs } ?? true
    }
}

private extension QuotaWindow {
    func at(_ ms: Int64) -> QuotaWindow {
        var window = self
        window.observedMs = ms
        return window
    }
}
