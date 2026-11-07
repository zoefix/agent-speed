import Darwin
import Foundation

/// Local addresses (LAN 2.3): private, link-local and loopback IPv4; `::1`, link-local and unique-local IPv6; an
/// IPv4-mapped IPv6 address by its IPv4 rule; `255.255.255.255` only for a UDP target.
public enum LanAddress {
    public static func isLocal(_ text: String, udp: Bool = false) -> Bool {
        let bare = text.split(separator: "%", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) ?? text
        if let octets = ipv4(bare) { return isLocal(ipv4: octets, udp: udp) }
        guard let bytes = ipv6(bare) else { return false }
        return isLocal(ipv6: bytes, udp: udp)
    }

    public static func isLocal(ipv4 o: [UInt8], udp: Bool = false) -> Bool {
        if udp && o == [255, 255, 255, 255] { return true }
        return o[0] == 10 || (o[0] == 172 && (16...31).contains(o[1])) || (o[0] == 192 && o[1] == 168)
            || (o[0] == 169 && o[1] == 254) || o[0] == 127
    }

    public static func isLocal(ipv6 b: [UInt8], udp: Bool = false) -> Bool {
        if b[0..<10].allSatisfy({ $0 == 0 }) && b[10] == 0xFF && b[11] == 0xFF {
            return isLocal(ipv4: Array(b[12..<16]), udp: udp)
        }
        if b[0..<15].allSatisfy({ $0 == 0 }) && b[15] == 1 { return true }
        return (b[0] == 0xFE && b[1] & 0xC0 == 0x80) || b[0] & 0xFE == 0xFC
    }

    /// A strict dotted-quad: four decimal parts 0–255 without leading zeros.
    public static func ipv4(_ text: String) -> [UInt8]? {
        let parts = text.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }
        var octets: [UInt8] = []
        for part in parts {
            guard !part.isEmpty, part.count <= 3, part.allSatisfy({ $0.isASCII && $0.isNumber }),
                  part.count == 1 || part.first != "0", let value = UInt8(part) else { return nil }
            octets.append(value)
        }
        return octets
    }

    public static func ipv6(_ text: String) -> [UInt8]? {
        guard text.contains(":") else { return nil }
        var address = in6_addr()
        guard inet_pton(AF_INET6, text, &address) == 1 else { return nil }
        return withUnsafeBytes(of: &address) { Array($0) }
    }

    /// The text form of a socket address (no scope), or nil for another family.
    public static func text(of storage: sockaddr_storage) -> String? {
        var storage = storage
        var buffer = [CChar](repeating: 0, count: Int(INET6_ADDRSTRLEN))
        switch Int32(storage.ss_family) {
        case AF_INET:
            return withUnsafePointer(to: &storage) {
                $0.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { pointer in
                    var address = pointer.pointee.sin_addr
                    return inet_ntop(AF_INET, &address, &buffer, socklen_t(buffer.count)).map { String(cString: $0) }
                }
            }
        case AF_INET6:
            return withUnsafePointer(to: &storage) {
                $0.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) { pointer in
                    var address = pointer.pointee.sin6_addr
                    return inet_ntop(AF_INET6, &address, &buffer, socklen_t(buffer.count)).map { String(cString: $0) }
                }
            }
        default:
            return nil
        }
    }
}

/// Why a target address is invalid (LAN 5.1); the sheet shows `port` and `https` apart.
public enum LanAddressProblem: String, Error, Sendable {
    case empty, spaces, port, host, kind, https, scheme, fragment, userinfo
}

/// A parsed target address (LAN 5.1).
public struct LanTargetAddress: Equatable, Sendable {
    /// The host as written, without brackets: an IP address or a name.
    public var host: String
    /// The host is an IP address (no lookup needed).
    public var isLiteral: Bool
    public var port: Int
    /// HTTP only: `Host` as written in the address (the port only when given).
    public var hostHeader: String?
    /// HTTP only: the path and query, `/` when none.
    public var target: String?

