import CryptoKit
import Foundation

/// The server side of RFC 6455 as LAN 4.5 needs it: the accept value, unmasked server frames and what to do with
/// each client frame.
public enum LanWebSocket {
    public static let clientLimit = 4096
    static let guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

    public static func accept(_ key: String) -> String {
        Data(Insecure.SHA1.hash(data: Data((key + guid).utf8))).base64EncodedString()
    }

    /// One unfragmented, unmasked frame.
    public static func frame(opcode: UInt8, _ payload: Data) -> Data {
        var head = Data([0x80 | opcode])
        let size = payload.count
        if size < 126 {
            head.append(UInt8(size))
        } else if size <= 0xFFFF {
            head.append(126)
            head.append(contentsOf: [UInt8(size >> 8), UInt8(size & 0xFF)])
        } else {
            head.append(127)
            head.append(contentsOf: (0..<8).reversed().map { UInt8((UInt64(size) >> (UInt64($0) * 8)) & 0xFF) })
        }
        return head + payload
    }

    public static func text(_ payload: Data) -> Data { frame(opcode: 0x1, payload) }

    /// A close frame with the code, or none.
    public static func close(_ code: UInt16?) -> Data {
        frame(opcode: 0x8, code.map { Data([UInt8($0 >> 8), UInt8($0 & 0xFF)]) } ?? Data())
    }

    /// What the server does with a client frame.
    public enum Action: Equatable, Sendable {
        /// More bytes are needed.
        case incomplete
        case pong(Data)
        /// Echo a close with this code (none when the client sent none), then close.
        case echoClose(UInt16?)
        /// Close with this code (1002 protocol error, 1009 too big).
        case close(UInt16)
        case ignore
    }

    /// The decision for the frame at the start of `buffer` and how many bytes it used (0 while incomplete or for a
    /// closing decision made from the header alone).
    public static func read(_ buffer: Data) -> (action: Action, length: Int) {
        let bytes = [UInt8](buffer.prefix(14))
        guard bytes.count >= 2 else { return (.incomplete, 0) }
        let fin = bytes[0] & 0x80 != 0, rsv = bytes[0] & 0x70, opcode = bytes[0] & 0x0F
        let masked = bytes[1] & 0x80 != 0
        var size = UInt64(bytes[1] & 0x7F)
        var position = 2
        if size == 126 {
            guard bytes.count >= 4 else { return (.incomplete, 0) }
            size = UInt64(bytes[2]) << 8 | UInt64(bytes[3])
            position = 4
        } else if size == 127 {
            guard bytes.count >= 10 else { return (.incomplete, 0) }
            size = bytes[2..<10].reduce(0) { $0 << 8 | UInt64($1) }
            position = 10
        }
        guard rsv == 0, [0x0, 0x1, 0x2, 0x8, 0x9, 0xA].contains(opcode), masked else { return (.close(1002), 0) }
        if opcode >= 0x8 && (!fin || size > 125) { return (.close(1002), 0) }
        if opcode < 0x8 && size > UInt64(clientLimit) { return (.close(1009), 0) }
        let total = position + 4 + Int(size)
        guard buffer.count >= total else { return (.incomplete, 0) }
        let all = [UInt8](buffer.prefix(total))
        let mask = all[position..<position + 4]
        let payload = Data(all[(position + 4)...].enumerated().map { $0.element ^ mask[mask.startIndex + $0.offset % 4] })
        switch opcode {
        case 0x9:
            return (.pong(payload), total)
        case 0x8:
            if payload.count == 1 { return (.close(1002), total) }
            let code = payload.count >= 2 ? UInt16(payload[payload.startIndex]) << 8 | UInt16(payload[payload.startIndex + 1]) : nil
            return (.echoClose(code), total)
        default:
            return (.ignore, total)
        }
    }
}

/// A push receiver (LAN 4.7): the first tick sends; then a changed frame once the minimum interval passed, or the
/// latest frame after the heartbeat interval; a busy receiver skips and remembers a change.
public struct LanPushReceiver: Equatable, Sendable {
    public enum Kind: Sendable {
        case stream, udp, http

        var interval: (minimum: Int64, heartbeat: Int64) {
            switch self {
            case .stream: (500, 5000)
            case .udp: (500, 1000)
            case .http: (1000, 10_000)
            }
        }
    }

    /// Ticks are not exactly 500 ms apart: an interval counts as passed this much early (LAN 4.7).
    public static let toleranceMs: Int64 = 100

    public let kind: Kind
    private var lastSentMs: Int64?
    private var held = false

    public init(_ kind: Kind) {
        self.kind = kind
    }

    /// Whether to send the tick's frame at `nowMs` (monotonic).
    public mutating func tick(nowMs: Int64, changed: Bool, busy: Bool = false) -> Bool {
        guard let last = lastSentMs else {
            if busy { return false }
            lastSentMs = nowMs
            return true
        }
        held = held || changed
        guard !busy else { return false }
        let (minimum, heartbeat) = kind.interval
        let elapsed = nowMs - last + Self.toleranceMs
        guard (held && elapsed >= minimum) || elapsed >= heartbeat else { return false }
        lastSentMs = nowMs
        held = false
        return true
    }
}

/// The HTTP target's wait after `failures` failures in a row (LAN 5.3): 2, 4, 8, 16, then 30 s.
public enum LanBackoff {
    static let steps: [Int64] = [2000, 4000, 8000, 16_000, 30_000]

    public static func ms(failures: Int) -> Int64 {
        failures > 0 ? steps[min(failures, steps.count) - 1] : 0
    }
}
