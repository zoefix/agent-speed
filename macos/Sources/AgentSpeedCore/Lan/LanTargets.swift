import Darwin
import Foundation
import Network

/// A target's state for its row (LAN 5.5).
public enum LanTargetState: String, Error, Equatable, Sendable {
    case waiting, ok, notFound, notLocal, unreachable, httpStatus, failed, off, networkOff
}

public struct LanTargetStatus: Equatable, Sendable {
    public var state: LanTargetState
    public var lastSuccess: Date?
    /// `unreachable` and `httpStatus`: when the next request may go.
    public var retryAt: Date?
    public var httpStatus: Int?

    public init(state: LanTargetState, lastSuccess: Date? = nil, retryAt: Date? = nil, httpStatus: Int? = nil) {
        self.state = state
        self.lastSuccess = lastSuccess
        self.retryAt = retryAt
        self.httpStatus = httpStatus
    }
}

/// The resolved addresses of a host: text for the local check, socket addresses to send to.
struct LanResolved: Sendable {
    var texts: [String]
    var addresses: [sockaddr_storage]

    /// The system resolver (blocking: call off the main thread). A literal resolves to itself.
    static func lookup(_ host: String, port: Int, datagram: Bool) -> LanResolved {
        var hints = addrinfo()
        hints.ai_family = AF_UNSPEC
        hints.ai_socktype = datagram ? SOCK_DGRAM : SOCK_STREAM
        var result: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, String(port), &hints, &result) == 0, let first = result else {
            return LanResolved(texts: [], addresses: [])
        }
        defer { freeaddrinfo(first) }
        var resolved = LanResolved(texts: [], addresses: [])
        var cursor: UnsafeMutablePointer<addrinfo>? = first
        while let info = cursor {
            if let address = info.pointee.ai_addr,
               info.pointee.ai_family == AF_INET || info.pointee.ai_family == AF_INET6 {
                var storage = sockaddr_storage()
                withUnsafeMutableBytes(of: &storage) { target in
                    target.copyMemory(from: UnsafeRawBufferPointer(start: address, count: Int(info.pointee.ai_addrlen)))
                }
                if let text = LanAddress.text(of: storage), !resolved.texts.contains(text) {
                    resolved.texts.append(text)
                    resolved.addresses.append(storage)
                }
            }
            cursor = info.pointee.ai_next
        }
        return resolved
    }
}

/// LAN 5: sends each frame to the enabled targets by their push rules. Every member lives on `queue`.
public final class LanTargets: @unchecked Sendable {
    /// Status changes by target id, on `queue`.
    public var onStatus: (@Sendable (String, LanTargetStatus) -> Void)?

    let queue = DispatchQueue(label: "com.agentspeed.lan.targets")
    private var senders: [String: LanTargetSender] = [:]

    public init() {}

    /// The enabled targets to send to; others stop. A target whose kind, address or key changed starts over.
    public func configure(_ targets: [LanDocument.Target]) {
        queue.async {
            var next: [String: LanTargetSender] = [:]
            for target in targets where target.enabled {
                if let existing = self.senders[target.id], existing.target == target {
                    next[target.id] = existing
                } else if let sender = LanTargetSender(target: target, owner: self) {
                    next[target.id] = sender
                    self.onStatus?(target.id, LanTargetStatus(state: .waiting))
                }
            }
            for (id, sender) in self.senders where next[id] !== sender { sender.stop() }
            self.senders = next
        }
    }

    public func publish(frame: ReportJSON, changed: Bool) {
        queue.async {
            let now = LanClock.nowMs()
            for sender in self.senders.values { sender.tick(frame: frame, changed: changed, nowMs: now) }
        }
    }

    /// Sends one frame to `target` now, apart from the configured senders (the sheet's Send Test).
    public func test(_ target: LanDocument.Target, frame: ReportJSON,
                     completion: @escaping @Sendable (LanTargetStatus) -> Void) {
        queue.async {
            guard let sender = LanTargetSender(target: target, owner: nil) else {
                return completion(LanTargetStatus(state: .failed))
            }
            sender.sendOnce(frame: frame) { status in
                sender.stop()
                completion(status)
            }
        }
    }

    func report(_ id: String, _ status: LanTargetStatus) {
        onStatus?(id, status)
    }
}

/// One target: resolution, the local check, the push receiver and the transport (LAN 5.1–5.3).
final class LanTargetSender: @unchecked Sendable {
    static let resolveMs: Int64 = 30_000
    static let httpTimeoutMs = 3000
    static let statusLineLimit = 1024

    let target: LanDocument.Target
    private let address: LanTargetAddress
    private weak var owner: LanTargets?
    private let queue: DispatchQueue
    private var receiver: LanPushReceiver
    private var busy = false
    private var stopped = false
    private var status = LanTargetStatus(state: .waiting)
    // UDP
    private var resolved: LanResolved?
    private var resolvedAt: Int64 = 0
    private var sockets: [Int32: Int32] = [:]
    // HTTP
    private var failures = 0
    private var waitUntil: Int64 = 0
    private var connection: NWConnection?

