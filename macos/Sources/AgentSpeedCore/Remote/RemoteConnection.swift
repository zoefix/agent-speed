import Foundation
import os

/// One host's connection (REMOTE 4.2–4.4): probe → (upload) → run over `/usr/bin/ssh`, then the protocol of
/// section 5 on the relay's stdin and stdout, with pings, the silence rule, back-off and the history schedule.
/// Events go to `onEvent` on `eventQueue`, in order.
///
/// `@unchecked Sendable`: every mutable property is only touched on `queue`.
public final class RemoteConnection: @unchecked Sendable {
    static let logger = Logger(subsystem: "io.github.zoefix.agentspeed", category: "remote")

    static let silenceLimit: TimeInterval = 150
    static let probeLimit: TimeInterval = 150
    static let pingInterval: TimeInterval = 30
    static let historyInterval: TimeInterval = 600
    static let killDelay: TimeInterval = 2
    static let uploadChunkBytes = 256 * 1024
    static let pendingPingLimit = 8

    enum Stage: Equatable {
        case idle, probe, upload, run
    }

    private static let attempts = OSAllocatedUnfairLock(initialState: 0)

    /// Attempt numbers are unique across connections, so a host's replaced connection never repeats one.
    static func nextAttempt() -> Int {
        attempts.withLock { count in
            count += 1
            return count
        }
    }

    public let host: RemoteHost
    let environment: RemoteEnvironment
    let queue: DispatchQueue
    private let eventQueue: DispatchQueue
    private let onEvent: @Sendable (RemoteHostEvent) -> Void

    var settings: RemoteSessionSettings
    var stage = Stage.idle
    var stopped = false
    /// The current attempt (`RemoteHostEvent.connection`).
    var attempt = 0
    /// Counts launched processes, so callbacks of an old process are ignored.
    var processToken = 0
    var process: (any RemoteProcess)?
    /// Why this side ended the running process (silence, protocol error, version); nil when ssh ended by itself.
    var endReason: RemoteStatus?
    var backoff = RemoteBackoff()
    var lastStatus: RemoteHostStatus?
    var lastErrorAt: Date?
    /// Waiting for the relay to restore the host's status line before stopping.
    var retiring = false

    var stdoutBuffer = Data()
    var stderrBuffer = Data()
    var relay: RemoteRelayExecutable?
    var uploadHandle: FileHandle?
    var uploadedBytes: Int64 = 0
    var reportedUploadFraction = 0.0

    var session = RemoteSessionState()
    var timers: [TimerSlot: any RemoteScheduledWork] = [:]

    enum TimerSlot: Hashable {
        case retry, stageLimit, silence, ping, history, midnight, retire
    }

    public init(
        host: RemoteHost,
        settings: RemoteSessionSettings,
        environment: RemoteEnvironment,
        eventQueue: DispatchQueue,
        onEvent: @escaping @Sendable (RemoteHostEvent) -> Void
    ) {
        self.host = host
        self.settings = settings
        self.environment = environment
        self.eventQueue = eventQueue
        self.onEvent = onEvent
        queue = DispatchQueue(label: "app.agentspeed.remote.connection", qos: .utility)
    }

    // MARK: - Commands (any thread)

    public func start() {
        queue.async { [self] in
            guard !stopped, stage == .idle, timers[.retry] == nil else { return }
            begin()
        }
    }

    /// Closes the relay's stdin, terminates `ssh` (SIGKILL after 2 s) and reports `finalStatus`, if any, as the last
    /// event. Synchronous: SIGTERM has been sent when it returns. The connection cannot be started again.
    public func stop(finalStatus: RemoteStatus? = nil) {
        queue.sync { [self] in
            guard !stopped else { return }
            stopped = true
            cancelTimers()
            closeUpload()
            endProcess()
            if let finalStatus { report(finalStatus) }
        }
    }

    /// *Retry now*: an attempt at once unless one is under way; the back-off starts over.
    public func retryNow() {
        queue.async { [self] in
            guard !stopped, stage == .idle else { return }
            backoff.reset()
            begin()
        }
    }

