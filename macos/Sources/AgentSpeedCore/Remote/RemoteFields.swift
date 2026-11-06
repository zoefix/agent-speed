import Foundation

/// Why a relay line is invalid (REMOTE 5.6). Only the field name is kept, never the value.
public struct RemoteLineError: Error, Equatable, Sendable {
    public enum Kind: String, Sendable {
        case notJSON, notObject, oversized, missing, wrongType, tooLong, badCharacter, outOfRange, badTime, tooMany
        case unexpected
    }

    public var kind: Kind
    public var field: String

    public init(_ kind: Kind, _ field: String = "") {
        self.kind = kind
        self.field = field
    }
}

/// The character rules of REMOTE 5.6.
enum RemoteText {
    /// Ids, keys, projects, paths and tool names: no U+0000–U+001F or U+007F.
    case identifier
    /// Models, providers and other displayed text: also no C1 controls, line and paragraph separators or bidi
    /// controls, the characters a model id (METRICS 9.2) never needs and a display could be spoofed with.
    case model

    func allows(_ text: String) -> Bool {
        switch self {
        case .identifier:
            return !text.utf8.contains { $0 < 0x20 || $0 == 0x7F }
        case .model:
            return !text.unicodeScalars.contains { scalar in
                let value = scalar.value
                return value < 0x20 || (0x7F...0x9F).contains(value) || value == 0x2028 || value == 0x2029
                    || value == 0x200E || value == 0x200F || (0x202A...0x202E).contains(value)
                    || (0x2066...0x2069).contains(value)
            }
        }
    }
}

/// Typed, validated access to one JSON object of a relay line. Every accessor throws `RemoteLineError` for a
/// missing required member, a wrong type or a violated bound.
struct RemoteFields {
    /// ±2^53: larger integers make a line invalid (REMOTE 5.6).
    static let integerLimit: Int64 = 1 << 53
    /// 2000-01-01T00:00:00Z … 2100-12-31T23:59:59.999Z.
    static let earliestMs: Int64 = 946_684_800_000
    static let latestMs: Int64 = 4_133_980_799_999

    let members: [String: JSONValue]
    /// The member path for errors (`r.`, `a.actions.`).
    let path: String

    init(_ members: [String: JSONValue], path: String = "") {
        self.members = members
        self.path = path
    }

    init(_ value: JSONValue?, path: String) throws(RemoteLineError) {
        guard let members = value?.object else { throw RemoteLineError(value == nil ? .missing : .wrongType, path) }
        self.init(members, path: path + ".")
    }

    private func name(_ key: String) -> String { path + key }

    private func required(_ key: String) throws(RemoteLineError) -> JSONValue {
        guard let value = members[key] else { throw RemoteLineError(.missing, name(key)) }
        return value
    }

    /// Absent and `null` both mean nil.
    private func optional(_ key: String) -> JSONValue? {
        guard let value = members[key], !value.isNull else { return nil }
        return value
    }

    // MARK: Strings

    func string(_ key: String, maxBytes: Int, _ rule: RemoteText = .identifier) throws(RemoteLineError) -> String {
        try Self.checked(try required(key), name(key), maxBytes: maxBytes, rule)
    }

    func optionalString(_ key: String, maxBytes: Int, _ rule: RemoteText = .identifier) throws(RemoteLineError) -> String? {
        guard let value = optional(key) else { return nil }
        return try Self.checked(value, name(key), maxBytes: maxBytes, rule)
    }

    /// A model or provider: `null`, absent or empty is nil.
    func modelString(_ key: String, maxBytes: Int) throws(RemoteLineError) -> String? {
        guard let text = try optionalString(key, maxBytes: maxBytes, .model), !text.isEmpty else { return nil }
        return text
    }

    static func checked(_ value: JSONValue, _ name: String, maxBytes: Int, _ rule: RemoteText) throws(RemoteLineError) -> String {
        guard let text = value.string else { throw RemoteLineError(.wrongType, name) }
        guard text.utf8.count <= maxBytes else { throw RemoteLineError(.tooLong, name) }
        guard rule.allows(text) else { throw RemoteLineError(.badCharacter, name) }
        return text
    }

    func source(_ key: String) throws(RemoteLineError) -> SourceKind {
        guard let text = try required(key).string else { throw RemoteLineError(.wrongType, name(key)) }
        guard let source = SourceKind(rawValue: text) else { throw RemoteLineError(.outOfRange, name(key)) }
        return source
    }

    /// `YYYY-MM-DD`.
    /// A calendar day `YYYY-MM-DD` from 2000-01-01 to 2100-12-31 (REMOTE 8.3 *Bounds*).
    func day(_ key: String) throws(RemoteLineError) -> String {
        let text = try string(key, maxBytes: 10)
        guard Self.isDay(text) else { throw RemoteLineError(.outOfRange, name(key)) }
        return text
    }

