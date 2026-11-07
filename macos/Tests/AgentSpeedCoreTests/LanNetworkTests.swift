import Darwin
import Foundation
import Testing
@testable import AgentSpeedCore

/// The server and the targets over real sockets on the loopback interface (LAN 4, 5).
@Suite(.serialized) struct LanNetworkTests {
    static let key = "K7Q4M2X9PT3D"

    static func frame(seq: Int64 = 1, output: Double = 75.9) -> ReportJSON {
        LanFrame.build(identity: LanIdentity(instance: "3f2504e0-4f89-41d3-9a0c-0305e82c3301", name: "Test Mac",
                                             run: "9b1c4e7a2f6d8e03"),
                       seq: seq, atMs: 1_794_042_000_250,
                       live: LanLiveValues(overall: LanValues(outputTokS: output, estimatedShare: 0.42,
                                                              sessions: LanSessions(active: 1, generating: 1))),
                       history: [output])
    }

    /// A started server on a free port with one frame published.
    static func server() async throws -> (LanServer, Int) {
        for _ in 0..<20 {
            let port = Int.random(in: 40_000...48_000)
            let server = LanServer()
            let states = StateBox<LanServer.State>()
            server.onStatus = { states.set($0.state) }
            server.configure(.init(port: port, key: key, serviceName: "Agent Speed Test", instance: "test"))
            if try await states.wait(until: { $0 != .stopped }) == .listening {
                server.publish(frame: frame().compactData, changed: true)
                try await Task.sleep(for: .milliseconds(50))
                return (server, port)
            }
            server.configure(nil)
        }
        throw Failure.noPort
    }

    enum Failure: Error { case noPort, timeout }

    @Test func liveAnswersWithTheFrameOrAnError() async throws {
        let (server, port) = try await Self.server()
        defer { server.configure(nil) }
        let ok = try await Socket.exchange(port: port, "GET /v1/live HTTP/1.1\r\nAuthorization: Bearer K7Q4-M2X9-PT3D\r\n\r\n")
        #expect(ok.hasPrefix("HTTP/1.1 200 OK\r\n"))
        #expect(ok.contains("Cache-Control: no-store\r\n"))
        #expect(ok.hasSuffix(String(decoding: Self.frame().compactData, as: UTF8.self)))
        let query = try await Socket.exchange(port: port, "GET /v1/live?key=k7q4-m2x9-pt3d HTTP/1.1\r\n\r\n")
        #expect(query.hasPrefix("HTTP/1.1 200 OK"))
        let missing = try await Socket.exchange(port: port, "GET /v1/live HTTP/1.1\r\n\r\n")
        #expect(missing.hasPrefix("HTTP/1.1 401 Unauthorized"))
        #expect(missing.hasSuffix(#"{"schema":1,"type":"error","error":"unauthorized"}"#))
        #expect(try await Socket.exchange(port: port, "GET /nope HTTP/1.1\r\n\r\n").hasPrefix("HTTP/1.1 404"))
        #expect(try await Socket.exchange(port: port, "DELETE /v1/live HTTP/1.1\r\n\r\n").hasPrefix("HTTP/1.1 405"))
        #expect(try await Socket.exchange(port: port, "hello there\n").isEmpty)
    }

    @Test func webSocketStreamsFramesAndAnswersPingAndClose() async throws {
        let (server, port) = try await Self.server()
        defer { server.configure(nil) }
        let socket = try Socket(port: port)
        defer { socket.close() }
        try socket.send("GET /v1/stream HTTP/1.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                        + "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n"
                        + "Authorization: Bearer K7Q4M2X9PT3D\r\n\r\n")
        let head = try await socket.read(until: Data("\r\n\r\n".utf8))
        #expect(String(decoding: head, as: UTF8.self).contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo="))
        let first = try await socket.readFrame()
        #expect(first.opcode == 1 && first.payload == Self.frame().compactData)
        // A changed frame waits for the minimum interval (LAN 4.7): the next tick after 500 ms sends it.
        server.publish(frame: Self.frame(seq: 2, output: 80).compactData, changed: true)
        try await Task.sleep(for: .milliseconds(550))
        server.publish(frame: Self.frame(seq: 2, output: 80).compactData, changed: false)
        let second = try await socket.readFrame()
        #expect(second.payload == Self.frame(seq: 2, output: 80).compactData)
        let mask: [UInt8] = [1, 2, 3, 4]
        try socket.send(Data([0x89, 0x82] + mask + [UInt8(ascii: "h") ^ 1, UInt8(ascii: "i") ^ 2]))
        let pong = try await socket.readFrame()
        #expect(pong.opcode == 0xA && pong.payload == Data("hi".utf8))
        try socket.send(Data([0x88, 0x82] + mask + [0x03 ^ 1, 0xE8 ^ 2]))
        let close = try await socket.readFrame()
        #expect(close.opcode == 0x8 && close.payload == Data([0x03, 0xE8]))
    }

