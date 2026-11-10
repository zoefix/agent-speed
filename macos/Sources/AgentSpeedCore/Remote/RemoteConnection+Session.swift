import Foundation

/// The protocol state of one running relay (REMOTE 5, 6, 8.2).
struct RemoteSessionState {
    var framer = RemoteLineFramer()
    var decoder: RelayLineDecoder?
    var invalidLines = 0
    var helloReceived = false
    /// When `hello` arrived: the back-off resets after 60 s up.
    var helloAt: Date?
    var ready = false
    var relayVersion: String?
    var relayArch: String?
    var clock = RemoteClockEstimator()
    var pendingPings: [Int64: Int64] = [:]
    var nextPingId: Int64 = 1
    var lastLineAt = Date.distantPast
    /// Prefixed ids of the agents this connection sent and did not remove (at most 4096).
    var agentIds: Set<String> = []
    var historyInFlight = false
    var historyDuePending = false
    /// The clock offset last reported, in whole seconds.
    var reportedOffsetSeconds: Int64?
}

extension RemoteConnection {
    /// The relay runs: `start` is its first line, then the watch if it is on.
    func beginSession() {
        session.decoder = RelayLineDecoder(hostId: host.id)
        session.lastLineAt = now
        scheduleSilenceCheck()
        send(RelayLineEncoder.start(todayStartMs: environment.todayStartMs(at: now), sources: settings.sources,
                                    zone: RemoteZone.current(environment.timeZone()), prices: settings.prices,
                                    quota: settings.quota))
        if settings.watch { send(RelayLineEncoder.watch(true)) }
    }

    func receivedRelayOutput(_ data: Data) {
        session.lastLineAt = now
        let (lines, oversized) = session.framer.consume(data)
        for line in lines {
            guard stage == .run, endReason == nil else { return }
            handle(line)
        }
        for _ in 0..<oversized where endReason == nil { countInvalid() }
    }

    private func handle(_ line: Data) {
        guard let decoder = session.decoder else { return }
        switch decoder.decode(line) {
        case .ignored:
            break
        case .invalid:
            countInvalid()
        case .message(let message):
            apply(message)
        }
    }

    /// More than 100 invalid lines end the connection (REMOTE 5.6).
    func countInvalid() {
        session.invalidLines += 1
        if session.invalidLines > RemoteLimits.invalidLines { endProcess(reason: .protocolError) }
    }

    private func apply(_ message: RelayMessage) {
        guard session.helloReceived else {
            guard case .hello(let hello) = message else {
                countInvalid()
                return
            }
            received(hello)
            return
        }
        switch message {
        case .hello:
            countInvalid()
        case .clock(let id, let remoteNowMs):
            guard let sentMs = session.pendingPings.removeValue(forKey: id) else { return }
            session.clock.addSample(sendMs: sentMs, receiveMs: Clock.milliseconds(now), remoteNowMs: remoteNowMs)
            reportClockIfChanged()
        case .progress(let fraction):
            if !session.ready { report(.seeding(fraction: fraction)) }
        case .ready:
            guard !session.ready else { return }
            session.ready = true
            emit(.message(.ready, seeded: true))
            report(.connected)
            historyDue()
            schedule(.history, after: Self.historyInterval) { $0.historyTimerFired() }
            scheduleMidnight()
        case .agent(let agent):
            if !session.agentIds.contains(agent.id), session.agentIds.count >= RemoteLimits.agents { return }
            guard emitConverted(message) else { return }
            session.agentIds.insert(agent.id)
        case .agentRemoved(let id):
            session.agentIds.remove(id)
            emit(.message(message, seeded: !session.ready))
        case .response, .tool, .quota:
            _ = emitConverted(message)
        case .quotaBridge(let bridge):
            emit(.message(message, seeded: !session.ready))
            if retiring, bridge.state == .off { stopNow() }
        case .historyDone:
            emit(.message(message, seeded: !session.ready))
            historyRunEnded()
        case .error(let error) where error.code == .historyFailed:
            emit(.message(message, seeded: !session.ready))
            historyRunEnded()
        default:
            emit(.message(message, seeded: !session.ready))
        }
    }

