import Foundation
@testable import AgentSpeedCore

/// A wall clock the test advances; due work runs on its queue, one item at a time, in deadline order.
final class ManualScheduling: RemoteScheduling, @unchecked Sendable {
    final class Item: RemoteScheduledWork, @unchecked Sendable {
        let deadline: Date
        let queue: DispatchQueue
        let work: @Sendable () -> Void
        private let lock = NSLock()
        private var cancelled = false

        init(deadline: Date, queue: DispatchQueue, work: @escaping @Sendable () -> Void) {
            self.deadline = deadline
            self.queue = queue
            self.work = work
        }

        func cancel() { lock.withLock { cancelled = true } }
        var isCancelled: Bool { lock.withLock { cancelled } }
    }

    private let lock = NSLock()
    private var current: Date
    private var items: [Item] = []

    init(start: Date = Date(timeIntervalSince1970: 1_780_000_000)) {
        current = start
    }

    var now: Date { lock.withLock { current } }

    func schedule(at deadline: Date, on queue: DispatchQueue, _ work: @escaping @Sendable () -> Void) -> any RemoteScheduledWork {
        let item = Item(deadline: deadline, queue: queue, work: work)
        lock.withLock { items.append(item) }
        return item
    }

    /// Pending, uncancelled deadlines.
    var deadlines: [Date] {
        lock.withLock { items.filter { !$0.isCancelled }.map(\.deadline).sorted() }
    }

    func advance(by seconds: TimeInterval) {
        let target = now.addingTimeInterval(seconds)
        while true {
            let next: Item? = lock.withLock {
                items.removeAll { $0.isCancelled }
                guard let index = items.indices.filter({ items[$0].deadline <= target })
                    .min(by: { items[$0].deadline < items[$1].deadline }) else {
                    current = max(current, target)
                    return nil
                }
                let item = items.remove(at: index)
                current = max(current, item.deadline)
                return item
            }
            guard let next else { return }
            next.queue.sync { if !next.isCancelled { next.work() } }
        }
    }
}

/// A process of the scripted transport. The test (or the transport's script) writes its output and ends it.
final class FakeProcess: RemoteProcess, @unchecked Sendable {
    let request: RemoteProcessRequest
    let handlers: RemoteProcessHandlers
    private let lock = NSLock()
    private var input = Data()
    private var inputClosed = false
    private var terminated = false
    private var killed = false
    private var ended = false
    /// Runs when stdin is closed (an upload script answers then).
    var onInputClosed: (@Sendable (FakeProcess) -> Void)?
    /// ssh ends on SIGTERM with this status.
    var exitOnTerminate: Int32? = 143

    init(request: RemoteProcessRequest, handlers: RemoteProcessHandlers) {
        self.request = request
        self.handlers = handlers
    }

    var remoteCommand: String { request.arguments.last ?? "" }
    var isProbe: Bool { remoteCommand.contains(RemoteScripts.probe) }
    var isUpload: Bool { remoteCommand.contains(RemoteScripts.upload) }
    var isRun: Bool { remoteCommand.contains(RemoteScripts.run) }

    /// Runs for every write that was accepted.
    var onWrite: (@Sendable (FakeProcess, Data) -> Void)?

    func write(_ data: Data, completion: (@Sendable (Bool) -> Void)?) {
        let accepted = lock.withLock { () -> Bool in
            guard !inputClosed else { return false }
            input.append(data)
            return true
        }
        if accepted { onWrite?(self, data) }
        completion?(accepted)
    }

    func closeInput() {
        let first = lock.withLock { () -> Bool in
            defer { inputClosed = true }
            return !inputClosed
        }
        if first { onInputClosed?(self) }
    }

    func terminate() {
        lock.withLock { terminated = true }
        if let status = exitOnTerminate { finish(status) }
    }

    func kill() {
        lock.withLock { killed = true }
        finish(137)
    }

    var writtenData: Data { lock.withLock { input } }
    var isInputClosed: Bool { lock.withLock { inputClosed } }
    var wasTerminated: Bool { lock.withLock { terminated } }
    var wasKilled: Bool { lock.withLock { killed } }

    /// The JSON lines written to stdin.
    var writtenLines: [JSONValue] {
        String(decoding: writtenData, as: UTF8.self).split(separator: "\n").compactMap { try? JSONParser.parse(String($0)) }
    }

    var writtenTypes: [String] { writtenLines.compactMap { $0["t"]?.string } }

    func send(_ line: String) {
        handlers.stdout(Data((line + "\n").utf8))
    }

    func sendStdout(_ text: String) {
        handlers.stdout(Data(text.utf8))
    }

    func sendStderr(_ text: String) {
        handlers.stderr(Data(text.utf8))
    }

    /// Ends the process once.
    func finish(_ status: Int32) {
        let first = lock.withLock { () -> Bool in
            defer { ended = true }
            return !ended
        }
        if first { handlers.exit(status) }
    }
}

/// A transport whose processes are scripted by `script`, called right after each launch.
final class FakeTransport: RemoteTransport, @unchecked Sendable {
    private let lock = NSLock()
    private var processes: [FakeProcess] = []
    private var sshPresent = true
    var script: (@Sendable (FakeProcess) -> Void)?

    func isAvailable(_ executable: String) -> Bool { lock.withLock { sshPresent } }

    func setAvailable(_ available: Bool) { lock.withLock { sshPresent = available } }

