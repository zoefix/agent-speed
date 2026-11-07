import Foundation

/// The server's first line (LAN 4.2).
public enum LanFirstLine: Equatable, Sendable {
    case http, line, close

    public static let limit = 8192

    /// The protocol for the bytes so far; nil while no `\n` arrived and fewer than 8192 bytes did (wait for more).
    /// With `final` (the 5 s passed or the peer stopped sending) a missing `\n` closes.
    public static func classify(_ data: Data, final: Bool = false) -> LanFirstLine? {
        guard let end = data.firstIndex(of: 0x0A) else {
            return final || data.count >= limit ? .close : nil
        }
        let offset = end - data.startIndex
        guard offset < limit else { return .close }
        var line = data[data.startIndex..<end]
        if line.last == 0x0D { line = line.dropLast() }
        guard let text = String(data: line, encoding: .utf8) else { return .close }
        if LanRequestLine(text) != nil { return .http }
        return text.hasPrefix("{") ? .line : .close
    }
}

/// `METHOD SP target SP HTTP/1.x`.
struct LanRequestLine {
    var method: String
    var target: String

    init?(_ text: String) {
        let parts = text.split(separator: " ", omittingEmptySubsequences: false)
        guard parts.count == 3, !parts[0].isEmpty, parts[0].unicodeScalars.allSatisfy({ ("A"..."Z").contains($0) }),
              !parts[1].isEmpty, !parts[1].contains(where: { $0.isWhitespace }),
              parts[2] == "HTTP/1.1" || parts[2] == "HTTP/1.0" else { return nil }
        method = String(parts[0])
        target = String(parts[1])
    }
}

/// A request head (LAN 4.3).
public struct LanRequestHead: Equatable, Sendable {
    public var method: String
    public var target: String
    /// Lower-cased names; the first of a repeated header counts.
    public var headers: [String: String]
    /// A header line without `:`.
    public var malformed: Bool

    public var path: String { String(target.prefix { $0 != "?" }) }

    /// The bytes up to the empty line (`\r\n\r\n` or `\n\n`, whichever comes first), with the head's length; nil while
    /// incomplete (or when the head is beyond 8192 bytes, which the caller treats as a close).
    public static func parse(_ data: Data) -> (head: LanRequestHead, length: Int)? {
        let bytes = [UInt8](data)
        var end: Int?
        var separator = 0
        var index = 0
        while index + 1 < bytes.count {
            if bytes[index] == 0x0A && bytes[index + 1] == 0x0A { end = index; separator = 2; break }
            if index + 3 < bytes.count, bytes[index] == 0x0D, bytes[index + 1] == 0x0A, bytes[index + 2] == 0x0D,
               bytes[index + 3] == 0x0A { end = index; separator = 4; break }
            index += 1
        }
        guard let end, end + separator <= LanFirstLine.limit else { return nil }
        let text = String(decoding: bytes[0..<end], as: UTF8.self)
        // By scalars: Swift counts "\r\n" as one character, so splitting characters on "\n" would miss it.
        var lines = text.components(separatedBy: "\n").map { line -> String in
            line.unicodeScalars.last == "\r" ? String(String.UnicodeScalarView(line.unicodeScalars.dropLast())) : line
        }
        guard !lines.isEmpty, let request = LanRequestLine(lines.removeFirst()) else { return nil }
        var head = LanRequestHead(method: request.method, target: request.target, headers: [:], malformed: false)
        for line in lines {
            guard let colon = line.firstIndex(of: ":") else {
                head.malformed = true
                break
            }
            let name = line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            if head.headers[name] == nil { head.headers[name] = value }
        }
        return (head, end + separator)
    }
}

/// The server's answer to a request head (LAN 4.3).
public struct LanResponse: Equatable, Sendable {
    public enum Body: Equatable, Sendable { case frame, error, none }

    public var status: Int
    public var error: String?
    /// The headers beyond the fixed ones, in order.
    public var headers: [(String, String)]
    public var body: Body

    public static func == (lhs: LanResponse, rhs: LanResponse) -> Bool {
        lhs.status == rhs.status && lhs.error == rhs.error && lhs.body == rhs.body
            && lhs.headers.map(\.0) == rhs.headers.map(\.0) && lhs.headers.map(\.1) == rhs.headers.map(\.1)
    }

