import AgentSpeedCore
import Foundation

/// The live side (METRICS 11.5): every engine tick feeds the window and the scheduler; built reports are signed and
/// sent one at a time; each outcome goes back to the scheduler and triggers the reaction of REPORTING 8.3.
extension ReportController {
    enum Attempt: Sendable {
        case reply(Result<ReportReply, ReportTransportError>)
        case signingFailed
    }

    func handleLive(_ snapshot: SpeedSnapshot) {
        monitorRunning = snapshot.state == .running
        guard monitorRunning else {
            // Seeding again: what it reads is history, and the ledger goes (METRICS 11.10 *Watch*).
            updatePerfWatch()
            return
        }
        lastSnapshot = snapshot
        // Off and unwatched: nothing to compute until reporting or a preview needs this tick.
        guard run != nil || stream != nil || preferences.enabled || previewWindow != nil else {
            if perfTracker.epoch != nil { updatePerfWatch() }
            return
        }
        if let observed = snapshot.observedAtMs { pendingObservation = min(pendingObservation ?? observed, observed) }
        tick()
    }

    /// The latest tick's values (METRICS 11.2): the live snapshot and the latest traffic sample.
    var lastSample: ReportSample {
        guard let snapshot = lastSnapshot else { return cachedSample }
        lastSnapshot = nil
        cachedSample = ReportSample(
            snapshot: snapshot,
            traffic: ReportTrafficSample(snapshot: traffic.snapshot, includeTools: settings.trafficIncludeTools,
                                         enabled: SourceSet(settings.enabledSources)))
        return cachedSample
    }

    /// A user event (pause, resume, network, stop) is handled at once with the latest values.
    func post(_ event: ReportScheduler.Event) {
        pendingEvents.append(event)
        tick()
    }

    /// Readiness the scheduler does not track itself (METRICS 11.5, Ready).
    func isReady() -> Bool {
        guard preferences.enabled, monitorRunning, problem == nil, signer != nil, let identity, identity.isRegistered,
              identity.pin != nil, !needsReview, let document = identity.discovery, document.supportsThisClient,
              consent?.service.origin != identity.id else { return false }
        return !isDiscoveryStale
    }

    /// Picks the transport by the effective one (METRICS 11.9); a run left on the other one is replaced first. The
    /// performance score's watch follows each tick's actions, so a leaving report takes the ledger before a break.
    func tick() {
        let effective = effectiveTransport
        if (run != nil && effective == .websocket) || (stream != nil && effective == .http) {
            transportChanged()
            return
        }
        defer { updatePerfWatch() }
        let sample = lastSample
        let now = ReportClock.monotonicMs
        previewWindow?.observe(sample)
        let completed = beginPerfTick(sample)
        if effective == .websocket {
            streamTick(sample, now: now, wake: false, completed: completed)
            return
        }
        startRunIfNeeded(sample)
        guard var run else {
            pendingEvents.removeAll()
            return
        }
        let events = pendingEvents
        pendingEvents.removeAll()
        let working = sample.facts.agents.values.contains { $0.generating + $0.tools > 0 }
        let active = working || sample.live.output > 0 || run.window.hasTokenIncrement(sample)
        run.title.update(value: sample.value(for: settings.metric), redline: settings.redline, now: Double(now) / 1000)
        let actions = run.scheduler.tick(at: now, active: active, blocked: !isReady(), events: events)
        self.run = run
        apply(actions, sample: sample, now: now)
        if let scheduler = self.run?.scheduler, mode != scheduler.mode { mode = scheduler.mode }
        if events.contains(.stop) { endRunWhenIdle() }
    }

    func apply(_ actions: [ReportScheduler.Action], sample: ReportSample, now: Int64) {
        var windowStarted = false
        for action in actions {
            switch action {
            case .discard(let keepingTick):
                run?.window.discard(sample, keepingTick: keepingTick)
                windowStarted = true
            case .build(let ticket):
                build(ticket, sample: sample)
                windowStarted = true
            case .send(let ticket):
                send(ticket)
            case .delivered(let ticket), .lost(let ticket):
                run?.bodies[ticket.seq] = nil
                run?.signWithPending.remove(ticket.seq)
            }
        }
        run?.window.observe(sample, peaks: !windowStarted)
    }

    // MARK: - Runs

    func startRunIfNeeded(_ sample: ReportSample) {
        guard run == nil, effectiveTransport == .http, preferences.enabled, monitorRunning, signer != nil, let identity, identity.isRegistered,
              let document = identity.discovery else { return }
        run = ReportRun(
            origin: identity.id,
            scheduler: ReportScheduler(limits: document.limits, paused: preferences.isPaused,
                                       networkOff: !settings.networkEnabled),
            window: ReportWindow(sample))
        renamedForMismatch = false
    }

    /// Ends the run of either transport (a new service, identity or transport, or reporting gone).
    func endRun() {
        run = nil
        endStream()
        if mode != .quiet { mode = .quiet }
    }

    /// After `stop`: the run ends once its leaving report is answered (or at once when nothing is in flight).
    func endRunWhenIdle() {
        guard let run else { return }
        if run.scheduler.isBusy {
            let id = run.id
            DispatchQueue.main.asyncAfter(deadline: .now() + ReportAPI.apiTimeout + 1) { [weak self] in
                if self?.run?.id == id, self?.run?.scheduler.stopped == true { self?.endRun() }
            }
        } else if run.scheduler.stopped {
            endRun()
        }
    }

    // MARK: - Building and sending

