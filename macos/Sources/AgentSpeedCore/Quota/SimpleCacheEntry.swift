import CryptoKit
import Foundation

/// One file of a Chromium *simple cache* (QUOTAS 4.2): the key, stream 1 (the body) and stream 0 (the raw response
/// headers), little-endian. Every offset is checked against the file; anything unexpected gives nil.
struct SimpleCacheEntry: Equatable {
    static let headerMagic: UInt64 = 0xFCFB_6D1B_A772_5C30
    static let eofMagic: UInt64 = 0xF4FA_6F45_970D_41D8
    static let version: UInt32 = 5
    static let headerSize = 24
    static let eofSize = 24
    static let keyHashFlag: UInt32 = 0x2
    static let keyHashSize = 32
    /// Larger files are not a usage response.
    static let fileLimit = 1 << 20

    var key: String
    var body: ArraySlice<UInt8>
    var responseInfo: ArraySlice<UInt8>

    init?(_ bytes: [UInt8]) {
        guard bytes.count <= Self.fileLimit, bytes.count >= Self.headerSize + 2 * Self.eofSize,
              let keyLength = Self.keyLength(ofHeader: bytes) else { return nil }
        let keyEnd = Self.headerSize + keyLength
        guard keyEnd <= bytes.count, let key = String(bytes: bytes[Self.headerSize..<keyEnd], encoding: .utf8) else {
            return nil
        }
        let eof0 = bytes.count - Self.eofSize
        guard Self.u64(bytes, eof0) == Self.eofMagic else { return nil }
        let flags = Self.u32(bytes, eof0 + 8)
        let size0 = Int(Self.u32(bytes, eof0 + 16))
        let stream0End = eof0 - (flags & Self.keyHashFlag != 0 ? Self.keyHashSize : 0)
        let stream0Start = stream0End - size0
        let eof1 = stream0Start - Self.eofSize
        guard eof1 >= keyEnd, Self.u64(bytes, eof1) == Self.eofMagic else { return nil }
        self.key = key
        body = bytes[keyEnd..<eof1]
        responseInfo = bytes[stream0Start..<stream0End]
    }

    /// The key's length from the first 24 bytes when the magic and version match.
    static func keyLength(ofHeader bytes: [UInt8]) -> Int? {
        guard bytes.count >= headerSize, u64(bytes, 0) == headerMagic, u32(bytes, 8) == version else { return nil }
        return Int(u32(bytes, 12))
    }

    /// The URL of a key: the text after its last space (partitioned caches prefix the top-frame site), else the key
    /// without a leading `<digits>/<digits>/`.
    static func url(ofKey key: String) -> String {
        if let space = key.lastIndex(of: " ") { return String(key[key.index(after: space)...]) }
        let utf8 = Array(key.utf8)
        var index = 0
        for _ in 0..<2 {
            let start = index
            while index < utf8.count, (UInt8(ascii: "0")...UInt8(ascii: "9")).contains(utf8[index]) { index += 1 }
            guard index > start, index < utf8.count, utf8[index] == UInt8(ascii: "/") else { return key }
            index += 1
        }
        return String(decoding: utf8[index...], as: UTF8.self)
    }

    /// The entry's file name: the first 8 bytes of the key's SHA-1 as a little-endian number, 16 lowercase hex digits,
    /// then `_0`.
    static func fileName(ofKey key: String) -> String {
        let digest = Array(Insecure.SHA1.hash(data: Data(key.utf8)))
        var value: UInt64 = 0
        for (shift, byte) in digest.prefix(8).enumerated() { value |= UInt64(byte) << (8 * UInt64(shift)) }
        let hex = String(value, radix: 16)
        return String(repeating: "0", count: 16 - hex.count) + hex + "_0"
    }

    static func u64(_ bytes: [UInt8], _ at: Int) -> UInt64 {
        guard at >= 0, at + 8 <= bytes.count else { return 0 }
        var value: UInt64 = 0
        for k in 0..<8 { value |= UInt64(bytes[at + k]) << (8 * UInt64(k)) }
        return value
    }

    static func u32(_ bytes: [UInt8], _ at: Int) -> UInt32 {
        guard at >= 0, at + 4 <= bytes.count else { return 0 }
        var value: UInt32 = 0
        for k in 0..<4 { value |= UInt32(bytes[at + k]) << (8 * UInt32(k)) }
        return value
    }
}

/// The response headers of stream 0: NUL-separated lines, the first starting with `HTTP/`, then `name:value` lines
/// up to an empty line (QUOTAS 4.2).
struct CachedResponseInfo: Equatable {
    var status: Int?
    /// Lower-case names; the first value of a repeated name.
    var headers: [String: String]

    init?(_ stream: ArraySlice<UInt8>) {
        let marker = Array("HTTP/".utf8)
        guard let start = Self.find(marker, in: stream) else { return nil }
        var lines: [String] = []
        var index = start
        while index < stream.endIndex {
            let end = stream[index...].firstIndex(of: 0) ?? stream.endIndex
            if end == index { break }
            lines.append(String(decoding: stream[index..<end], as: UTF8.self))
            index = end + 1
        }
        guard let first = lines.first else { return nil }
        let parts = first.split(separator: " ", omittingEmptySubsequences: false)
        status = parts.count > 1 && !parts[1].isEmpty && parts[1].allSatisfy(\.isASCIIDigit) ? Int(parts[1]) : nil
        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { continue }
            let name = line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            if headers[name] == nil {
                headers[name] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            }
        }
        self.headers = headers
    }

    private static func find(_ needle: [UInt8], in haystack: ArraySlice<UInt8>) -> Int? {
        guard haystack.count >= needle.count else { return nil }
        var index = haystack.startIndex
        while index + needle.count <= haystack.endIndex {
            if haystack[index] == needle[0], haystack[index..<(index + needle.count)].elementsEqual(needle) { return index }
            index += 1
        }
        return nil
    }

    /// An RFC 7231 `date` (IMF-fixdate, e.g. `Tue, 10 Nov 2026 19:26:13 GMT`) in Unix milliseconds.
    static func dateMs(_ text: String) -> Int64? {
        let parts = text.split(separator: " ")
        guard parts.count == 6, parts[5] == "GMT" || parts[5] == "UTC",
              let day = Int(parts[1]), let year = Int(parts[3]),
              let month = Self.months.firstIndex(of: parts[2].lowercased()).map({ $0 + 1 }) else { return nil }
        let clock = parts[4].split(separator: ":")
        guard clock.count == 3, let hour = Int(clock[0]), let minute = Int(clock[1]), let second = Int(clock[2]),
              (1...31).contains(day), hour < 24, minute < 60, second < 61 else { return nil }
        var components = DateComponents()
        components.calendar = Calendar(identifier: .gregorian)
        components.timeZone = TimeZone(identifier: "UTC")
        (components.year, components.month, components.day) = (year, month, day)
        (components.hour, components.minute, components.second) = (hour, minute, second)
        guard let date = components.date else { return nil }
        return Int64((date.timeIntervalSince1970 * 1000).rounded())
    }

    private static let months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"]
}

private extension Character {
    var isASCIIDigit: Bool { isASCII && isNumber }
}