    static func isDay(_ text: String) -> Bool {
        let bytes = Array(text.utf8)
        let shaped = bytes.count == 10 && bytes.enumerated().allSatisfy { index, byte in
            index == 4 || index == 7 ? byte == UInt8(ascii: "-") : (48...57).contains(byte)
        }
        guard shaped else { return false }
        func number(_ range: Range<Int>) -> Int { bytes[range].reduce(0) { $0 * 10 + Int($1 - 48) } }
        let year = number(0..<4), month = number(5..<7), day = number(8..<10)
        guard (2000...2100).contains(year), (1...12).contains(month), day >= 1 else { return false }
        let leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)
        let lengths = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31]
        return day <= lengths[month - 1]
    }

    /// `count` lowercase hex digits.
    func hex(_ key: String, count: Int) throws(RemoteLineError) -> String {
        let text = try string(key, maxBytes: count)
        guard Self.isLowerHex(text, count: count) else { throw RemoteLineError(.outOfRange, name(key)) }
        return text
    }

    static func isLowerHex(_ text: String, count: Int) -> Bool {
        text.utf8.count == count && text.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
    }

    // MARK: Scalars

    func bool(_ key: String) throws(RemoteLineError) -> Bool {
        guard let value = try required(key).bool else { throw RemoteLineError(.wrongType, name(key)) }
        return value
    }

    func optionalBool(_ key: String) throws(RemoteLineError) -> Bool? {
        guard let value = optional(key) else { return nil }
        guard let flag = value.bool else { throw RemoteLineError(.wrongType, name(key)) }
        return flag
    }

    func integer(_ key: String) throws(RemoteLineError) -> Int64 {
        try Self.integer(try required(key), name(key))
    }

    func optionalInteger(_ key: String) throws(RemoteLineError) -> Int64? {
        guard let value = optional(key) else { return nil }
        return try Self.integer(value, name(key))
    }

    /// An integer literal within ±2^53.
    static func integer(_ value: JSONValue, _ name: String) throws(RemoteLineError) -> Int64 {
        guard let literal = value.literal, literal.isInteger else { throw RemoteLineError(.wrongType, name) }
        guard let number = literal.int64, (-integerLimit...integerLimit).contains(number) else {
            throw RemoteLineError(.outOfRange, name)
        }
        return number
    }

    func count(_ key: String, limit: Int64 = RemoteFields.integerLimit) throws(RemoteLineError) -> Int {
        let value = try integer(key)
        guard (0...limit).contains(value) else { throw RemoteLineError(.outOfRange, name(key)) }
        return Int(value)
    }

    /// A Unix-millisecond time within the years 2000–2100.
    func time(_ key: String) throws(RemoteLineError) -> Int64 {
        try Self.time(try integer(key), name(key))
    }

    func optionalTime(_ key: String) throws(RemoteLineError) -> Int64? {
        guard let value = try optionalInteger(key) else { return nil }
        return try Self.time(value, name(key))
    }

    static func time(_ value: Int64, _ name: String) throws(RemoteLineError) -> Int64 {
        guard isTime(value) else { throw RemoteLineError(.badTime, name) }
        return value
    }

    static func isTime(_ value: Int64) -> Bool {
        (earliestMs...latestMs).contains(value)
    }

    /// A token value (METRICS 3.2): any number, truncated and clamped to 0 … 10^12.
    func tokens(_ key: String) throws(RemoteLineError) -> Int {
        let value = try required(key)
        guard value.literal != nil else { throw RemoteLineError(.wrongType, name(key)) }
        return HarnessJSON.count(value)
    }

    /// A finite number.
    func number(_ key: String) throws(RemoteLineError) -> Double {
        guard let double = try required(key).literal?.double else { throw RemoteLineError(.wrongType, name(key)) }
        guard double.isFinite else { throw RemoteLineError(.outOfRange, name(key)) }
        return double
    }

    /// A fraction 0–1 (progress); finite values outside are clamped.
    func fraction(_ key: String) throws(RemoteLineError) -> Double {
        min(1, max(0, try number(key)))
    }

    // MARK: Containers

    func object(_ key: String) throws(RemoteLineError) -> RemoteFields {
        try RemoteFields(try required(key), path: name(key))
    }

    func objectMembers(_ key: String) throws(RemoteLineError) -> [String: JSONValue] {
        guard let members = try required(key).object else { throw RemoteLineError(.wrongType, name(key)) }
        return members
    }

    func array(_ key: String, limit: Int) throws(RemoteLineError) -> [JSONValue] {
        guard let items = try required(key).array else { throw RemoteLineError(.wrongType, name(key)) }
        guard items.count <= limit else { throw RemoteLineError(.tooMany, name(key)) }
        return items
    }
}