    init?(target: LanDocument.Target, owner: LanTargets?) {
        guard let address = target.parsed else { return nil }
        self.target = target
        self.address = address
        self.owner = owner
        queue = owner?.queue ?? DispatchQueue(label: "com.agentspeed.lan.test")
        receiver = LanPushReceiver(target.kind == .udp ? .udp : .http)
    }

    deinit {
        for socket in sockets.values { Darwin.close(socket) }
    }

    func stop() {
        stopped = true
        connection?.cancel()
        connection = nil
    }

    func tick(frame: ReportJSON, changed: Bool, nowMs: Int64) {
        let waiting = target.kind == .http && nowMs < waitUntil
        guard !stopped, receiver.tick(nowMs: nowMs, changed: changed, busy: busy || waiting) else { return }
        send(frame) { _ in }
    }

    func sendOnce(frame: ReportJSON, completion: @escaping (LanTargetStatus) -> Void) {
        send(frame, completion: completion)
    }

    private func send(_ frame: ReportJSON, completion: @escaping (LanTargetStatus) -> Void) {
        busy = true
        switch target.kind {
        case .udp: sendDatagram(frame, completion: completion)
        case .http: post(frame, completion: completion)
        }
    }

    private func finish(_ state: LanTargetState, httpStatus: Int? = nil, completion: (LanTargetStatus) -> Void) {
        busy = false
        var next = status
        next.state = state
        next.httpStatus = httpStatus
        if state == .ok {
            next.lastSuccess = Date()
            next.retryAt = nil
            failures = 0
            waitUntil = 0
        } else if target.kind == .http, state == .unreachable || state == .httpStatus {
            failures += 1
            let wait = LanBackoff.ms(failures: failures)
            waitUntil = LanClock.nowMs() + wait
            next.retryAt = Date().addingTimeInterval(Double(wait) / 1000)
        } else {
            next.retryAt = nil
        }
        completion(next)
        guard !stopped, next != status else { return }
        status = next
        owner?.report(target.id, next)
    }

    /// Resolves off the queue, then continues on it with the local addresses (or a status).
    private func resolve(datagram: Bool, then body: @escaping (Result<LanResolved, LanTargetState>) -> Void) {
        let host = address.host
        let port = address.port
        let udp = target.kind == .udp
        DispatchQueue.global(qos: .utility).async { [queue] in
            let resolved = LanResolved.lookup(host, port: port, datagram: datagram)
            queue.async {
                switch LanResolution.check(resolved.texts, udp: udp) {
                case .ok: body(.success(resolved))
                case .notFound: body(.failure(.notFound))
                case .notLocal: body(.failure(.notLocal))
                }
            }
        }
    }

    // MARK: UDP

    private func sendDatagram(_ frame: ReportJSON, completion: @escaping (LanTargetStatus) -> Void) {
        let now = LanClock.nowMs()
        if let resolved, now - resolvedAt < Self.resolveMs {
            transmit(frame, to: resolved, completion: completion)
            return
        }
        resolve(datagram: true) { [weak self] result in
            guard let self else { return }
            switch result {
            case .success(let resolved):
                self.resolved = resolved
                self.resolvedAt = LanClock.nowMs()
                self.transmit(frame, to: resolved, completion: completion)
            case .failure(let state):
                self.resolved = nil
                self.finish(state, completion: completion)
            }
        }
    }

