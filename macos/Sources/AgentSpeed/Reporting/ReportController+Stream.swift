import AgentSpeedCore
import Foundation

/// Live reporting over WebSocket (METRICS 11.9, DESIGN 11 item 2b), the default transport: every snapshot, every
/// wake-up the stream scheduler asks for and every event of the current connection drive the stream session; it
/// connects, builds, signs and writes reports and asks for the reactions of REPORTING 8.3. Nothing of this runs while
/// HTTP is effective — chosen, or WebSocket chosen on a service without the capability — and a stop of this transport
/// never falls back to HTTP.
extension ReportController: ReportStreamSessionDelegate {
    /// Quitting waits this long after the leaving connection closed, so its close frame goes out.
    static let closeFrameGrace: TimeInterval = 0.15

    // MARK: - The user's side

    /// The user's choice, kept per service. It applies at once when it changes the effective transport; WebSocket
    /// chosen on a service without the capability leaves the HTTP run going.
    func setTransport(_ transport: ReportTransportKind) {
        guard let service = selectedService, transport != self.transport else { return }
        let effective = effectiveTransport
        updatePreferences { $0.setTransport(transport, for: service.origin) }
        if effectiveTransport != effective { transportChanged() }
    }

    /// A change of the effective transport (METRICS 11.9) — the user's choice, or a stored or refreshed document that
    /// adds or drops the capability: the old transport stops without a leaving report (its unanswered reports are
    /// dropped) and a new run starts on the new one.
    func transportChanged() {
        endRun()
        retryReason = nil
        rateLimitedUntil = nil
        lastDeliveryAt = nil
        tick()
    }

    /// *Try Again* after `report.status.wsMismatch` or `report.status.wsReplaced`: clears the stop and reconnects.
    func retryStream() {
        guard let stream, settings.networkEnabled else { return }
        stream.session.retry()
        tick()
    }

    // MARK: - Ticks

    /// One tick of the stream scheduler: a snapshot, a user event or (`wake`) a wake-up it asked for. `completed`: the
    /// statistics ledger received a completion at this tick (METRICS 11.10 *WebSocket*).
    func streamTick(_ sample: ReportSample, now: Int64, wake: Bool, completed: Bool = false) {
        startStreamIfNeeded(sample)
        let events = pendingEvents
        pendingEvents.removeAll()
        let observed = wake ? nil : pendingObservation
        if !wake { pendingObservation = nil }
        guard var stream else { return }
        let working = sample.facts.agents.values.contains { $0.generating + $0.tools > 0 } || sample.live.output > 0
        let active = wake ? stream.lastActive : working || stream.window.hasTokenIncrement(sample)
        let changed = !wake && (completed
            || stream.window.hasIncrement(sample, traffic: effectiveSections.contains(.traffic))
            || ReportStreamValues(sample, perfScore: streamPerfScoreValues(stream.window, sample))
                .changed(since: stream.lastBuilt))
        stream.lastActive = active
        stream.windowStarted = false
        stream.title.update(value: sample.value(for: settings.metric), redline: settings.redline, now: Double(now) / 1000)
        self.stream = stream
        if let stop = stream.scheduler.stop, ReportStreamIssue.problem(for: stop) != nil, problem == nil {
            // The user fixed what the stop stood for (a key, the sections): automatic attempts resume.
            stream.session.retry()
        }
        stream.session.tick(at: now, active: active, changed: changed, observed: observed, blocked: !isReady(),
                            events: events)
    }

    func startStreamIfNeeded(_ sample: ReportSample) {
        guard stream == nil, effectiveTransport == .websocket, preferences.enabled, monitorRunning, signer != nil,
              let identity, identity.isRegistered, let document = identity.discovery else { return }
        let scheduler = ReportStreamScheduler(
            settings: document.websocket, heartbeatSeconds: document.limits.heartbeatSeconds,
            paused: preferences.isPaused, networkOff: !settings.networkEnabled)
        let session = ReportStreamSession(scheduler: scheduler, transport: streamTransport,
                                          clock: { ReportClock.monotonicMs })
        session.delegate = self
        stream = ReportStreamRun(origin: identity.id, session: session, window: ReportWindow(sample))
        renamedForMismatch = false
        publishStream()
    }

