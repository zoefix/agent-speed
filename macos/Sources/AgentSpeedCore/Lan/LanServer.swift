import Foundation
import Network

/// The monotonic clock of the feed, the lockout and the push rules; it keeps counting while the Mac sleeps.
public enum LanClock {
    public static func nowMs() -> Int64 { Int64(clock_gettime_nsec_np(CLOCK_MONOTONIC) / 1_000_000) }
}

/// LAN 4: one TCP port for HTTP, the WebSocket stream and the line protocol. Every member lives on `queue`; the public
/// methods may be called from any thread.
public final class LanServer: @unchecked Sendable {
    public enum State: String, Equatable, Sendable {
        case stopped, listening, portInUse, failed
    }

    public struct Status: Equatable, Sendable {
        public var state: State
        public var port: Int
        /// Distinct peers with an open stream or an authorized request in the last 10 s (DESIGN 14).
        public var devices: Int

        public init(state: State, port: Int, devices: Int) {
            self.state = state
            self.port = port
            self.devices = devices
        }

        public static let stopped = Status(state: .stopped, port: 0, devices: 0)
    }

    public struct Configuration: Equatable, Sendable {
        public var port: Int
        public var key: String
        /// The DNS-SD instance name (the frame's `name`).
        public var serviceName: String
        public var instance: String

        public init(port: Int, key: String, serviceName: String, instance: String) {
            self.port = port
            self.key = key
            self.serviceName = serviceName
            self.instance = instance
        }
    }

    public static let maxConnections = 16
    public static let serviceType = "_agentspeed._tcp"
    static let retryMs = 30_000
    static let recentMs: Int64 = 10_000

    /// Status changes, on `queue`.
    public var onStatus: (@Sendable (Status) -> Void)?

    let queue = DispatchQueue(label: "com.agentspeed.lan.server")
    private var listener: NWListener?
    private var configuration: Configuration?
    private var connections: [ObjectIdentifier: LanServerConnection] = [:]
    private var retry: DispatchWorkItem?
    private(set) var lockout = LanLockout()
    private var recent: [String: Int64] = [:]
    private var status = Status.stopped
    /// The latest frame's bytes (nil before the feed's first tick).
    private(set) var latest: Data?

    public init() {}

    /// Starts, restarts (a new port, name or instance) or stops (nil) the server. A new key closes the open streams.
    public func configure(_ configuration: Configuration?) {
        queue.async { self.apply(configuration) }
    }

    /// The tick's frame: kept for `/v1/live` and pushed to the streams by their rules.
    public func publish(frame: Data, changed: Bool) {
        queue.async {
            self.latest = frame
            let now = LanClock.nowMs()
            for connection in self.connections.values { connection.push(frame, changed: changed, nowMs: now) }
            self.updateStatus()
        }
    }

    /// Drops the latest frame (the feed stopped).
    public func clearFrame() {
        queue.async { self.latest = nil }
    }

    // MARK: Listening

    private func apply(_ next: Configuration?) {
        let previous = configuration
        configuration = next
        guard let next else {
            stopListening(code: 1001)
            setStatus(.stopped)
            return
        }
        if let previous, previous.port == next.port, previous.serviceName == next.serviceName,
           previous.instance == next.instance, listener != nil || retry != nil {
            if previous.key != next.key {
                for connection in connections.values where connection.isStream { connection.close(code: 1008) }
            }
            return
        }
        stopListening(code: 1001)
        listen()
    }

    private func listen() {
        guard let configuration, let port = NWEndpoint.Port(rawValue: UInt16(configuration.port)) else { return }
        retry?.cancel()
        retry = nil
        let parameters = NWParameters.tcp
        parameters.allowLocalEndpointReuse = true
        parameters.includePeerToPeer = false
        do {
            let listener = try NWListener(using: parameters, on: port)
            var txt = NWTXTRecord()
            txt["schema"] = "1"
            txt["id"] = configuration.instance
            listener.service = NWListener.Service(name: Self.serviceName(configuration.serviceName),
                                                  type: Self.serviceType, domain: nil, txtRecord: txt)
            listener.stateUpdateHandler = { [weak self] state in self?.listenerChanged(state, listener: listener) }
            listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
            self.listener = listener
            listener.start(queue: queue)
        } catch {
            failed(error)
        }
    }

    private func listenerChanged(_ state: NWListener.State, listener: NWListener) {
        guard listener === self.listener else { return }
        switch state {
        case .ready:
            setStatus(.listening)
        case .failed(let error):
            failed(error)
        default:
            break
        }
    }