    /// The Mac woke or its network came back: a host waiting for its back-off retries at once; a running relay is
    /// pinged and its silence clock restarts (the link may need a moment).
    public func noteWake() {
        queue.async { [self] in
            guard !stopped else { return }
            if stage == .idle, lastStatus?.status.isRetrying == true, timers[.retry] != nil {
                begin()
            } else if stage == .run {
                session.lastLineAt = now
                if session.helloReceived { sendPing() }
            }
        }
    }

    public func updatePrices(_ prices: RemotePriceTableJSON) {
        queue.async { [self] in
            guard settings.prices != prices else { return }
            settings.prices = prices
            if stage == .run { send(RelayLineEncoder.prices(prices)) }
        }
    }

    /// The quota config changed (a switch, the host's bridge): the running relay gets a `quota` line, no reconnect.
    public func updateQuota(_ config: RemoteQuotaConfig) {
        queue.async { [self] in
            guard settings.quota != config else { return }
            settings.quota = config
            if stage == .run { send(RelayLineEncoder.quota(config)) }
        }
    }

    /// The host left the settings with its bridge on (REMOTE 2.4, 11.3): the relay is asked to restore the status line
    /// first, and the connection stops once it reports the bridge off, or after `timeout`. A relay that is not running
    /// stops at once.
    public func retire(timeout: TimeInterval = RemoteConnection.retireLimit) {
        queue.async { [self] in
            guard !stopped else { return }
            guard stage == .run, session.helloReceived else {
                stopNow()
                return
            }
            retiring = true
            var config = settings.quota ?? .off
            config.claudeBridge = false
            config.sources.remove(.claudeCode)
            settings.quota = config
            send(RelayLineEncoder.quota(config))
            schedule(.retire, after: timeout) { $0.stopNow() }
        }
    }

    /// The bridge once off and on again, for *Reconnect*.
    public func reapplyQuota() {
        queue.async { [self] in
            guard stage == .run, var config = settings.quota, config.claudeBridge else { return }
            config.claudeBridge = false
            config.sources.remove(.claudeCode)
            send(RelayLineEncoder.quota(config))
            if let current = settings.quota { send(RelayLineEncoder.quota(current)) }
        }
    }

    public static let retireLimit: TimeInterval = 10

    func stopNow() {
        guard !stopped else { return }
        stopped = true
        cancelTimers()
        closeUpload()
        endProcess()
    }

    public func setWatch(_ on: Bool) {
        queue.async { [self] in
            guard settings.watch != on else { return }
            settings.watch = on
            if stage == .run { send(RelayLineEncoder.watch(on)) }
        }
    }

    // MARK: - Attempts

    var now: Date { environment.scheduling.now }

    func begin() {
        cancelTimers()
        attempt = Self.nextAttempt()
        stage = .idle
        endReason = nil
        relay = nil
        session = RemoteSessionState()
        guard host.isConnectable else {
            report(.invalidDestination)
            return
        }
        guard environment.transport.isAvailable(RemoteSSH.executable) else {
            fail(.sshMissing)
            return
        }
        report(.connecting)
        guard let command = RemoteCommand.probe(folder: host.effectiveRelayFolder) else {
            fail(.invalidDestination)
            return
        }
        launch(.probe, command)
    }

    func launch(_ next: Stage, _ command: String) {
        let configFile = RemoteSSH.developmentConfigFile(environment.processEnvironment)
        guard let arguments = RemoteSSH.arguments(destination: host.destination, remoteCommand: command,
                                                  configFile: configFile) else {
            fail(.invalidDestination)
            return
        }
        processToken += 1
        let token = processToken
        stdoutBuffer = Data()
        stderrBuffer = Data()
        let handlers = RemoteProcessHandlers(
            stdout: { [weak self] data in self?.onQueue { $0.received(stdout: data, token: token) } },
            stderr: { [weak self] data in self?.onQueue { $0.received(stderr: data, token: token) } },
            exit: { [weak self] status in self?.onQueue { $0.exited(status: status, token: token) } }
        )
        let request = RemoteProcessRequest(executable: RemoteSSH.executable, arguments: arguments,
                                           environment: RemoteSSH.environment(environment.processEnvironment))
        do {
            process = try environment.transport.launch(request, handlers: handlers)
        } catch {
            process = nil
            fail(environment.transport.isAvailable(RemoteSSH.executable) ? .exited(status: 127) : .sshMissing)
            return
        }
        stage = next
        switch next {
        case .probe:
            schedule(.stageLimit, after: Self.probeLimit) { $0.endProcess(reason: .unreachable) }
        case .upload:
            streamUpload()
        case .run:
            beginSession()
        case .idle:
            break
        }
    }