    @Test func unmaskedClientFramesCloseWithAProtocolError() async throws {
        let (server, port) = try await Self.server()
        defer { server.configure(nil) }
        let socket = try Socket(port: port)
        defer { socket.close() }
        try socket.send("GET /v1/stream?key=K7Q4M2X9PT3D HTTP/1.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                        + "Sec-WebSocket-Key: AQIDBAUGBwgJCgsMDQ4PEA==\r\nSec-WebSocket-Version: 13\r\n\r\n")
        _ = try await socket.read(until: Data("\r\n\r\n".utf8))
        _ = try await socket.readFrame()
        try socket.send(Data([0x81, 0x01, 0x78]))
        let close = try await socket.readFrame()
        #expect(close.opcode == 0x8 && close.payload == Data([0x03, 0xEA]))
    }

    @Test func lineProtocolStreamsAfterAValidHello() async throws {
        let (server, port) = try await Self.server()
        defer { server.configure(nil) }
        let socket = try Socket(port: port)
        defer { socket.close() }
        try socket.send(#"{"type":"hello","key":"K7Q4-M2X9-PT3D"}"# + "\n")
        let line = try await socket.read(until: Data([0x0A]))
        #expect(line == Self.frame().compactData + Data([0x0A]))
        let wrong = try await Socket.exchange(port: port, #"{"type":"hello","key":"nope"}"# + "\n")
        #expect(wrong == #"{"schema":1,"type":"error","error":"unauthorized"}"# + "\n")
    }

    @Test func aNewKeyClosesStreamsWith1008() async throws {
        let (server, port) = try await Self.server()
        defer { server.configure(nil) }
        let socket = try Socket(port: port)
        defer { socket.close() }
        try socket.send("GET /v1/stream?key=K7Q4M2X9PT3D HTTP/1.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                        + "Sec-WebSocket-Key: AQIDBAUGBwgJCgsMDQ4PEA==\r\nSec-WebSocket-Version: 13\r\n\r\n")
        _ = try await socket.read(until: Data("\r\n\r\n".utf8))
        _ = try await socket.readFrame()
        server.configure(.init(port: port, key: "AAAABBBBCCCC", serviceName: "Agent Speed Test", instance: "test"))
        let close = try await socket.readFrame()
        #expect(close.opcode == 0x8 && close.payload == Data([0x03, 0xF0]))
    }

    @Test func udpTargetSendsSignedDatagrams() async throws {
        let receiver = try UDPReceiver()
        defer { receiver.close() }
        let targets = LanTargets()
        let statuses = StateBox<LanTargetState>()
        targets.onStatus = { _, status in statuses.set(status.state) }
        let target = LanDocument.Target(kind: .udp, address: "127.0.0.1:\(receiver.port)", key: "s3cret-phrase")
        targets.configure([target])
        targets.publish(frame: Self.frame(), changed: true)
        let datagram = try await receiver.receive()
        let body = Self.frame().compactData
        #expect(datagram == body + Data(("\n" + LanSignature.line(key: "s3cret-phrase", body: body)).utf8))
        #expect(try await statuses.wait(until: { $0 == .ok }) == .ok)
        targets.configure([])
    }

    @Test func httpTargetPostsAndReportsTheOutcome() async throws {
        let ok = try HTTPReceiver(status: "204 No Content")
        defer { ok.close() }
        let targets = LanTargets()
        let statuses = StateBox<LanTargetStatus>()
        targets.onStatus = { _, status in statuses.set(status) }
        targets.configure([LanDocument.Target(kind: .http, address: "http://127.0.0.1:\(ok.port)/hook?x=1", key: "k")])
        targets.publish(frame: Self.frame(), changed: true)
        let request = try await ok.receive()
        let text = String(decoding: request, as: UTF8.self)
        #expect(text.hasPrefix("POST /hook?x=1 HTTP/1.1\r\nHost: 127.0.0.1:\(ok.port)\r\n"))
        #expect(text.contains("X-AgentSpeed-Signature: \(LanSignature.line(key: "k", body: Self.frame().compactData))\r\n"))
        #expect(text.hasSuffix(String(decoding: Self.frame().compactData, as: UTF8.self)))
        #expect(try await statuses.wait(until: { $0.state != .waiting }).state == .ok)

        let missing = try HTTPReceiver(status: "404 Not Found")
        defer { missing.close() }
        targets.configure([LanDocument.Target(kind: .http, address: "http://127.0.0.1:\(missing.port)/")])
        targets.publish(frame: Self.frame(), changed: true)
        _ = try await missing.receive()
        let failed = try await statuses.wait(until: { $0.state == .httpStatus })
        #expect(failed.httpStatus == 404 && failed.retryAt != nil)

        let closedPort = missing.port
        missing.close()
        let unreachable = LanTargets()
        let outcome = StateBox<LanTargetStatus>()
        unreachable.test(LanDocument.Target(kind: .http, address: "http://127.0.0.1:\(closedPort)/"), frame: Self.frame()) {
            outcome.set($0)
        }
        #expect(try await outcome.wait(until: { _ in true }).state == .unreachable)
        targets.configure([])
    }

    @Test func publicTargetsAreNotLocal() async throws {
        let targets = LanTargets()
        let outcome = StateBox<LanTargetStatus>()
        targets.test(LanDocument.Target(kind: .udp, address: "8.8.8.8:7247"), frame: Self.frame()) { outcome.set($0) }
        #expect(try await outcome.wait(until: { _ in true }).state == .notLocal)
    }
}

// MARK: - Helpers

/// The latest value from a callback, awaited without holding a thread of the shared pool.
final class StateBox<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var values: [Value] = []

    func set(_ value: Value) { lock.withLock { values.append(value) } }

    func wait(until match: (Value) -> Bool, seconds: Double = 8) async throws -> Value {
        let deadline = Date().addingTimeInterval(seconds)
        while Date() < deadline {
            if let found = lock.withLock({ values.last(where: match) }) { return found }
            try await Task.sleep(for: .milliseconds(20))
        }
        throw LanNetworkTests.Failure.timeout
    }
}

/// A blocking TCP client on loopback; reads run on a global queue, not the cooperative pool.
final class Socket: @unchecked Sendable {
    let fd: Int32
    private var buffer = Data()

    init(port: Int) throws {
        fd = Darwin.socket(AF_INET, SOCK_STREAM, 0)
        var timeout = timeval(tv_sec: 5, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        var on: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(port).bigEndian)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let result = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        guard result == 0 else { throw LanNetworkTests.Failure.timeout }
    }

    func send(_ text: String) throws { try send(Data(text.utf8)) }

    func send(_ data: Data) throws {
        let sent = data.withUnsafeBytes { Darwin.send(fd, $0.baseAddress, $0.count, 0) }
        guard sent == data.count else { throw LanNetworkTests.Failure.timeout }
    }

    func close() { Darwin.close(fd) }

    /// Reads until `marker` (included) or the end.
    func read(until marker: Data) async throws -> Data {
        while true {
            if let range = buffer.range(of: marker) {
                let out = buffer[..<range.upperBound]
                buffer.removeSubrange(..<range.upperBound)
                return Data(out)
            }
            let chunk = try await readChunk()
            if chunk.isEmpty { let out = buffer; buffer.removeAll(); return out }
            buffer.append(chunk)
        }
    }

    func readFrame() async throws -> (opcode: UInt8, payload: Data) {
        while true {
            if buffer.count >= 2 {
                let size = Int(buffer[buffer.startIndex + 1] & 0x7F)
                let header = size == 126 ? 4 : size == 127 ? 10 : 2
                if buffer.count >= header {
                    var length = size
                    if size == 126 {
                        length = Int(buffer[buffer.startIndex + 2]) << 8 | Int(buffer[buffer.startIndex + 3])
                    }
                    if buffer.count >= header + length {
                        let opcode = buffer[buffer.startIndex] & 0x0F
                        let payload = Data(buffer[(buffer.startIndex + header)..<(buffer.startIndex + header + length)])
                        buffer.removeFirst(header + length)
                        return (opcode, payload)
                    }
                }
            }
            let chunk = try await readChunk()
            guard !chunk.isEmpty else { throw LanNetworkTests.Failure.timeout }
            buffer.append(chunk)
        }
    }

    private func readChunk() async throws -> Data {
        let fd = fd
        return try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global().async {
                var bytes = [UInt8](repeating: 0, count: 65_536)
                let count = recv(fd, &bytes, bytes.count, 0)
                if count < 0 { continuation.resume(throwing: LanNetworkTests.Failure.timeout) }
                else { continuation.resume(returning: Data(bytes.prefix(count))) }
            }
        }
    }

    /// Sends `text` and reads to the end (the server closes after one answer).
    static func exchange(port: Int, _ text: String) async throws -> String {
        let socket = try Socket(port: port)
        defer { socket.close() }
        try socket.send(text)
        var all = Data()
        while true {
            let chunk = try await socket.readChunk()
            if chunk.isEmpty { break }
            all.append(chunk)
        }
        return String(decoding: all, as: UTF8.self)
    }
}

/// A bound UDP socket on 127.0.0.1.
final class UDPReceiver: @unchecked Sendable {
    let fd: Int32
    let port: Int

    init() throws {
        fd = Darwin.socket(AF_INET, SOCK_DGRAM, 0)
        var timeout = timeval(tv_sec: 5, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        port = try Self.bind(fd)
    }

    static func bind(_ fd: Int32) throws -> Int {
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let bound = withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { pointer -> Int32 in
                guard Darwin.bind(fd, pointer, length) == 0 else { return -1 }
                return getsockname(fd, pointer, &length)
            }
        }
        guard bound == 0 else { throw LanNetworkTests.Failure.noPort }
        return Int(UInt16(bigEndian: address.sin_port))
    }

    func receive() async throws -> Data {
        let fd = fd
        return try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global().async {
                var bytes = [UInt8](repeating: 0, count: 65_536)
                let count = recv(fd, &bytes, bytes.count, 0)
                if count < 0 { continuation.resume(throwing: LanNetworkTests.Failure.timeout) }
                else { continuation.resume(returning: Data(bytes.prefix(count))) }
            }
        }
    }

    func close() { Darwin.close(fd) }
}

/// A one-request-at-a-time HTTP server on 127.0.0.1 that answers with a fixed status.
final class HTTPReceiver: @unchecked Sendable {
    let fd: Int32
    let port: Int
    private let status: String
    private var closed = false

