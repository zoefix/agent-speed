import Foundation

/// A JSON value of a settings member this version does not know, kept so saving writes it back (REMOTE 2.1: unknown
/// fields from a newer version are kept). Integers stay exact up to 64 bits; other numbers round-trip as `Double`.
enum PreservedJSON: Equatable, Sendable, Codable {
    case null
    case bool(Bool)
    case int(Int64)
    case double(Double)
    case string(String)
    case array([PreservedJSON])
    case object([String: PreservedJSON])

    struct AnyKey: CodingKey {
        var stringValue: String
        var intValue: Int? { nil }

        init(_ string: String) { stringValue = string }
        init?(stringValue: String) { self.stringValue = stringValue }
        init?(intValue: Int) { nil }
    }

    init(from decoder: Decoder) throws {
        if let container = try? decoder.container(keyedBy: AnyKey.self) {
            var members: [String: PreservedJSON] = [:]
            for key in container.allKeys { members[key.stringValue] = try container.decode(PreservedJSON.self, forKey: key) }
            self = .object(members)
            return
        }
        if var container = try? decoder.unkeyedContainer() {
            var items: [PreservedJSON] = []
            while !container.isAtEnd { items.append(try container.decode(PreservedJSON.self)) }
            self = .array(items)
            return
        }
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Int64.self) {
            self = .int(value)
        } else if let value = try? container.decode(Double.self) {
            self = .double(value)
        } else {
            self = .string(try container.decode(String.self))
        }
    }

    func encode(to encoder: Encoder) throws {
        switch self {
        case .null:
            var container = encoder.singleValueContainer()
            try container.encodeNil()
        case .bool(let value):
            var container = encoder.singleValueContainer()
            try container.encode(value)
        case .int(let value):
            var container = encoder.singleValueContainer()
            try container.encode(value)
        case .double(let value):
            var container = encoder.singleValueContainer()
            try container.encode(value)
        case .string(let value):
            var container = encoder.singleValueContainer()
            try container.encode(value)
        case .array(let items):
            var container = encoder.unkeyedContainer()
            for item in items { try container.encode(item) }
        case .object(let members):
            var container = encoder.container(keyedBy: AnyKey.self)
            for (key, value) in members { try container.encode(value, forKey: AnyKey(key)) }
        }
    }

    /// The members of the object being decoded whose names are not in `known`.
    static func unknownMembers(in decoder: Decoder, known: Set<String>) throws -> [String: PreservedJSON] {
        let container = try decoder.container(keyedBy: AnyKey.self)
        var members: [String: PreservedJSON] = [:]
        for key in container.allKeys where !known.contains(key.stringValue) {
            members[key.stringValue] = try? container.decode(PreservedJSON.self, forKey: key)
        }
        return members
    }

    static func encode(_ members: [String: PreservedJSON], to encoder: Encoder) throws {
        guard !members.isEmpty else { return }
        var container = encoder.container(keyedBy: AnyKey.self)
        for (key, value) in members { try container.encode(value, forKey: AnyKey(key)) }
    }
}