    private func received(stdout data: Data, token: Int) {
        guard token == processToken, !stopped else { return }
        switch stage {
        case .probe, .upload:
            if stdoutBuffer.count < RemoteProbeResult.maxOutputBytes {
                stdoutBuffer.append(data.prefix(RemoteProbeResult.maxOutputBytes - stdoutBuffer.count))
            }
        case .run:
            receivedRelayOutput(data)
        case .idle:
            break
        }
    }

    private func received(stderr data: Data, token: Int) {
        guard token == processToken, !stopped else { return }
        stderrBuffer.append(data)
        if stderrBuffer.count > RemoteStatusClassifier.maxStderrBytes {
            stderrBuffer = Data(stderrBuffer.suffix(RemoteStatusClassifier.maxStderrBytes))
        }
    }

    private func exited(status: Int32, token: Int) {
        guard token == processToken else { return }
        process = nil
        cancel(.stageLimit)
        closeUpload()
        guard !stopped else { return }
        let ended = stage
        stage = .idle
        if let endReason {
            fail(endReason)
            return
        }
        switch ended {
        case .probe:
            probeFinished(status: status)
        case .upload:
            uploadFinished(status: status)
        case .run:
            fail(RemoteStatusClassifier.classify(exitStatus: status, stderr: stderrBuffer, stage: .run))
        case .idle:
            break
        }
    }

    private func probeFinished(status: Int32) {
        guard status == 0 else {
            fail(RemoteStatusClassifier.classify(exitStatus: status, stderr: stderrBuffer, stage: .probe))
            return
        }
        let result = RemoteProbeResult.parse(stdoutBuffer)
        switch result.verdict {
        case .malformed:
            fail(.exited(status: status))
        case .unsupportedSystem(let system):
            fail(.unsupportedSystem(system: system))
        case .cannotVerify:
            fail(.cannotVerify)
        case .supported(let arch):
            guard let executable = environment.relays.executable(for: arch) else {
                Self.logger.error("No bundled relay for \(arch.rawValue, privacy: .public)")
                fail(.uploadFailed)
                return
            }
            relay = executable
            if result.hasInstalled(name: executable.installedName, sha256: executable.sha256) {
                startRelay()
            } else if let command = RemoteCommand.upload(folder: host.effectiveRelayFolder,
                                                         fileName: executable.installedName, sha256: executable.sha256) {
                report(.installing(fraction: 0))
                launch(.upload, command)
            } else {
                fail(.uploadFailed)
            }
        }
    }

    private func uploadFinished(status: Int32) {
        guard status == 0 else {
            fail(RemoteStatusClassifier.classify(exitStatus: status, stderr: stderrBuffer, stage: .upload))
            return
        }
        guard let relay, RemoteProbeResult.uploadedHash(stdoutBuffer) == relay.sha256 else {
            fail(.uploadFailed)
            return
        }
        startRelay()
    }

    private func startRelay() {
        guard let relay, let command = RemoteCommand.run(folder: host.effectiveRelayFolder, fileName: relay.installedName) else {
            fail(.uploadFailed)
            return
        }
        launch(.run, command)
    }

    /// Streams the bundled relay into the upload command's stdin, one chunk after the other, then closes it.
    private func streamUpload() {
        guard let relay, let handle = try? FileHandle(forReadingFrom: relay.url) else {
            endProcess(reason: .uploadFailed)
            return
        }
        uploadHandle = handle
        uploadedBytes = 0
        reportedUploadFraction = 0
        uploadNextChunk(token: processToken)
    }

