import Foundation

/// A JSON document that keeps its members in order and its numbers as written, so one value of Claude Code's settings
/// can change while the rest of the file stays as the user and Claude Code wrote it (QUOTAS 5.2).
indirect enum OrderedJSON: Equatable, Sendable {
    case null
    case bool(Bool)
    case number(String)
    case string(String)
    case array([OrderedJSON])
    case object([(String, OrderedJSON)])

    static func == (lhs: OrderedJSON, rhs: OrderedJSON) -> Bool {
        switch (lhs, rhs) {
        case (.null, .null): true
        case let (.bool(a), .bool(b)): a == b
        case let (.number(a), .number(b)): a == b
        case let (.string(a), .string(b)): a == b
        case let (.array(a), .array(b)): a == b
        case let (.object(a), .object(b)): a.count == b.count && zip(a, b).allSatisfy { $0.0 == $1.0 && $0.1 == $1.1 }
        default: false
        }
    }

    subscript(key: String) -> OrderedJSON? {
        guard case .object(let members) = self else { return nil }
        return members.first { $0.0 == key }?.1
    }

    var isObject: Bool {
        if case .object = self { return true }
        return false
    }

    var string: String? {
        if case .string(let text) = self { return text }
        return nil
    }

    var isNumber: Bool {
        if case .number = self { return true }
        return false
    }

    /// Replaces a member where it is, or appends it last.
    mutating func set(_ key: String, _ value: OrderedJSON) {
        guard case .object(var members) = self else { return }
        if let index = members.firstIndex(where: { $0.0 == key }) {
            members[index].1 = value
        } else {
            members.append((key, value))
        }
        self = .object(members)
    }

    mutating func remove(_ key: String) {
        guard case .object(let members) = self else { return }
        self = .object(members.filter { $0.0 != key })
    }

    // MARK: Writing

    /// Two-space indentation, `": "` after names and `","` at line ends, like Claude Code's and Python's writers; the
    /// caller adds the final newline.
    func text(indent: Int = 0) -> String {
        var out = ""
        write(into: &out, level: indent)
        return out
    }

    private func write(into out: inout String, level: Int) {
        switch self {
        case .null: out += "null"
        case .bool(let value): out += value ? "true" : "false"
        case .number(let literal): out += literal
        case .string(let text): Self.quote(text, into: &out)
        case .array(let items):
            guard !items.isEmpty else {
                out += "[]"
                return
            }
            out += "[\n"
            for (index, item) in items.enumerated() {
                out += String(repeating: "  ", count: level + 1)
                item.write(into: &out, level: level + 1)
                out += index < items.count - 1 ? ",\n" : "\n"
            }
            out += String(repeating: "  ", count: level) + "]"
        case .object(let members):
            guard !members.isEmpty else {
                out += "{}"
                return
            }
            out += "{\n"
            for (index, (key, value)) in members.enumerated() {
                out += String(repeating: "  ", count: level + 1)
                Self.quote(key, into: &out)
                out += ": "
                value.write(into: &out, level: level + 1)
                out += index < members.count - 1 ? ",\n" : "\n"
            }
            out += String(repeating: "  ", count: level) + "}"
        }
    }

    /// Non-ASCII text as is; quotes, backslashes and control characters escaped.
    static func quote(_ text: String, into out: inout String) {
        out += "\""
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            case "\u{08}": out += "\\b"
            case "\u{0C}": out += "\\f"
            case _ where scalar.value < 0x20:
                let hex = String(scalar.value, radix: 16)
                out += "\\u" + String(repeating: "0", count: 4 - hex.count) + hex
            default: out.unicodeScalars.append(scalar)
            }
        }
        out += "\""
    }

    // MARK: Reading

    /// Strict RFC 8259 with a depth limit; a repeated name keeps its first place and its last value.
    static func parse(_ data: Data, maxDepth: Int = 64) -> OrderedJSON? {
        var reader = Reader(bytes: [UInt8](data), maxDepth: maxDepth)
        reader.skipWhitespace()
        guard let value = reader.value(depth: 0) else { return nil }
        reader.skipWhitespace()
        return reader.index == reader.bytes.count ? value : nil
    }

    private struct Reader {
        let bytes: [UInt8]
        let maxDepth: Int
        var index = 0

        init(bytes: [UInt8], maxDepth: Int) {
            self.bytes = bytes
            self.maxDepth = maxDepth
        }

        mutating func skipWhitespace() {
            while index < bytes.count, [0x20, 0x09, 0x0A, 0x0D].contains(bytes[index]) { index += 1 }
        }

        mutating func value(depth: Int) -> OrderedJSON? {
            guard depth <= maxDepth, index < bytes.count else { return nil }
            switch bytes[index] {
            case UInt8(ascii: "{"): return object(depth: depth + 1)
            case UInt8(ascii: "["): return array(depth: depth + 1)
            case UInt8(ascii: "\""): return string().map(OrderedJSON.string)
            case UInt8(ascii: "t"): return literal("true") ? .bool(true) : nil
            case UInt8(ascii: "f"): return literal("false") ? .bool(false) : nil
            case UInt8(ascii: "n"): return literal("null") ? .null : nil
            default: return number()
            }
        }

        mutating func literal(_ word: String) -> Bool {
            let utf8 = Array(word.utf8)
            guard index + utf8.count <= bytes.count, Array(bytes[index..<(index + utf8.count)]) == utf8 else { return false }
            index += utf8.count
            return true
        }

        mutating func object(depth: Int) -> OrderedJSON? {
            index += 1
            var members: [(String, OrderedJSON)] = []
            skipWhitespace()
            if index < bytes.count, bytes[index] == UInt8(ascii: "}") {
                index += 1
                return .object(members)
            }
            while true {
                skipWhitespace()
                guard index < bytes.count, bytes[index] == UInt8(ascii: "\""), let key = string() else { return nil }
                skipWhitespace()
                guard index < bytes.count, bytes[index] == UInt8(ascii: ":") else { return nil }
                index += 1
                skipWhitespace()
                guard let item = value(depth: depth) else { return nil }
                if let existing = members.firstIndex(where: { $0.0 == key }) {
                    members[existing].1 = item
                } else {
                    members.append((key, item))
                }
                skipWhitespace()
                guard index < bytes.count else { return nil }
                if bytes[index] == UInt8(ascii: ",") {
                    index += 1
                    continue
                }
                guard bytes[index] == UInt8(ascii: "}") else { return nil }
                index += 1
                return .object(members)
            }
        }

        mutating func array(depth: Int) -> OrderedJSON? {
            index += 1
            var items: [OrderedJSON] = []
            skipWhitespace()
            if index < bytes.count, bytes[index] == UInt8(ascii: "]") {
                index += 1
                return .array(items)
            }
            while true {
                skipWhitespace()
                guard let item = value(depth: depth) else { return nil }
                items.append(item)
                skipWhitespace()
                guard index < bytes.count else { return nil }
                if bytes[index] == UInt8(ascii: ",") {
                    index += 1
                    continue
                }
                guard bytes[index] == UInt8(ascii: "]") else { return nil }
                index += 1
                return .array(items)
            }
        }

        mutating func number() -> OrderedJSON? {
            let start = index
            func digits() -> Int {
                let from = index
                while index < bytes.count, (UInt8(ascii: "0")...UInt8(ascii: "9")).contains(bytes[index]) { index += 1 }
                return index - from
            }
            if index < bytes.count, bytes[index] == UInt8(ascii: "-") { index += 1 }
            guard index < bytes.count else { return nil }
            if bytes[index] == UInt8(ascii: "0") {
                index += 1
            } else if digits() == 0 {
                return nil
            }
            if index < bytes.count, bytes[index] == UInt8(ascii: ".") {
                index += 1
                guard digits() > 0 else { return nil }
            }
            if index < bytes.count, bytes[index] == UInt8(ascii: "e") || bytes[index] == UInt8(ascii: "E") {
                index += 1
                if index < bytes.count, bytes[index] == UInt8(ascii: "+") || bytes[index] == UInt8(ascii: "-") { index += 1 }
                guard digits() > 0 else { return nil }
            }
            return .number(String(decoding: bytes[start..<index], as: UTF8.self))
        }

        mutating func string() -> String? {
            index += 1
            var scalars = String.UnicodeScalarView()
            var run = index
            func flush(_ end: Int) -> Bool {
                guard run < end else { return true }
                guard let text = String(bytes: bytes[run..<end], encoding: .utf8) else { return false }
                scalars.append(contentsOf: text.unicodeScalars)
                return true
            }
            while index < bytes.count {
                let byte = bytes[index]
                if byte == UInt8(ascii: "\"") {
                    guard flush(index) else { return nil }
                    index += 1
                    return String(scalars)
                }
                if byte < 0x20 { return nil }
                guard byte == UInt8(ascii: "\\") else {
                    index += 1
                    continue
                }
                guard flush(index), index + 1 < bytes.count else { return nil }
                let escape = bytes[index + 1]
                index += 2
                switch escape {
                case UInt8(ascii: "\""): scalars.append("\"")
                case UInt8(ascii: "\\"): scalars.append("\\")
                case UInt8(ascii: "/"): scalars.append("/")
                case UInt8(ascii: "b"): scalars.append("\u{08}")
                case UInt8(ascii: "f"): scalars.append("\u{0C}")
                case UInt8(ascii: "n"): scalars.append("\n")
                case UInt8(ascii: "r"): scalars.append("\r")
                case UInt8(ascii: "t"): scalars.append("\t")
                case UInt8(ascii: "u"):
                    guard var code = hex4() else { return nil }
                    if (0xD800...0xDBFF).contains(code), index + 1 < bytes.count, bytes[index] == UInt8(ascii: "\\"),
                       bytes[index + 1] == UInt8(ascii: "u") {
                        index += 2
                        guard let low = hex4(), (0xDC00...0xDFFF).contains(low) else { return nil }
                        code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00)
                    }
                    scalars.append(Unicode.Scalar(code) ?? "\u{FFFD}")
                default: return nil
                }
                run = index
            }
            return nil
        }

        mutating func hex4() -> UInt32? {
            guard index + 4 <= bytes.count else { return nil }
            var value: UInt32 = 0
            for byte in bytes[index..<(index + 4)] {
                let digit: UInt32
                switch byte {
                case UInt8(ascii: "0")...UInt8(ascii: "9"): digit = UInt32(byte - UInt8(ascii: "0"))
                case UInt8(ascii: "a")...UInt8(ascii: "f"): digit = UInt32(byte - UInt8(ascii: "a") + 10)
                case UInt8(ascii: "A")...UInt8(ascii: "F"): digit = UInt32(byte - UInt8(ascii: "A") + 10)
                default: return nil
                }
                value = value << 4 | digit
            }
            index += 4
            return value
        }
    }
}
