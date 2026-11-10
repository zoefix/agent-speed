import Foundation

/// The value rules shared by the phase 2 readers (QUOTAS 9), as `spec/tools/quota_vendors.py` applies them.
enum VendorValue {
    static let dayMinutes = 1440
    static let weekMinutes = 10080
    static let monthMinutes = 43200

    /// A finite number, never a boolean.
    static func strictNumber(_ value: JSONValue?) -> Double? {
        guard let double = value?.literal?.double, double.isFinite else { return nil }
        return double
    }

    /// A finite number, or a string that reads as one (`"150"`, `" 2.5 "`).
    static func number(_ value: JSONValue?) -> Double? {
        if let number = strictNumber(value) { return number }
        guard let text = value?.string?.trimmingCharacters(in: .whitespaces), !text.isEmpty,
              let double = Double(text), double.isFinite else { return nil }
        return double
    }

    /// ISO 8601 text, epoch seconds, or epoch milliseconds (above 1e11), as epoch milliseconds.
    static func timeMs(_ value: JSONValue?) -> Int64? {
        if let number = strictNumber(value) { return epochMs(number) }
        guard let text = value?.string?.trimmingCharacters(in: .whitespaces), !text.isEmpty else { return nil }
        if text.utf8.allSatisfy({ (48...57).contains($0) }), let integer = Double(text) { return epochMs(integer) }
        return isoMs(text)
    }

    static func epochMs(_ value: Double) -> Int64 {
        truncated(value > 1e11 ? value : value * 1000)
    }

    /// `fromisoformat` of the reference: an offset or `Z`, else UTC; a date alone is its midnight.
    static func isoMs(_ text: String) -> Int64? {
        if let parsed = Timestamp.parse(text) { return parsed }
        let bytes = Array(text.utf8)
        if bytes.count == 10 { return Timestamp.parse(text + "T00:00:00Z") }
        guard bytes.count >= 16, bytes.count <= 40 else { return nil }
        let tail = bytes[min(19, bytes.count)...]
        let zoned = tail.contains { $0 == UInt8(ascii: "Z") || $0 == UInt8(ascii: "z") || $0 == UInt8(ascii: "+") || $0 == UInt8(ascii: "-") }
        if zoned { return nil }
        let seconds = bytes.count == 16 ? text + ":00" : text
        return Timestamp.parse(seconds + "Z")
    }

    /// Python's `int()` of a finite double, saturated.
    static func truncated(_ value: Double) -> Int64 {
        CodexRateLimits.truncated(value)
    }

    /// `str(value or "")` for the texts a reader matches words in.
    static func text(_ value: JSONValue?) -> String {
        switch value {
        case .string(let text)?: text
        case .number(let literal)?: literal.text
        case .bool(true)?: "True"
        default: ""
        }
    }

    /// 290–310 minutes is the 5-hour window, 10000–10160 the weekly one, anything else its own length.
    static func kind(minutes: Int) -> QuotaWindowKind {
        if (290...310).contains(minutes) { return .fiveHour }
        if (10000...10160).contains(minutes) { return .sevenDay }
        return .other
    }

    /// A window as the reference's `win()` builds it: `minutes` defaults to the kind's length.
    static func window(_ kind: QuotaWindowKind, _ used: Double, _ resets: Int64?, name: String? = nil,
                       minutes: Int? = nil) -> QuotaWindow {
        QuotaWindow(kind: kind, name: name, minutes: minutes, usedPercent: used, resetsAtMs: resets)
    }

    /// The object of a member, or an empty one.
    static func object(_ value: JSONValue?) -> [String: JSONValue] {
        value?.object ?? [:]
    }

    /// The first member that is present and not null.
    static func pick(_ members: [String: JSONValue], _ keys: String...) -> JSONValue? {
        for key in keys {
            if let value = members[key], !value.isNull { return value }
        }
        return nil
    }
}