    private func uploadNextChunk(token: Int) {
        guard token == processToken, stage == .upload, let process, let handle = uploadHandle else {
            closeUpload()
            return
        }
        guard let chunk = try? handle.read(upToCount: Self.uploadChunkBytes), !chunk.isEmpty else {
            closeUpload()
            process.closeInput()
            return
        }
        process.write(chunk) { [weak self] written in
            self?.onQueue { connection in
                guard token == connection.processToken else { return }
                guard written else {
                    connection.closeUpload()
                    return
                }
                connection.uploadedBytes += Int64(chunk.count)
                connection.reportUploadProgress(total: connection.relay?.size ?? 0)
                connection.uploadNextChunk(token: token)
            }
        }
    }

    func closeUpload() {
        try? uploadHandle?.close()
        uploadHandle = nil
    }

    private func reportUploadProgress(total: Int64) {
        let fraction = total > 0 ? min(1, Double(uploadedBytes) / Double(total)) : 1
        guard fraction - reportedUploadFraction >= 0.05 || (fraction == 1 && reportedUploadFraction < 1) else { return }
        reportedUploadFraction = fraction
        report(.installing(fraction: fraction))
    }

    // MARK: - Ending

    /// Ends the running process: stdin closed, SIGTERM, SIGKILL after 2 s. Its exit then reports `reason`.
    func endProcess(reason: RemoteStatus? = nil) {
        if let reason, endReason == nil { endReason = reason }
        guard let process else { return }
        process.closeInput()
        process.terminate()
        let deadline = now.addingTimeInterval(Self.killDelay)
        _ = environment.scheduling.schedule(at: deadline, on: queue) { process.kill() }
        if stopped { self.process = nil }
    }

    /// A failed attempt: the status with the time of the next one.
    func fail(_ status: RemoteStatus) {
        cancelTimers()
        stage = .idle
        let failedAt = now
        lastErrorAt = failedAt
        guard status != .invalidDestination else {
            report(status)
            return
        }
        let delay = backoff.delay(after: status, upSince: session.helloAt, now: failedAt)
        let retryAt = failedAt.addingTimeInterval(delay)
        Self.logger.notice("Remote host ended: \(status.code, privacy: .public), retry in \(Int(delay), privacy: .public) s")
        report(status, retryAt: retryAt)
        schedule(.retry, at: retryAt) { $0.begin() }
    }

    // MARK: - Plumbing

    /// Reports a status once; a live status carries the relay's `hello` facts and the clock offset.
    func report(_ status: RemoteStatus, retryAt: Date? = nil) {
        var value = RemoteHostStatus(status: status, retryAt: retryAt, lastErrorAt: lastErrorAt)
        if session.helloReceived, status == .connected || status.isSeeding {
            value.clockOffsetMs = session.clock.offsetMs
            value.relayVersion = session.relayVersion
            value.relayArch = session.relayArch
        }
        guard value != lastStatus else { return }
        lastStatus = value
        emit(.status(value))
    }

    func emit(_ kind: RemoteHostEvent.Kind) {
        let event = RemoteHostEvent(hostId: host.id, connection: attempt, kind: kind)
        let onEvent = onEvent
        eventQueue.async { onEvent(event) }
    }

    func send(_ line: Data?) {
        guard let line, let process else { return }
        process.write(line, completion: nil)
    }

    func schedule(_ slot: TimerSlot, after seconds: TimeInterval, _ work: @escaping @Sendable (RemoteConnection) -> Void) {
        schedule(slot, at: now.addingTimeInterval(seconds), work)
    }

    func schedule(_ slot: TimerSlot, at deadline: Date, _ work: @escaping @Sendable (RemoteConnection) -> Void) {
        timers[slot]?.cancel()
        let generation = attempt
        timers[slot] = environment.scheduling.schedule(at: deadline, on: queue) { [weak self] in
            guard let self, !self.stopped, self.attempt == generation else { return }
            self.timers[slot] = nil
            work(self)
        }
    }

    func cancel(_ slot: TimerSlot) {
        timers.removeValue(forKey: slot)?.cancel()
    }

    func cancelTimers() {
        for timer in timers.values { timer.cancel() }
        timers.removeAll()
    }

    private func onQueue(_ work: @escaping @Sendable (RemoteConnection) -> Void) {
        queue.async { work(self) }
    }

    /// Runs `body` on the connection's queue and waits (tests).
    func sync<T>(_ body: () -> T) -> T {
        queue.sync(execute: body)
    }
}