    private func failed(_ error: Error) {
        listener?.cancel()
        listener = nil
        let inUse: Bool
        if case .posix(let code) = error as? NWError { inUse = code == .EADDRINUSE } else { inUse = false }
        setStatus(inUse ? .portInUse : .failed)
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.configuration != nil, self.listener == nil else { return }
            self.retry = nil
            self.listen()
        }
        retry = work
        queue.asyncAfter(deadline: .now() + .milliseconds(Self.retryMs), execute: work)
    }

    private func stopListening(code: UInt16) {
        retry?.cancel()
        retry = nil
        listener?.cancel()
        listener = nil
        for connection in connections.values { connection.close(code: code) }
        connections.removeAll()
        recent.removeAll()
    }

    /// At most 63 UTF-8 bytes, cut at a scalar boundary (LAN 4.1).
    static func serviceName(_ name: String) -> String {
        var result = String.UnicodeScalarView()
        var bytes = 0
        for scalar in name.unicodeScalars {
            let size = String(scalar).utf8.count
            guard bytes + size <= 63 else { break }
            result.append(scalar)
            bytes += size
        }
        return String(result)
    }

    // MARK: Connections

    private func accept(_ connection: NWConnection) {
        guard configuration != nil, let peer = Self.localPeer(connection.endpoint),
              connections.count < Self.maxConnections else {
            connection.cancel()
            return
        }
        let handler = LanServerConnection(connection: connection, peer: peer, server: self)
        connections[ObjectIdentifier(handler)] = handler
        handler.start()
    }

    func closed(_ connection: LanServerConnection) {
        connections[ObjectIdentifier(connection)] = nil
        updateStatus()
    }

    /// The peer's text when its address is local (LAN 2.3), else nil.
    static func localPeer(_ endpoint: NWEndpoint) -> String? {
        guard case .hostPort(let host, _) = endpoint else { return nil }
        switch host {
        case .ipv4(let address):
            let bytes = [UInt8](address.rawValue)
            guard LanAddress.isLocal(ipv4: bytes) else { return nil }
            return bytes.map(String.init).joined(separator: ".")
        case .ipv6(let address):
            let bytes = [UInt8](address.rawValue)
            guard LanAddress.isLocal(ipv6: bytes) else { return nil }
            if let mapped = address.asIPv4 { return [UInt8](mapped.rawValue).map(String.init).joined(separator: ".") }
            return "\(IPv6Address(address.rawValue) ?? address)"
        default:
            return nil
        }
    }

    // MARK: Requests

    var key: String { configuration?.key ?? "" }

    func respond(to head: LanRequestHead, peer: String) -> LanResponse {
        let response = LanResponse.respond(to: head, peer: peer, nowMs: LanClock.nowMs(), key: key,
                                           hasFrame: latest != nil, lockout: &lockout)
        if response.status == 200 || response.status == 101 { noteAuthorized(peer) }
        return response
    }

    func hello(_ line: Data, peer: String) -> String? {
        let error = LanError.hello(line, peer: peer, nowMs: LanClock.nowMs(), key: key, lockout: &lockout)
        if error == nil { noteAuthorized(peer) }
        return error
    }

    private func noteAuthorized(_ peer: String) {
        recent[peer] = LanClock.nowMs()
        updateStatus()
    }

    // MARK: Status

    private func setStatus(_ state: State) {
        status.state = state
        status.port = configuration?.port ?? 0
        if state != .listening { status.devices = 0 }
        onStatus?(status)
    }

    private func updateStatus() {
        guard status.state == .listening else { return }
        let now = LanClock.nowMs()
        recent = recent.filter { now - $0.value < Self.recentMs }
        let devices = Set(recent.keys).union(connections.values.filter(\.isStream).map(\.peer)).count
        guard devices != status.devices else { return }
        status.devices = devices
        onStatus?(status)
    }
}

/// One connection of the server: the first line decides HTTP, WebSocket or the line protocol (LAN 4.2–4.7).
final class LanServerConnection: @unchecked Sendable {
    enum Mode { case sniffing, http, websocket, lines, closing }

    static let headTimeoutMs = 5000
    static let unsentLimit = 65_536

    let peer: String
    private let connection: NWConnection
    private weak var server: LanServer?
    private var buffer = Data()
    private var mode = Mode.sniffing
    private var deadline: DispatchWorkItem?
    private var receiver = LanPushReceiver(.stream)
    private var unsent = 0
    private var sending = 0

    init(connection: NWConnection, peer: String, server: LanServer) {
        self.connection = connection
        self.peer = peer
        self.server = server
    }

    var isStream: Bool { mode == .websocket || mode == .lines }