    func build(_ ticket: ReportScheduler.Ticket, sample: ReportSample) {
        guard var run, let identity, let username = identity.username, let document = identity.discovery else { return }
        var content = run.window.take(sample)
        let options = options(for: document)
        content.perfStats = takePerfStats(options)
        let envelope = ReportEnvelope(
            client: client, deviceId: identity.deviceId, username: username, runId: run.id, seq: ticket.seq,
            sentAtMs: ReportClock.unixMs + clockCorrection * 1000, windowMs: ticket.windowMs, activity: ticket.activity,
            titleLevel: run.title.level)
        run.bodies[ticket.seq] = ReportBody.data(envelope, content, sample, options)
        self.run = run
    }

    func options(for document: ReportDiscovery) -> ReportOptions {
        ReportOptions(
            sections: preferences.sections.intersection(document.sections), maxModels: document.limits.maxModels,
            bodyLimit: document.limits.sendLimitBytes, metric: settings.metric, redline: settings.redline,
            priceVersion: priceBook.reportPriceVersion(atMs: ReportClock.unixMs), extensions: document.extensions ?? .none)
    }

    func send(_ ticket: ReportScheduler.Ticket) {
        guard let run, let body = run.bodies[ticket.seq], let identity, let document = identity.discovery else {
            complete(ticket, runId: self.run?.id ?? "", attempt: .reply(.failure(.other(-3))), usedPending: false)
            return
        }
        let usedPending = run.signWithPending.contains(ticket.seq)
        guard let key = usedPending ? pendingSigner : signer else {
            complete(ticket, runId: run.id, attempt: .signingFailed, usedPending: usedPending)
            return
        }
        let builder = ReportRequestBuilder(userAgent: userAgent, deviceId: identity.deviceId, apiKey: apiKey)
        let url = document.api.appending(endpoint: ReportEndpoint.reports)
        let timestamp = ReportClock.unixMs / 1000 + clockCorrection
        let api = self.api
        let runId = run.id
        Task { [weak self] in
            let attempt = await Self.perform(api: api, builder: builder, method: "POST", url: url, body: body,
                                             signer: key, timestamp: timestamp)
            self?.complete(ticket, runId: runId, attempt: attempt, usedPending: usedPending)
        }
    }

    /// Signs (off the main actor; inside the Secure Enclave) and sends one request.
    nonisolated static func perform(
        api: ReportAPI, builder: ReportRequestBuilder, method: String, url: URL, body: Data, signer: any ReportSigner,
        timestamp: Int64, newKey: (any ReportSigner)? = nil
    ) async -> Attempt {
        guard let request = try? builder.make(method: method, url: url, body: body, signer: signer, timestamp: timestamp,
                                              newKey: newKey) else { return .signingFailed }
        return .reply(await api.call(request))
    }

    func complete(_ ticket: ReportScheduler.Ticket, runId: String, attempt: Attempt, usedPending: Bool) {
        let now = ReportClock.monotonicMs
        let outcome: ReportScheduler.Outcome
        let reaction: ReportReaction
        switch attempt {
        case .signingFailed:
            (outcome, reaction) = (.rejected, .needsUser(.keyMismatch))
        case .reply(let result):
            (outcome, reaction) = ReportOutcome.classify(result, hasPendingKey: pendingSigner != nil && !usedPending)
            note(result, outcome: outcome, usedPending: usedPending)
        }
        guard var run, run.id == runId else { return }
        if reaction == .usePendingKey { run.signWithPending.insert(ticket.seq) }
        let actions = run.scheduler.finish(ticket, outcome, at: now)
        self.run = run
        apply(actions, sample: lastSample, now: now)
        react(reaction)
        if quitReply != nil, self.run?.scheduler.isBusy != true { finishQuit() }
        if self.run?.scheduler.stopped == true, self.run?.scheduler.isBusy == false { endRun() }
    }

    /// Status bookkeeping of one answer.
    func note(_ result: Result<ReportReply, ReportTransportError>, outcome: ReportScheduler.Outcome, usedPending: Bool) {
        switch outcome {
        case .delivered:
            lastDeliveryAt = Date()
            if case .success(let reply) = result, let rank = reply.rank { self.rank = rank }
            retryReason = nil
            rateLimitedUntil = nil
            strikes = 0
            if usedPending { promotePendingKey() }
            rotateKeyIfDue()
        case .transient:
            switch result {
            case .success: retryReason = .unavailable
            case .failure(let error): retryReason = error.failure
            }
        case .rateLimited(let seconds):
            rateLimitedUntil = Date().addingTimeInterval(TimeInterval(seconds))
            retryReason = nil
        case .again, .rejected:
            break
        }
    }

    // MARK: - Reactions (REPORTING 8.3)

    func react(_ reaction: ReportReaction) {
        switch reaction {
        case .none, .usePendingKey:
            break
        case .correctClock(let serverTime):
            guard let serverTime else { return }
            let correction = serverTime - ReportClock.unixMs / 1000
            if abs(correction) <= 86_400 { clockCorrection = correction }
        case .reRegister:
            guard lastReRegister.map({ Date().timeIntervalSince($0) >= Self.reRegisterInterval }) ?? true else { return }
            lastReRegister = Date()
            reRegister(recheck: false)
        case .recheckRegistration:
            reRegister(recheck: true)
        case .rename:
            guard !renamedForMismatch, let name = identity?.username else { return }
            renamedForMismatch = true
            rename(to: name, automatic: true)
        case .newRun:
            endRun()
        case .refreshDiscovery(let upgrade):
            if upgrade { problem = .upgrade }
            refreshDiscoveryIfDue(force: true)
        case .strike:
            strikes += 1
            if strikes >= 3 { problem = .rejected }
        case .needsUser(let needed):
            problem = needed
            if needed == .gone {
                updatePreferences { $0.enabled = false }
                endRun()
            }
        }
    }
}