    static let reasons = [101: "Switching Protocols", 200: "OK", 400: "Bad Request", 401: "Unauthorized",
                          404: "Not Found", 405: "Method Not Allowed", 426: "Upgrade Required",
                          429: "Too Many Requests", 503: "Service Unavailable"]

    static func error(_ status: Int, _ code: String, _ headers: [(String, String)] = []) -> LanResponse {
        LanResponse(status: status, error: code, headers: headers, body: .error)
    }

    /// Checks in the order of LAN 4.3; failures and successes reach the lockout.
    public static func respond(to head: LanRequestHead, peer: String, nowMs: Int64, key: String, hasFrame: Bool,
                               lockout: inout LanLockout) -> LanResponse {
        if head.malformed { return .error(400, "badRequest") }
        if let left = lockout.secondsLeft(peer, nowMs: nowMs) {
            return .error(429, "tooManyAttempts", [("Retry-After", String(left))])
        }
        guard head.method == "GET" else { return .error(405, "methodNotAllowed", [("Allow", "GET")]) }
        let path = head.path
        guard path == "/v1/live" || path == "/v1/stream" else { return .error(404, "notFound") }
        guard LanAccess.matches(LanAccess.requestKey(target: head.target, headers: head.headers), key: key) else {
            lockout.fail(peer, nowMs: nowMs)
            return .error(401, "unauthorized", [("WWW-Authenticate", "Bearer realm=\"agent-speed\"")])
        }
        lockout.succeed(peer)
        if path == "/v1/live" {
            return hasFrame ? LanResponse(status: 200, error: nil, headers: [], body: .frame) : .error(503, "starting")
        }
        let headers = head.headers
        guard tokens(headers["upgrade"]).contains("websocket"), tokens(headers["connection"]).contains("upgrade") else {
            return .error(426, "upgradeRequired", [("Upgrade", "websocket")])
        }
        guard headers["sec-websocket-version"] == "13", let wsKey = headers["sec-websocket-key"],
              Data(base64Encoded: wsKey)?.count == 16 else {
            return .error(400, "badRequest", [("Sec-WebSocket-Version", "13")])
        }
        return LanResponse(status: 101, error: nil,
                           headers: [("Upgrade", "websocket"), ("Connection", "Upgrade"),
                                     ("Sec-WebSocket-Accept", LanWebSocket.accept(wsKey))],
                           body: .none)
    }

    /// The response's bytes; `frame` is the latest frame for a 200.
    public func data(frame: Data?) -> Data {
        var lines = ["HTTP/1.1 \(status) \(Self.reasons[status] ?? "")"]
        if status == 101 {
            lines += headers.map { "\($0.0): \($0.1)" }
            return Data((lines.joined(separator: "\r\n") + "\r\n\r\n").utf8)
        }
        let content = error.map(LanError.data) ?? frame ?? Data()
        lines += ["Content-Type: application/json; charset=utf-8", "Content-Length: \(content.count)",
                  "Cache-Control: no-store", "Connection: close"]
        lines += headers.map { "\($0.0): \($0.1)" }
        return Data((lines.joined(separator: "\r\n") + "\r\n\r\n").utf8) + content
    }

    private static func tokens(_ value: String?) -> Set<String> {
        Set((value ?? "").split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
            .filter { !$0.isEmpty })
    }
}

/// The error object (LAN 3.6).
public enum LanError {
    public static func data(_ code: String) -> Data {
        ReportJSON.object(["schema": .int(1), "type": .string("error"), "error": .string(code)]).compactData
    }

    /// The line protocol's hello (LAN 4.6): nil when valid, else the error code; failures reach the lockout.
    public static func hello(_ line: Data, peer: String, nowMs: Int64, key: String, lockout: inout LanLockout) -> String? {
        if lockout.secondsLeft(peer, nowMs: nowMs) != nil { return "tooManyAttempts" }
        guard let value = try? JSONSerialization.jsonObject(with: line, options: [.fragmentsAllowed]),
              let object = value as? [String: Any], object["type"] as? String == "hello",
              let given = object["key"] as? String else { return "badRequest" }
        guard LanAccess.matches(given, key: key) else {
            lockout.fail(peer, nowMs: nowMs)
            return "unauthorized"
        }
        lockout.succeed(peer)
        return nil
    }
}