    func start() {
        guard let server else { return }
        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .failed, .cancelled: self?.finish()
            default: break
            }
        }
        connection.start(queue: server.queue)
        let deadline = DispatchWorkItem { [weak self] in
            guard let self, self.mode == .sniffing || self.mode == .http else { return }
            self.cancel()
        }
        self.deadline = deadline
        server.queue.asyncAfter(deadline: .now() + .milliseconds(Self.headTimeoutMs), execute: deadline)
        receive()
    }

    private func receive() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16_384) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            if let data, !data.isEmpty { self.received(data) }
            if isComplete || error != nil {
                // A response still being written closes itself; anything else ends here.
                if self.mode != .closing { self.cancel() }
                return
            }
            if self.mode != .closing { self.receive() }
        }
    }

    private func received(_ data: Data) {
        switch mode {
        case .sniffing, .http:
            buffer.append(data)
            if mode == .sniffing {
                guard let kind = LanFirstLine.classify(buffer) else { return }
                switch kind {
                case .close: return cancel()
                case .http: mode = .http
                case .line: return helloLine()
                }
            }
            if buffer.count > LanFirstLine.limit, LanRequestHead.parse(buffer) == nil { return cancel() }
            guard let (head, _) = LanRequestHead.parse(buffer) else { return }
            buffer.removeAll()
            request(head)
        case .websocket:
            buffer.append(data)
            readClientFrames()
        case .lines, .closing:
            break
        }
    }

    // MARK: HTTP and WebSocket

    private func request(_ head: LanRequestHead) {
        guard let server else { return }
        deadline?.cancel()
        let response = server.respond(to: head, peer: peer)
        if response.status == 101 {
            mode = .websocket
            send(response.data(frame: nil))
            if let frame = server.latest { push(frame, changed: true, nowMs: LanClock.nowMs(), first: true) }
            return
        }
        mode = .closing
        connection.send(content: response.data(frame: server.latest), contentContext: .finalMessage, isComplete: true,
                        completion: .contentProcessed { [weak self] _ in self?.cancel() })
    }

    private func readClientFrames() {
        while mode == .websocket {
            let (action, length) = LanWebSocket.read(buffer)
            switch action {
            case .incomplete:
                return
            case .ignore:
                buffer.removeFirst(length)
            case .pong(let payload):
                buffer.removeFirst(length)
                send(LanWebSocket.frame(opcode: 0xA, payload))
            case .echoClose(let code):
                close(code: code)
            case .close(let code):
                close(code: code)
            }
        }
    }

    // MARK: Line protocol

    private func helloLine() {
        guard let server, let end = buffer.firstIndex(of: 0x0A) else { return }
        deadline?.cancel()
        var line = buffer[buffer.startIndex..<end]
        if line.last == 0x0D { line = line.dropLast() }
        buffer.removeAll()
        if let error = server.hello(Data(line), peer: peer) {
            mode = .closing
            connection.send(content: LanError.data(error) + Data([0x0A]), contentContext: .finalMessage,
                            isComplete: true, completion: .contentProcessed { [weak self] _ in self?.cancel() })
            return
        }
        mode = .lines
        if let frame = server.latest { push(frame, changed: true, nowMs: LanClock.nowMs(), first: true) }
    }

    // MARK: Pushing

    /// `first`: the frame right after the upgrade or hello, which goes out behind the 101 head.
    func push(_ frame: Data, changed: Bool, nowMs: Int64, first: Bool = false) {
        guard isStream, receiver.tick(nowMs: nowMs, changed: changed, busy: !first && sending > 0) else { return }
        send(mode == .websocket ? LanWebSocket.text(frame) : frame + Data([0x0A]))
    }

    private func send(_ data: Data) {
        unsent += data.count
        guard unsent <= Self.unsentLimit else { return cancel() }
        sending += 1
        connection.send(content: data, completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            self.unsent -= data.count
            self.sending -= 1
            if error != nil { self.cancel() }
        })
    }

    /// Ends a stream with a WebSocket close frame (or just closes the line protocol and HTTP).
    func close(code: UInt16?) {
        guard mode != .closing else { return }
        let wasWebSocket = mode == .websocket
        mode = .closing
        guard wasWebSocket else { return cancel() }
        connection.send(content: LanWebSocket.close(code), contentContext: .finalMessage, isComplete: true,
                        completion: .contentProcessed { [weak self] _ in self?.cancel() })
    }

    private func cancel() {
        mode = .closing
        deadline?.cancel()
        connection.cancel()
    }

    private func finish() {
        deadline?.cancel()
        mode = .closing
        server?.closed(self)
    }
}