    /// Ends the session: its connection is closed and nothing it awaits is answered any more (no leaving).
    func endStream() {
        guard let stream else { return }
        self.stream = nil
        stream.session.delegate = nil
        stream.session.shutdown()
        streamState = nil
        streamStats = nil
        statsPublishedAt = nil
        pendingObservation = nil
        if mode != .quiet { mode = .quiet }
    }

    /// At quit: `leaving` when the connection is open and active, then `reply` once it closed, within a second.
    func prepareStreamToQuit(_ reply: @escaping () -> Void) {
        guard let stream, stream.scheduler.connection == .open, stream.scheduler.mode != .quiet, isReady(),
              settings.networkEnabled else {
            endStream()
            reply()
            return
        }
        quitReply = reply
        post(.stop)
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in self?.finishQuit() }
    }

    // MARK: - Discovery

    /// A refreshed document that keeps the capability reaches the session: settings for the next connection.
    func streamDiscovery(_ document: ReportDiscovery) {
        guard let stream, document.websocket != nil else { return }
        stream.session.update(heartbeatSeconds: document.limits.heartbeatSeconds)
        stream.session.settingsChanged(document.websocket)
    }

    /// The forced refresh after a `ready` with other limits could not be made.
    func streamRefreshFailed() {
        stream?.session.refreshFailed()
    }

    // MARK: - ReportStreamSessionDelegate

    func streamSession(_ session: ReportStreamSession, discardBeforeIncrements: Bool) {
        guard stream?.session === session else { return }
        stream?.window.discard(lastSample, keepingTick: discardBeforeIncrements)
        stream?.windowStarted = true
    }

    func streamSession(_ session: ReportStreamSession, bodyFor ticket: ReportStreamScheduler.Ticket) -> Data? {
        guard var stream, stream.session === session, let identity, let username = identity.username,
              let document = identity.discovery else { return nil }
        let sample = lastSample
        var content = stream.window.take(sample)
        var options = options(for: document)
        options.messageLimit = session.scheduler.limits.maxMessageBytes
        content.perfStats = takePerfStats(options)
        stream.lastBuilt = ReportStreamValues(sample, perfScore: ReportBody.perfScoreValues(content, sample, options))
        stream.windowStarted = true
        self.stream = stream
        let envelope = ReportEnvelope(
            client: client, deviceId: identity.deviceId, username: username, runId: session.runId, seq: ticket.seq,
            sentAtMs: ReportClock.unixMs + clockCorrection * 1000, windowMs: ticket.windowMs, activity: ticket.activity,
            titleLevel: stream.title.level, realtime: ReportRealtime(observationAgeMs: ticket.observationAgeMs))
        return ReportBody.fitted(envelope, content, sample, options).data
    }

    func streamSessionCredentials(_ session: ReportStreamSession) -> ReportStreamCredentials? {
        guard let identity, let document = identity.discovery,
              let key = stream?.signsWithPending == true ? pendingSigner ?? signer : signer else { return nil }
        return ReportStreamCredentials(
            api: document.api,
            builder: ReportRequestBuilder(userAgent: userAgent, deviceId: identity.deviceId, apiKey: apiKey),
            signer: key, clockCorrection: clockCorrection)
    }

    func streamSession(_ session: ReportStreamSession, react reaction: ReportStreamScheduler.Reaction, serverTime: Int64?) {
        guard stream?.session === session else { return }
        switch reaction {
        case .correctClock:
            react(.correctClock(serverTime: serverTime))
        case .reRegister:
            react(.reRegister)
        case .recheckRegistration:
            if pendingSigner != nil, stream?.signsWithPending == false {
                stream?.signsWithPending = true
            } else {
                reRegister(recheck: true)
            }
        case .rename:
            react(.rename(registered: nil))
        case .newRun:
            stream?.startOver(lastSample)
        case .refreshDiscovery:
            refreshDiscoveryIfDue(force: true)
            if serviceState != .checking {
                DispatchQueue.main.async { [weak self] in self?.streamRefreshFailed() }
            }
        }
    }

    func streamSessionSigningFailed(_ session: ReportStreamSession) {
        guard stream?.session === session else { return }
        react(.needsUser(.keyMismatch))
    }

    func streamSessionDidChange(_ session: ReportStreamSession) {
        guard stream?.session === session else { return }
        publishStream()
    }

    func streamSessionWake(_ session: ReportStreamSession) {
        guard stream?.session === session else { return }
        let sample = lastSample
        _ = beginPerfTick(sample)
        streamTick(sample, now: ReportClock.monotonicMs, wake: true)
    }

    // MARK: - After each step

    /// The window's observation, a stop's shared problem, the delivery, the wait, the mode, the status inputs and the
    /// stats, the end of a finished session and the quit reply.
    private func publishStream() {
        guard var stream else { return }
        let scheduler = stream.scheduler
        stream.window.observe(lastSample, peaks: !stream.windowStarted)
        let newStop = scheduler.stop != stream.handledStop ? scheduler.stop : nil
        stream.handledStop = scheduler.stop
        let promotes = stream.signsWithPending && scheduler.counters.delivered > stream.delivered
        if promotes { stream.signsWithPending = false }
        stream.delivered = scheduler.counters.delivered
        self.stream = stream

        if let newStop { halted(newStop) }
        noteDelivery(stream.session, promotesPendingKey: promotes)
        noteWait(scheduler)
        if mode != scheduler.mode { mode = scheduler.mode }
        let state = ReportStreamState(scheduler)
        if state != streamState { streamState = state }
        publishStreamStats()

        if scheduler.ended, scheduler.connection == .closed {
            DispatchQueue.main.async { [weak self] in
                guard let self, self.stream?.session === stream.session else { return }
                self.endStream()
            }
        }
        if quitReply != nil, scheduler.connection == .closed {
            DispatchQueue.main.asyncAfter(deadline: .now() + Self.closeFrameGrace) { [weak self] in self?.finishQuit() }
        }
    }

    /// The last acknowledged report, at most once a second for the status line (several can arrive in one).
    private func noteDelivery(_ session: ReportStreamSession, promotesPendingKey: Bool) {
        if promotesPendingKey { promotePendingKey() }
        guard let delivered = session.lastDeliveredAt else { return }
        if let rank = session.scheduler.rank, rank != self.rank { self.rank = rank }
        guard lastDeliveryAt.map({ delivered.timeIntervalSince($0) >= 1 }) ?? true else { return }
        lastDeliveryAt = delivered
        retryReason = nil
        rotateKeyIfDue()
    }

    /// `Retry-After` on the monotonic clock, as the local time the status line shows.
    private func noteWait(_ scheduler: ReportStreamScheduler) {
        let now = ReportClock.monotonicMs
        guard scheduler.rateLimitedUntil > now else {
            if rateLimitedUntil != nil { rateLimitedUntil = nil }
            return
        }
        let until = Date().addingTimeInterval(Double(scheduler.rateLimitedUntil - now) / 1000)
        if rateLimitedUntil.map({ abs($0.timeIntervalSince(until)) > 1 }) ?? true { rateLimitedUntil = until }
    }

    /// A stop that the shared status line has a sentence for becomes that problem; the user's fix clears it.
    private func halted(_ stop: ReportStreamScheduler.Stop) {
        guard let problem = ReportStreamIssue.problem(for: stop) else { return }
        self.problem = problem
        if problem == .gone {
            updatePreferences { $0.enabled = false }
            DispatchQueue.main.async { [weak self] in self?.endRun() }
        }
    }

    /// The counters and latency change with every answer; the stats line takes them at most once a second.
    private func publishStreamStats(force: Bool = false) {
        guard let scheduler = stream?.scheduler else { return }
        let now = Date()
        if !force, let at = statsPublishedAt, now.timeIntervalSince(at) < 1 {
            guard !statsPending else { return }
            statsPending = true
            DispatchQueue.main.asyncAfter(deadline: .now() + (1 - now.timeIntervalSince(at))) { [weak self] in
                self?.statsPending = false
                self?.publishStreamStats(force: true)
            }
            return
        }
        statsPublishedAt = now
        let stats = ReportStreamStats(scheduler)
        if stats != streamStats { streamStats = stats }
    }
}
