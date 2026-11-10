import Foundation

/// `payload.rate_limits` of a Codex `token_count` line (QUOTAS 3), as far as the quota needs it.
struct CodexRateLimits: Equatable, Sendable {
    struct Slot: Equatable, Sendable {
        var usedPercent: Double?
        var windowMinutes: Double?
        var resetsAt: Double?
        var resetsInSeconds: Double?
    }

    /// `limit_id` is `"codex"`, null or absent; other buckets (`"premium"`) are not the account's limits.
    var isCodexBucket = true
    var primary: Slot?
    var secondary: Slot?

    /// The windows this line gives, `primary` first; a kind seen twice keeps the first. `timestampMs` is the line's
    /// time, which an older Codex's `resets_in_seconds` counts from.
    func windows(timestampMs: Int64) -> [QuotaWindow] {
        guard isCodexBucket else { return [] }
        var found: [QuotaWindow] = []
        for slot in [primary, secondary] {
            guard let slot, let used = slot.usedPercent, let windowMinutes = slot.windowMinutes else { continue }
            let minutes = Self.truncated(windowMinutes)
            guard minutes > 0 else { continue }
            let kind = Self.kind(minutes: minutes)
            let resets: Int64? = if let at = slot.resetsAt {
                Self.truncated(at) &* 1000
            } else if let after = slot.resetsInSeconds {
                timestampMs &+ Self.truncated(after * 1000)
            } else {
                nil
            }
            let length = Int(clamping: minutes)
            if found.contains(where: { $0.kind == kind && (kind != .other || $0.minutes == length) }) { continue }
            found.append(QuotaWindow(kind: kind, minutes: kind == .other ? length : nil, usedPercent: used, resetsAtMs: resets))
        }
        return found
    }

    /// 290–310 minutes is the 5-hour window (older versions wrote 299), 10000–10160 the weekly one.
    static func kind(minutes: Int64) -> QuotaWindowKind {
        if (290...310).contains(minutes) { return .fiveHour }
        if (10000...10160).contains(minutes) { return .sevenDay }
        return .other
    }

    /// Python's `int()` of a finite double, saturated.
    static func truncated(_ value: Double) -> Int64 {
        if value >= 0x1p63 { return .max }
        if value < -0x1p63 { return .min }
        return Int64(value.rounded(.towardZero))
    }
}

extension CodexRateLimits {
    /// Scans the `rate_limits` value; nil when it is not an object.
    static func scan(_ scanner: inout JSONScanner) throws(JSONScanError) -> CodexRateLimits? {
        guard try scanner.beginObjectIfPresent() else { return nil }
        var limits = CodexRateLimits()
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "limit_id") {
                if try scanner.readNull() {
                    limits.isCodexBucket = true
                } else {
                    limits.isCodexBucket = try scanner.readString() == "codex"
                }
            } else if scanner.key(key, is: "primary") {
                limits.primary = try scanSlot(&scanner)
            } else if scanner.key(key, is: "secondary") {
                limits.secondary = try scanSlot(&scanner)
            } else {
                try scanner.skipValue()
            }
        }
        return limits
    }

    private static func scanSlot(_ scanner: inout JSONScanner) throws(JSONScanError) -> Slot? {
        guard try scanner.beginObjectIfPresent() else { return nil }
        var slot = Slot()
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "used_percent") {
                slot.usedPercent = try scanner.readDouble()
            } else if scanner.key(key, is: "window_minutes") {
                slot.windowMinutes = try scanner.readDouble()
            } else if scanner.key(key, is: "resets_at") {
                slot.resetsAt = try scanner.readDouble()
            } else if scanner.key(key, is: "resets_in_seconds") {
                slot.resetsInSeconds = try scanner.readDouble()
            } else {
                try scanner.skipValue()
            }
        }
        return slot
    }
}