    public static func parse(kind: LanDocument.Kind, _ address: String) -> Result<LanTargetAddress, LanAddressProblem> {
        guard !address.isEmpty, address == address.trimmingCharacters(in: .whitespacesAndNewlines) else {
            return .failure(address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? .empty : .spaces)
        }
        guard !address.unicodeScalars.contains(where: { $0.value <= 0x20 || $0.value == 0x7F }) else {
            return .failure(.spaces)
        }
        switch kind {
        case .udp:
            let (hostText, portText) = splitHostPort(address)
            guard let portText, portText != "!" else { return .failure(.port) }
            let host: (host: String, literal: Bool)
            switch parseHost(hostText) {
            case .success(let value): host = value
            case .failure(let problem): return .failure(problem)
            }
            guard let port = parsePort(portText) else { return .failure(.port) }
            return .success(LanTargetAddress(host: host.host, isLiteral: host.literal, port: port))
        case .http:
            let lower = address.lowercased()
            if lower.hasPrefix("https://") { return .failure(.https) }
            guard lower.hasPrefix("http://") else { return .failure(.scheme) }
            let rest = String(address.dropFirst(7))
            if rest.contains("#") { return .failure(.fragment) }
            let cut = rest.firstIndex(where: { $0 == "/" || $0 == "?" }) ?? rest.endIndex
            let authority = String(rest[..<cut])
            var target = String(rest[cut...])
            if authority.contains("@") { return .failure(.userinfo) }
            let (hostText, portText) = splitHostPort(authority)
            if portText == "!" { return .failure(.port) }
            let host: (host: String, literal: Bool)
            switch parseHost(hostText) {
            case .success(let value): host = value
            case .failure(let problem): return .failure(problem)
            }
            let port: Int
            if let portText {
                guard let value = parsePort(portText) else { return .failure(.port) }
                port = value
            } else {
                port = 80
            }
            if target.isEmpty || target.hasPrefix("?") { target = "/" + target }
            return .success(LanTargetAddress(host: host.host, isLiteral: host.literal, port: port,
                                             hostHeader: authority, target: target))
        }
    }

    /// `host` and the port text: nil without a port, `!` for a malformed one.
    private static func splitHostPort(_ authority: String) -> (String, String?) {
        if authority.hasPrefix("[") {
            guard let end = authority.firstIndex(of: "]") else { return (authority, nil) }
            let host = String(authority[...end])
            let rest = authority[authority.index(after: end)...]
            if rest.isEmpty { return (authority, nil) }
            return rest.hasPrefix(":") ? (host, String(rest.dropFirst())) : (authority, "!")
        }
        if authority.filter({ $0 == ":" }).count > 1 { return (authority, "!") }
        guard let colon = authority.firstIndex(of: ":") else { return (authority, nil) }
        return (String(authority[..<colon]), String(authority[authority.index(after: colon)...]))
    }

    private static func parseHost(_ text: String) -> Result<(host: String, literal: Bool), LanAddressProblem> {
        if text.hasPrefix("[") {
            guard text.hasSuffix("]"), text.count >= 2 else { return .failure(.host) }
            let inner = String(text.dropFirst().dropLast())
            let bare = inner.split(separator: "%", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) ?? inner
            guard LanAddress.ipv6(bare) != nil else { return .failure(.host) }
            return .success((inner, true))
        }
        guard !text.isEmpty, text.unicodeScalars.count <= 253 else { return .failure(.host) }
        var trimmed = Substring(text)
        while trimmed.hasSuffix(".") { trimmed = trimmed.dropLast() }
        let labels = text == "." ? [""] : trimmed.split(separator: ".", omittingEmptySubsequences: false).map(String.init)
        if labels.allSatisfy({ !$0.isEmpty && $0.allSatisfy { $0.isASCII && $0.isNumber } }) {
            return LanAddress.ipv4(text) != nil ? .success((text, true)) : .failure(.host)
        }
        guard labels.allSatisfy(isLabel) else { return .failure(.host) }
        return .success((text, false))
    }

    private static func isLabel(_ label: String) -> Bool {
        let scalars = Array(label.unicodeScalars)
        guard (1...63).contains(scalars.count) else { return false }
        func edge(_ scalar: Unicode.Scalar) -> Bool { scalar.isASCII && (scalar.properties.isAlphabetic || ("0"..."9").contains(scalar) || scalar == "_") }
        func inner(_ scalar: Unicode.Scalar) -> Bool { edge(scalar) || scalar == "-" }
        return edge(scalars.first!) && edge(scalars.last!) && scalars.allSatisfy(inner)
    }

    private static func parsePort(_ text: String) -> Int? {
        guard !text.isEmpty, text.count <= 5, text.allSatisfy({ $0.isASCII && $0.isNumber }),
              text.count == 1 || text.first != "0", let value = Int(text), (1...65535).contains(value) else { return nil }
        return value
    }
}

/// The outcome of resolving a target's host (LAN 5.1, 5.5).
public enum LanResolution: String, Equatable, Sendable {
    case ok, notFound, notLocal

    /// Every address local: `ok`; none: `notFound`; any other: `notLocal`.
    public static func check(_ addresses: [String], udp: Bool) -> LanResolution {
        guard !addresses.isEmpty else { return .notFound }
        return addresses.allSatisfy { LanAddress.isLocal($0, udp: udp) } ? .ok : .notLocal
    }
}
