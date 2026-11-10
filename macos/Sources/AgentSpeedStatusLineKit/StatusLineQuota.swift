import Foundation

/// The Claude CLI's quota file, `<data>/quota/claude-cli.json` (QUOTAS 5.5): one value per window as Claude Code last
/// passed it to the status line, and when a value last changed.
///
/// ```json
/// {"version": 1, "receivedMs": 1794338685000,
///  "windows": {"five_hour": {"used_percentage": 6, "resets_at": 1794354000},
///              "seven_day": {"used_percentage": 86, "resets_at": 1794448800}}}
/// ```
public struct StatusLineQuota: Equatable, Sendable {
    public static let version = 1
    /// Two values whose reset times are this close belong to the same window.
    public static let sameWindowMs = 60_000.0
    public static let fields = ["five_hour", "seven_day"]

    public struct Window: Equatable, Sendable {
        public var usedPercentage: Double
        /// Epoch seconds.
        public var resetsAt: Double

        public init(usedPercentage: Double, resetsAt: Double) {
            self.usedPercentage = usedPercentage
            self.resetsAt = resetsAt
        }

        public var resetsAtMs: Int64 { StatusLineQuota.saturated(resetsAt * 1000) }

        /// An object with a numeric `used_percentage` and a numeric `resets_at`.
        init?(_ value: Any?) {
            guard let object = value as? [String: Any], let used = Self.number(object["used_percentage"]),
                  let resets = Self.number(object["resets_at"]) else { return nil }
            self.init(usedPercentage: used, resetsAt: resets)
        }

        static func number(_ value: Any?) -> Double? {
            guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
            let double = number.doubleValue
            return double.isFinite ? double : nil
        }

        var json: [String: Any] {
            ["used_percentage": Self.json(usedPercentage), "resets_at": Self.json(resetsAt)]
        }

        /// Whole numbers as integers, as Claude Code writes them.
        static func json(_ value: Double) -> NSNumber {
            value == value.rounded() && abs(value) < 0x1p53 ? NSNumber(value: Int64(value)) : NSNumber(value: value)
        }
    }

    public var receivedMs: Int64?
    /// By field name: `five_hour`, `seven_day`.
    public var windows: [String: Window]

    public init(receivedMs: Int64? = nil, windows: [String: Window] = [:]) {
        self.receivedMs = receivedMs
        self.windows = windows
    }

    /// The file's contents; nil when it is not this format.
    public init?(data: Data) {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              (object["version"] as? NSNumber)?.intValue == Self.version else { return nil }
        receivedMs = Window.number(object["receivedMs"]).map(Self.saturated)
        var windows: [String: Window] = [:]
        if let stored = object["windows"] as? [String: Any] {
            for field in Self.fields {
                if let window = Window(stored[field]) { windows[field] = window }
            }
        }
        self.windows = windows
    }

    public func data() -> Data {
        var stored: [String: Any] = [:]
        for (field, window) in windows { stored[field] = window.json }
        let object: [String: Any] = ["version": Self.version, "receivedMs": receivedMs.map { NSNumber(value: $0) } ?? NSNull(),
                                     "windows": stored]
        return (try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])) ?? Data()
    }

    /// Merges a status line's `rate_limits` (QUOTAS 5.5 step 2). A new value replaces the stored one when its reset
    /// time is more than 60 s later; within 60 s it is the same window and the higher use wins; an earlier one is
    /// ignored. A stored window the input lacks is kept. Returns whether anything changed (`receivedMs` is then `nowMs`).
    @discardableResult
    public mutating func merge(rateLimits: Any?, nowMs: Int64) -> Bool {
        guard let limits = rateLimits as? [String: Any] else { return false }
        var changed = false
        for field in Self.fields {
            guard let new = Window(limits[field]) else { continue }
            guard let old = windows[field] else {
                windows[field] = new
                changed = true
                continue
            }
            let later = new.resetsAt * 1000 - old.resetsAt * 1000
            if later > Self.sameWindowMs || (abs(later) <= Self.sameWindowMs && new.usedPercentage > old.usedPercentage) {
                windows[field] = new
                changed = true
            }
        }
        if changed { receivedMs = nowMs }
        return changed
    }

    /// `5h 6% · 7d 86%`: the windows that have not reset, in order; empty when there is none.
    public func line(nowMs: Int64) -> String {
        var parts: [String] = []
        for (field, label) in [("five_hour", "5h"), ("seven_day", "7d")] {
            guard let window = windows[field], nowMs < window.resetsAtMs else { continue }
            parts.append("\(label) \(Self.percent(window.usedPercentage))%")
        }
        return parts.joined(separator: " · ")
    }

    /// Python's `int()` of a finite double, saturated.
    static func saturated(_ value: Double) -> Int64 {
        value >= 0x1p63 ? .max : value < -0x1p63 ? .min : Int64(value.rounded(.towardZero))
    }

    /// Rounded half up and clamped to 0…100 (QUOTAS 2).
    public static func percent(_ used: Double) -> Int {
        Int(min(100, max(0, (used + 0.5).rounded(.down))))
    }
}
