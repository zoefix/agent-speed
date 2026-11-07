import CryptoKit
import Foundation

/// The access key (LAN 4.4): where a request carries it, how it is compared, and the per-address lockout.
public enum LanAccess {
    /// Hyphens and spaces removed, ASCII letters upper-cased.
    public static func normalize(_ text: String) -> String {
        String(String.UnicodeScalarView(text.unicodeScalars.compactMap { scalar -> Unicode.Scalar? in
            if scalar == "-" || scalar == " " { return nil }
            if ("a"..."z").contains(scalar) { return Unicode.Scalar(scalar.value - 32) }
            return scalar
        }))
    }

    /// Constant-time comparison with the stored key; a different length is a mismatch.
    public static func matches(_ given: String?, key: String) -> Bool {
        guard let given else { return false }
        let candidate = Array(normalize(given).utf8)
        let expected = Array(key.utf8)
        guard candidate.count == expected.count else { return false }
        var difference: UInt8 = 0
        for index in candidate.indices { difference |= candidate[index] ^ expected[index] }
        return difference == 0
    }

    /// The Bearer token, else the first `key` query parameter (percent-decoded), else nil.
    public static func requestKey(target: String, headers: [String: String]) -> String? {
        if let authorization = headers["authorization"] {
            let parts = authorization.split(separator: " ", maxSplits: 1, omittingEmptySubsequences: false)
            if parts.count == 2, parts[0].lowercased() == "bearer" {
                let token = parts[1].trimmingCharacters(in: .whitespaces)
                if !token.isEmpty { return token }
            }
        }
        guard let mark = target.firstIndex(of: "?") else { return nil }
        for part in target[target.index(after: mark)...].split(separator: "&", omittingEmptySubsequences: false) {
            guard let equals = part.firstIndex(of: "="), part[..<equals] == "key" else { continue }
            let value = String(part[part.index(after: equals)...])
            return value.removingPercentEncoding ?? value
        }
        return nil
    }
}

/// LAN 4.4: the 10th failure within 60 s locks an address out for 60 s; a valid key clears its failures; at most 256
/// addresses are remembered, the least recently failed forgotten first.
public struct LanLockout: Sendable {
    public static let failures = 10
    public static let windowMs: Int64 = 60_000
    public static let lockMs: Int64 = 60_000
    public static let addresses = 256

    private struct Entry: Sendable {
        var failures: [Int64] = []
        var until: Int64?
    }

    private var entries: [String: Entry] = [:]
    /// Least recently failed first.
    private var order: [String] = []

    public init() {}

    /// Seconds left (rounded up) while `address` is locked out at `nowMs`, else nil.
    public func secondsLeft(_ address: String, nowMs: Int64) -> Int? {
        guard let until = entries[address]?.until, nowMs < until else { return nil }
        return Int((until - nowMs + 999) / 1000)
    }

    public mutating func fail(_ address: String, nowMs: Int64) {
        var entry = entries[address] ?? Entry()
        entry.failures = entry.failures.filter { nowMs - $0 < Self.windowMs } + [nowMs]
        if entry.failures.count >= Self.failures {
            entry.until = nowMs + Self.lockMs
            entry.failures = []
        }
        entries[address] = entry
        order.removeAll { $0 == address }
        order.append(address)
        while order.count > Self.addresses { entries[order.removeFirst()] = nil }
    }

    public mutating func succeed(_ address: String) {
        entries[address] = nil
        order.removeAll { $0 == address }
    }
}

/// The target signature (LAN 5.4): `sha256=` and the lowercase hex HMAC-SHA256 of the exact bytes.
public enum LanSignature {
    public static func line(key: String, body: Data) -> String {
        let code = HMAC<SHA256>.authenticationCode(for: body, using: SymmetricKey(data: Data(key.utf8)))
        return "sha256=" + code.map { String(format: "%02x", $0) }.joined()
    }
}

/// LAN 3.5 and 5.2: a UDP datagram of at most 1400 bytes, signature line included: `history` is left out first, then
/// `sources`.
public enum LanDatagram {
    public static let limit = 1400

    public static func make(frame: ReportJSON, key: String?) -> (data: Data, omitted: [String]) {
        var omitted: [String] = []
        var candidate = frame
        var data = Data()
        for member in [nil, "history", "sources"] as [String?] {
            if let member, case .object(let members) = candidate {
                candidate = .object(members.filter { $0.key != member })
                omitted.append(member)
            }
            data = bytes(candidate, key: key)
            if data.count <= limit { break }
        }
        return (data, omitted)
    }

    public static func bytes(_ frame: ReportJSON, key: String?) -> Data {
        let body = frame.compactData
        guard let key else { return body }
        return body + Data(("\n" + LanSignature.line(key: key, body: body)).utf8)
    }
}