    func launch(_ request: RemoteProcessRequest, handlers: RemoteProcessHandlers) throws -> any RemoteProcess {
        let process = FakeProcess(request: request, handlers: handlers)
        lock.withLock { processes.append(process) }
        script?(process)
        return process
    }

    var launched: [FakeProcess] { lock.withLock { processes } }
    var runs: [FakeProcess] { launched.filter(\.isRun) }
}

/// Collects a hub's or connection's events.
final class RemoteEventLog: @unchecked Sendable {
    let queue = DispatchQueue(label: "test.remote.events")
    private let lock = NSLock()
    private var recorded: [RemoteHostEvent] = []

    var handler: @Sendable (RemoteHostEvent) -> Void {
        { [self] event in lock.withLock { recorded.append(event) } }
    }

    var events: [RemoteHostEvent] {
        queue.sync {}
        return lock.withLock { recorded }
    }

    func statuses(_ hostId: String? = nil) -> [RemoteStatus] {
        events.compactMap { event in
            guard hostId == nil || event.hostId == hostId, case .status(let status) = event.kind else { return nil }
            return status.status
        }
    }

    var lastStatus: RemoteHostStatus? {
        events.reversed().lazy.compactMap { event -> RemoteHostStatus? in
            if case .status(let status) = event.kind { return status }
            return nil
        }.first
    }

    var messages: [(RelayMessage, Bool)] {
        events.compactMap { event in
            if case .message(let message, let seeded) = event.kind { return (message, seeded) }
            return nil
        }
    }

    var historyDueCount: Int { historyDueCount(nil) }

    func historyDueCount(_ hostId: String?) -> Int {
        events.filter { event in
            guard hostId == nil || event.hostId == hostId, case .historyDue = event.kind else { return false }
            return true
        }.count
    }

    func clear() {
        queue.sync {}
        lock.withLock { recorded.removeAll() }
    }
}

/// A relay bundle with fake executables in a temp folder (`@unchecked Sendable`: read-only after init).
struct FakeRelayBundle: @unchecked Sendable {
    let directory: TempDirectory
    let bundle: RemoteRelayBundle
    let x86: RemoteRelayExecutable

    init(version: String = "1.7.0", bytes: Int = 600_000) throws {
        directory = try TempDirectory()
        for arch in RemoteArch.allCases {
            try pseudoRandomData(count: bytes, seed: arch == .x86_64 ? 7 : 8)
                .write(to: directory.file(RemoteRelayName.bundled(arch)))
        }
        bundle = RemoteRelayBundle(directory: directory.url, version: version)
        x86 = bundle.executable(for: .x86_64)!
    }

    /// Probe output of a Linux x86_64 host; `installed` lists the relay file as present.
    func probeOutput(installed: Bool, machine: String = "x86_64") -> String {
        var text = "system Linux \(machine)\ntool sha256sum\n"
        if installed { text += "hash \(x86.sha256) \(x86.installedName)\n" }
        return text + "end\n"
    }
}

/// Relay lines for tests.
enum RelayLines {
    static let baseMs: Int64 = 1_780_000_000_000

    static func json(_ object: [String: Any]) -> String {
        let data = try! JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes])
        return String(decoding: data, as: UTF8.self)
    }

    static func hello(now: Int64 = baseMs, protocolVersion: Int = 1) -> String {
        json(["t": "hello", "protocol": protocolVersion, "version": "1.7.0", "arch": "x86_64", "now": now])
    }

    static let ready = #"{"t":"ready"}"#

    static func fact(kind: String = "serviceTier", value: String = "fast", raw: String = "priority",
                     evidence: String = "recorded", provider: Any = "openai", scope: String = "request") -> [String: Any] {
        ["kind": kind, "value": value, "rawValue": raw, "evidence": evidence, "provider": provider, "scope": scope]
    }

    static func action(callMs: Int64 = baseMs, resultMs: Any = baseMs + 400) -> [String: Any] {
        ["tool": "shell", "digest": "0123456789abcdef", "callMs": callMs, "resultMs": resultMs, "failed": false,
         "model": "gpt-5.5", "seeded": true]
    }

    static func agentObject(id: String = "/home/u/.codex/sessions/a.jsonl") -> [String: Any] {
        ["id": id, "source": "codex", "project": "proj", "cwd": "/home/u/proj", "model": "gpt-5.5", "subagent": false,
         "phase": "generating", "inFlightStartMs": baseMs, "lastLineMs": baseMs + 500, "modes": [fact()],
         "sessionModes": [] as [Any], "configuredModes": [] as [Any], "actions": [action()]]
    }

    static func agent(_ object: [String: Any] = agentObject()) -> String {
        json(["t": "agent", "a": object])
    }

    static func responseObject(key: String = "resp_1") -> [String: Any] {
        ["key": key, "source": "codex", "agent": "/home/u/.codex/sessions/a.jsonl", "model": "gpt-5.5",
         "startMs": baseMs, "endMs": baseMs + 2000, "firstMs": baseMs + 300, "input": 1200, "cachedInput": 1000,
         "cacheWriteInput": 0, "cacheWrite1hInput": 0, "output": 340, "reasoning": 120, "requestCount": 1,
         "modes": [] as [Any], "final": true, "provider": "openai", "fast": false]
    }

    static func response(_ object: [String: Any] = responseObject(), completion: Bool = true, project: String = "proj") -> String {
        json(["t": "response", "r": object, "completion": completion, "project": project])
    }

    static func clock(id: Int64, now: Int64) -> String {
        json(["t": "clock", "id": id, "now": now])
    }
}