    init(status: String) throws {
        self.status = status
        fd = Darwin.socket(AF_INET, SOCK_STREAM, 0)
        port = try UDPReceiver.bind(fd)
        listen(fd, 4)
    }

    /// The next request (head and body).
    func receive() async throws -> Data {
        let fd = fd
        let status = status
        return try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global().async {
                let client = accept(fd, nil, nil)
                guard client >= 0 else { return continuation.resume(throwing: LanNetworkTests.Failure.timeout) }
                var timeout = timeval(tv_sec: 5, tv_usec: 0)
                setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
                var all = Data()
                var bytes = [UInt8](repeating: 0, count: 65_536)
                while true {
                    let count = recv(client, &bytes, bytes.count, 0)
                    if count <= 0 { break }
                    all.append(contentsOf: bytes.prefix(count))
                    if let end = all.range(of: Data("\r\n\r\n".utf8)) {
                        let head = String(decoding: all[..<end.lowerBound], as: UTF8.self)
                        let length = head.split(separator: "\r\n").first { $0.lowercased().hasPrefix("content-length:") }
                            .flatMap { Int($0.split(separator: ":")[1].trimmingCharacters(in: .whitespaces)) } ?? 0
                        if all.count >= end.upperBound + length { break }
                    }
                }
                let reply = Data("HTTP/1.1 \(status)\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".utf8)
                _ = reply.withUnsafeBytes { Darwin.send(client, $0.baseAddress, $0.count, 0) }
                Darwin.close(client)
                continuation.resume(returning: all)
            }
        }
    }

    func close() {
        guard !closed else { return }
        closed = true
        Darwin.close(fd)
    }
}