    private func transmit(_ frame: ReportJSON, to resolved: LanResolved, completion: (LanTargetStatus) -> Void) {
        guard var storage = resolved.addresses.first else { return finish(.notFound, completion: completion) }
        let family = Int32(storage.ss_family)
        let socket = sockets[family] ?? makeSocket(family)
        guard socket >= 0 else { return finish(.failed, completion: completion) }
        let data = LanDatagram.make(frame: frame, key: target.key).data
        let length = socklen_t(family == AF_INET ? MemoryLayout<sockaddr_in>.size : MemoryLayout<sockaddr_in6>.size)
        let sent = data.withUnsafeBytes { bytes in
            withUnsafePointer(to: &storage) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    sendto(socket, bytes.baseAddress, bytes.count, 0, $0, length)
                }
            }
        }
        if sent < 0 {
            // Look the name up again after an error (LAN 5.1).
            self.resolved = nil
            finish(.failed, completion: completion)
        } else {
            finish(.ok, completion: completion)
        }
    }

    private func makeSocket(_ family: Int32) -> Int32 {
        let socket = Darwin.socket(family, SOCK_DGRAM, IPPROTO_UDP)
        guard socket >= 0 else { return -1 }
        var on: Int32 = 1
        setsockopt(socket, SOL_SOCKET, SO_BROADCAST, &on, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(socket, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
        sockets[family] = socket
        return socket
    }

    // MARK: HTTP

    private func post(_ frame: ReportJSON, completion: @escaping (LanTargetStatus) -> Void) {
        resolve(datagram: false) { [weak self] result in
            guard let self else { return }
            switch result {
            case .failure(let state):
                self.finish(state, completion: completion)
            case .success(let resolved):
                guard let storage = resolved.addresses.first, let host = Self.host(storage),
                      let port = NWEndpoint.Port(rawValue: UInt16(self.address.port)) else {
                    return self.finish(.notFound, completion: completion)
                }
                self.connect(host: host, port: port, body: frame.compactData, completion: completion)
            }
        }
    }

    private func connect(host: NWEndpoint.Host, port: NWEndpoint.Port, body: Data,
                         completion: @escaping (LanTargetStatus) -> Void) {
        let exchange = LanHTTPExchange(host: host, port: port, request: request(body), queue: queue)
        connection = exchange.connection
        exchange.run { [weak self] state, code in
            guard let self else { return }
            if self.connection === exchange.connection { self.connection = nil }
            self.finish(state, httpStatus: code, completion: completion)
        }
    }

    /// The request of LAN 5.3.
    func request(_ body: Data) -> Data {
        var lines = ["POST \(address.target ?? "/") HTTP/1.1", "Host: \(address.hostHeader ?? address.host)",
                     "User-Agent: AgentSpeed/\(Self.version) (macOS)", "Content-Type: application/json",
                     "Content-Length: \(body.count)"]
        if let key = target.key { lines.append("X-AgentSpeed-Signature: \(LanSignature.line(key: key, body: body))") }
        lines.append("Connection: close")
        return Data((lines.joined(separator: "\r\n") + "\r\n\r\n").utf8) + body
    }

    /// The status code once a full status line arrived (nil inside when it is not one), or nil while waiting.
    static func statusLine(_ data: Data) -> Int?? {
        guard let end = data.firstIndex(of: 0x0A) else { return nil }
        let line = String(decoding: data[data.startIndex..<end], as: UTF8.self)
        let parts = line.split(separator: " ", maxSplits: 2)
        guard parts.count >= 2, parts[0].hasPrefix("HTTP/1."), parts[1].count == 3, let code = Int(parts[1]) else {
            return .some(nil)
        }
        return .some(code)
    }

    /// The checked address itself as a host, with its scope for a link-local IPv6 address.
    static func host(_ storage: sockaddr_storage) -> NWEndpoint.Host? {
        guard var text = LanAddress.text(of: storage) else { return nil }
        if Int32(storage.ss_family) == AF_INET6 {
            var copy = storage
            let scope = withUnsafePointer(to: &copy) {
                $0.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) { $0.pointee.sin6_scope_id }
            }
            var name = [CChar](repeating: 0, count: Int(IF_NAMESIZE))
            if scope != 0, if_indextoname(scope, &name) != nil { text += "%" + String(cString: name) }
        }
        return NWEndpoint.Host(text)
    }

    static let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
}

/// One HTTP request to a target over its own TCP connection (LAN 5.3): write the request, read the status line, close.
/// Every member is touched on `queue` only.
final class LanHTTPExchange: @unchecked Sendable {
    let connection: NWConnection
    private let request: Data
    private let queue: DispatchQueue
    private var received = Data()
    private var done = false
    private var timeout: DispatchWorkItem?
    private var completion: ((LanTargetState, Int?) -> Void)?

    init(host: NWEndpoint.Host, port: NWEndpoint.Port, request: Data, queue: DispatchQueue) {
        let parameters = NWParameters.tcp
        parameters.preferNoProxies = true
        connection = NWConnection(host: host, port: port, using: parameters)
        self.request = request
        self.queue = queue
    }

    func run(_ completion: @escaping (LanTargetState, Int?) -> Void) {
        self.completion = completion
        let timeout = DispatchWorkItem { [weak self] in self?.end(.unreachable, nil) }
        self.timeout = timeout
        queue.asyncAfter(deadline: .now() + .milliseconds(LanTargetSender.httpTimeoutMs), execute: timeout)
        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                self.connection.send(content: self.request, completion: .contentProcessed { [weak self] error in
                    if error != nil { self?.end(.unreachable, nil) }
                })
                self.readStatus()
            case .failed:
                self.end(.unreachable, nil)
            default:
                break
            }
        }
        connection.start(queue: queue)
    }

    private func readStatus() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: LanTargetSender.statusLineLimit) {
            [weak self] data, _, isComplete, error in
            guard let self, !self.done else { return }
            if let data { self.received.append(data) }
            if let line = LanTargetSender.statusLine(self.received) {
                if let code = line, (200...299).contains(code) {
                    self.end(.ok, nil)
                } else {
                    self.end(line == nil ? .unreachable : .httpStatus, line ?? nil)
                }
            } else if isComplete || error != nil || self.received.count >= LanTargetSender.statusLineLimit {
                self.end(.unreachable, nil)
            } else {
                self.readStatus()
            }
        }
    }

    private func end(_ state: LanTargetState, _ code: Int?) {
        guard !done else { return }
        done = true
        timeout?.cancel()
        connection.cancel()
        completion?(state, code)
        completion = nil
    }
}