    private func received(_ hello: RelayHello) {
        session.helloReceived = true
        guard hello.protocolVersion == RemoteProtocol.version else {
            endProcess(reason: .versionMismatch(relayProtocol: hello.protocolVersion))
            return
        }
        session.helloAt = now
        session.relayVersion = hello.version
        session.relayArch = hello.arch
        session.clock.noteHello(remoteNowMs: hello.nowMs, localMs: Clock.milliseconds(now))
        session.reportedOffsetSeconds = session.clock.offsetMs.map { $0 / 1000 }
        emit(.message(.hello(hello), seeded: true))
        report(.seeding(fraction: 0))
        sendPing()
    }

    /// Converts the line's times with the current offset; a time that leaves 2000–2100 makes it invalid.
    private func emitConverted(_ message: RelayMessage) -> Bool {
        guard let converted = message.converted(offsetMs: session.clock.offsetMs ?? 0) else {
            countInvalid()
            return false
        }
        emit(.message(converted, seeded: !session.ready))
        return true
    }

    private func reportClockIfChanged() {
        let seconds = session.clock.offsetMs.map { $0 / 1000 }
        guard seconds != session.reportedOffsetSeconds, let status = lastStatus?.status else { return }
        session.reportedOffsetSeconds = seconds
        report(status)
    }

    // MARK: Pings and silence

    func sendPing() {
        let id = session.nextPingId
        session.nextPingId += 1
        session.pendingPings[id] = Clock.milliseconds(now)
        if session.pendingPings.count > Self.pendingPingLimit, let oldest = session.pendingPings.keys.min() {
            session.pendingPings[oldest] = nil
        }
        send(RelayLineEncoder.ping(id: id))
        schedule(.ping, after: Self.pingInterval) { $0.sendPing() }
    }

    /// 150 s without any line end the connection as `unreachable` (REMOTE 4.4).
    private func scheduleSilenceCheck() {
        schedule(.silence, at: session.lastLineAt.addingTimeInterval(Self.silenceLimit)) { connection in
            guard connection.stage == .run else { return }
            if connection.now.timeIntervalSince(connection.session.lastLineAt) >= Self.silenceLimit {
                connection.endProcess(reason: .unreachable)
            } else {
                connection.scheduleSilenceCheck()
            }
        }
    }

    // MARK: History (REMOTE 8.2)

    /// Sends `history` with the digests the app holds for the host. Ignored before `ready`.
    public func requestHistory(digests: [String: String]) {
        queue.async { [self] in
            guard stage == .run, session.ready, let line = RelayLineEncoder.history(digests: digests) else { return }
            session.historyInFlight = true
            send(line)
        }
    }

    /// The app's time zone changed: the relay gets the zone and the new local midnight, the schedule its next one.
    public func noteTimeZoneChange() {
        queue.async { [self] in
            guard stage == .run else { return }
            send(RelayLineEncoder.zone(RemoteZone.current(environment.timeZone())))
            send(RelayLineEncoder.today(todayStartMs: environment.todayStartMs(at: now)))
            if session.ready { scheduleMidnight() }
        }
    }

    /// A `historyDone` or a `historyFailed` error ends the run: a sync that waited for it is due now.
    private func historyRunEnded() {
        session.historyInFlight = false
        guard session.historyDuePending else { return }
        session.historyDuePending = false
        historyDue()
    }

    /// Asks the app for a sync, or once the running one is done.
    private func historyDue() {
        if session.historyInFlight {
            session.historyDuePending = true
        } else {
            emit(.historyDue)
        }
    }

    private func historyTimerFired() {
        historyDue()
        schedule(.history, after: Self.historyInterval) { $0.historyTimerFired() }
    }

    private func scheduleMidnight() {
        schedule(.midnight, at: environment.nextMidnight(after: now)) { connection in
            connection.send(RelayLineEncoder.today(todayStartMs: connection.environment.todayStartMs(at: connection.now)))
            connection.historyDue()
            connection.scheduleMidnight()
        }
    }
}
