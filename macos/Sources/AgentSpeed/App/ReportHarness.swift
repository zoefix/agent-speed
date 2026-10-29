#if DEBUG
import AgentSpeedCore
import AppKit

/// `--report-e2e URL`: drives the real live reporting controller — the dashboard invitation (declined, then accepted),
/// consent, the Secure Enclave key, registration, reports while busy and idle, rename, key rotation, pause, API key,
/// network off, turning off and Forget — against a local reference server (`spec/tools/report_server.py`). Run it with
/// `AGENT_SPEED_APP_DATA` pointing at a temporary folder and `AGENT_SPEED_REPORT_PRESET_URL` set to the same URL, so
/// the preset never reaches the real service; settings live in the `AgentSpeedReportE2E` suite. Prints one line per
/// check and quits. With `AGENT_SPEED_REPORT_E2E_WEBSOCKET=1` (and the server started with `--websocket`) it drives the
/// WebSocket transport instead, see `ReportStreamHarness`; with `AGENT_SPEED_REPORT_E2E_PERFSCORE=1` (and the server
/// started with `--perf-score`) it first checks the performance score extension, see `ReportPerfScoreHarness`.
@MainActor
enum ReportHarness {
    private static var feed: Timer?
    private static var busy = false
    private static var counters = ReportCounters()
    private static var failures = 0

    static func start(url: String) {
        let suite = "AgentSpeedReportE2E"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        let model = AppModel(settings: AppSettings(defaults: defaults),
                             updates: UpdateController(configuration: .current(environment: [:]), store: UpdateStore(defaults: defaults)))
        model.activate(live: false)
        model.report.activate()
        let feed = Timer(timeInterval: 0.5, repeats: true) { _ in
            MainActor.assumeIsolated { publish(model) }
        }
        RunLoop.main.add(feed, forMode: .common)
        self.feed = feed
        Task {
            await run(model, url: url)
            print("REPORT-E2E \(failures == 0 ? "PASSED" : "FAILED (\(failures))")")
            UserDefaults.standard.removePersistentDomain(forName: suite)
            exit(failures == 0 ? 0 : 1)
        }
    }

    private static func publish(_ model: AppModel) {
        if busy {
            counters.add(ReportUsage(requests: 1, output: 120, input: 40_000, cachedInput: 36_000, measuredOutput: 120,
                                     measuredMs: 900, costMicroUsd: 1800), to: ReportModelKey(modelId: "claude-opus-5-5", source: .claudeCode))
        }
        var snapshot = PreviewData.snapshot(speed: busy ? 240 : 0, redline: 2000)
        if busy { snapshot.observedAtMs = ReportClock.monotonicMs - 4 }
        if !busy {
            snapshot.live = .zero
            snapshot.liveCost = .zero
            for index in snapshot.sources.indices {
                snapshot.sources[index].live = .zero
                snapshot.sources[index].liveEstimated = .zero
                snapshot.sources[index].liveCost = .zero
            }
        }
        snapshot.report = ReportLiveFacts(
            agents: [.claudeCode: ReportAgentCounts(generating: busy ? 1 : 0, active: 1)],
            generatingModels: busy ? [ReportModelKey(modelId: "claude-opus-5-5", source: .claudeCode): 1] : [:],
            counters: counters, perf: ReportPerfScoreHarness.facts(busy: busy))
        model.store.publishLive(snapshot)
    }

    private static func run(_ model: AppModel, url: String) async {
        let report = model.report
        let apiKey = ProcessInfo.processInfo.environment["AGENT_SPEED_REPORT_E2E_API_KEY"]
        let profileE2E = ProcessInfo.processInfo.environment["AGENT_SPEED_PROFILE_E2E"] == "1"
        if apiKey == nil && !profileE2E { await invitation(model, url: url) }
        report.selectPreset(.custom)
        report.commitCustomURL(url)
        await check("discovery", within: 10) { if case .ready = report.serviceState { true } else { false } }

        if profileE2E { report.updatePreferences { $0.username = ReportUsername.random() } }
        let expectedName = report.nextUsername(rules: report.usernameRules)
        report.setEnabled(true)
        await check("consent sheet opens with the fitted computer name", within: 10) {
            report.consent?.username == expectedName
        }
        check("nothing is created before Start", report.identity == nil && keyFiles(model).isEmpty)
        if let apiKey { report.consent?.apiKey = apiKey }
        report.startReporting()
        await check("registered and on", within: 10) { report.isOn && report.identity?.username == expectedName }
        let deviceId = report.identity?.deviceId ?? ""
        check("Secure Enclave key handle stored with mode 0600", fileMode(model, "\(deviceId).sekey") == 0o600)
        if apiKey != nil {
            check("API key sealed (no Keychain)", fileMode(model, "\(deviceId).apikey") == 0o600
                  && fileMode(model, "\(deviceId).seagree") == 0o600)
            check("sealed API key opens again", ReportKeyStore.standard(appData: AppModel.appDataDirectory)
                .loadAPIKey(deviceId: deviceId) == apiKey)
        }

        if ProcessInfo.processInfo.environment["AGENT_SPEED_PROFILE_E2E"] == "1" {
            check("profile extension lifecycle", await ReportProfileHarness.run(first: model, url: url))
            return
        }

        if ReportPerfScoreHarness.enabled {
            busy = true
            await ReportPerfScoreHarness.run(model, publish: { publish(model) }, check: { check($0, $1) },
                                             wait: { name, seconds, condition in await check(name, within: seconds, condition) })
        }

        if ProcessInfo.processInfo.environment["AGENT_SPEED_REPORT_E2E_WEBSOCKET"] == "1" {
            await ReportStreamHarness.run(model, busy: { busy = $0 }, check: { check($0, $1) },
                                          wait: { name, seconds, condition in await check(name, within: seconds, condition) })
            feed?.invalidate()
            return
        }

        if ProcessInfo.processInfo.environment["AGENT_SPEED_REPORT_BENCH"] != nil {
            await bench(model)
        }

        busy = true
        await check("reports delivered while busy", within: 20) { report.lastDeliveryAt != nil && report.mode == .active }
        if report.discovery?.websocket == nil {
            check("WebSocket chosen by default, the service lacks it: reports go over HTTP",
                  report.transport == .websocket && report.effectiveTransport == .http && report.run != nil
                      && report.stream == nil)
        }
        let firstDelivery = report.lastDeliveryAt
        await check("a second active report", within: 8) { report.lastDeliveryAt != firstDelivery }

        report.commitUsername("racer-e2e01")
        await check("rename", within: 10) { report.renameState == .updated && report.identity?.username == "racer-e2e01" }
        let renamedDelivery = report.lastDeliveryAt
        await check("report under the new name", within: 10) { report.lastDeliveryAt != renamedDelivery }

        let oldFingerprint = report.fingerprint
        report.rotateKeyIfDue(force: true)
        await check("key rotated", within: 10) { report.pendingSigner == nil && report.fingerprint != oldFingerprint }
        let rotatedDelivery = report.lastDeliveryAt
        await check("report signed with the rotated key", within: 10) {
            report.lastDeliveryAt != rotatedDelivery && report.problem == nil
        }

        report.pause(for: 3600)
        check("paused", report.status == .paused(until: report.preferences.pausedUntil ?? .distantPast))
        try? await Task.sleep(for: .seconds(1))
        report.resume()

        model.settings.networkEnabled = false
        let offDelivery = report.lastDeliveryAt
        try? await Task.sleep(for: .seconds(7))
        check("network off: nothing sent", report.lastDeliveryAt == offDelivery && report.status == .networkOff)
        model.settings.networkEnabled = true
        await check("network back on: reporting resumes", within: 12) { report.lastDeliveryAt != offDelivery }

        busy = false
        await check("idle after 15 quiet seconds", within: 20) { report.mode == .idle }
        let idleStart = report.lastDeliveryAt
        await check("idle heartbeat", within: 70) { report.lastDeliveryAt != idleStart }

        report.setEnabled(false)
        try? await Task.sleep(for: .seconds(2))
        check("turned off, registration kept", !report.preferences.enabled && report.identity?.isRegistered == true)

        report.forget()
        await check("forgotten on the service", within: 10) { report.forgetState == .done }
        check("keys deleted locally", keyFiles(model).isEmpty && report.identity == nil)
        feed?.invalidate()
    }

    // MARK: - Invitation

    /// The dashboard invitation for the preset, pointed at the local server: declining never touches the network and
    /// creates nothing; accepting is the consent that fetches, creates the key, registers and reports.
    private static func invitation(_ model: AppModel, url: String) async {
        let report = model.report
        guard let local = try? ReportServiceURL.parse(url).get(), report.selectedService == local else {
            check("the preset points at the local server (AGENT_SPEED_REPORT_PRESET_URL)", false)
            return
        }
        let counting = CountingReportTransport(base: report.api.transport)
        report.api.transport = counting
        check("invitation offered on a fresh install", report.invitationOffered)
        check("showing it touches nothing", counting.count == 0 && report.serviceState == .unknown)

        report.declineInvitation()
        try? await Task.sleep(for: .seconds(3))
        check("declined: answered and never offered again", report.preferences.invite == .declined && !report.invitationOffered)
        check("declined: no request, no key, nothing stored",
              counting.count == 0 && keyFiles(model).isEmpty && report.preferences.identities.isEmpty
                  && !report.preferences.enabled)

        report.updatePreferences { $0.invite = nil }
        check("offered again once the answer is reset", report.invitationOffered && counting.count == 0)
        let fitted = report.nextUsername(rules: report.usernameRules)
        report.acceptInvitation()
        check("accepted: the done line shows and the indicator waits", report.inviteJustAccepted && !report.indicatorShown)
        await check("accepted: registered as the fitted computer name", within: 10) {
            report.isOn && report.identity?.username == fitted
        }
        check("accepted: answered, the default sections kept", report.preferences.invite == .accepted
              && report.preferences.sections == ReportPreferences.defaultSections)
        busy = true
        await check("accepted: reports delivered while busy", within: 20) {
            report.lastDeliveryAt != nil && report.status.tone == .green
        }
        busy = false
        await check("accepted: the indicator appears after the done line", within: 5) { report.indicatorShown }
        // Discovery and registration over HTTP; the reports too, or over WebSocket to the same server when it offers it.
        let requests = counting.count
        let streamed = report.stream?.scheduler.counters.accepted ?? 0
        check("accepted: requests went to the local server only",
              report.effectiveTransport == .websocket ? requests >= 2 && streamed >= 1 : requests >= 3)

        report.forget()
        await check("invitation device forgotten on the service", within: 10) { report.forgetState == .done }
        report.dismissForgetResult()
        check("forgotten: off, keys deleted, invitation stays answered", !report.preferences.enabled
              && keyFiles(model).isEmpty && report.preferences.invite == .accepted && !report.invitationOffered)
        report.api.transport = counting.base
    }

    // MARK: - CPU

    /// The reporting loop's cost: per-tick and per-report CPU in isolation, then the whole process over 30 s with
    /// reporting off, on and busy, and on and idle (the snapshot feed itself is the same in every phase).
    private static func bench(_ model: AppModel) async {
        let report = model.report
        busy = true
        publish(model)
        let ticks = 2000
        let tickStart = threadCPU()
        for _ in 0..<ticks { report.tick() }
        let perTick = (threadCPU() - tickStart) / Double(ticks)
        let sample = report.lastSample
        let envelope = ReportEnvelope(client: report.client, deviceId: report.identity?.deviceId ?? "", username: "bench",
                                      runId: ReportCanonical.newUUID(), seq: 1, sentAtMs: ReportClock.unixMs, windowMs: 5000,
                                      activity: .active, titleLevel: 3)
        let options = ReportOptions(sections: Set(ReportSection.allCases))
        let buildStart = threadCPU()
        for _ in 0..<200 { _ = ReportBody.data(envelope, ReportWindow(ReportSample()).content(sample), sample, options) }
        let perBuild = (threadCPU() - buildStart) / 200
        let signStart = threadCPU()
        if let signer = report.signer {
            for _ in 0..<50 { _ = try? signer.signature(for: Data(repeating: 7, count: 300)) }
        }
        let perSign = (threadCPU() - signStart) / 50
        print(String(format: "BENCH tick %.1f µs, body %.1f µs, Secure Enclave signature %.2f ms (thread CPU)",
                     perTick * 1e6, perBuild * 1e6, perSign * 1e3))

        func phase(_ name: String, enabled: Bool, busy isBusy: Bool) async {
            if report.preferences.enabled != enabled { report.setEnabled(enabled) }
            busy = isBusy
            try? await Task.sleep(for: .seconds(3))
            let cpu = processCPU()
            let wall = Date()
            try? await Task.sleep(for: .seconds(30))
            let percent = (processCPU() - cpu) / Date().timeIntervalSince(wall) * 100
            print(String(format: "BENCH %@: %.2f %% CPU (whole process, 30 s)", name, percent))
        }
        await phase("reporting off, agents busy", enabled: false, busy: true)
        await phase("reporting on, agents busy", enabled: true, busy: true)
        await phase("reporting on, agents idle", enabled: true, busy: false)
        busy = false
    }

    private static func threadCPU() -> Double {
        Double(clock_gettime_nsec_np(CLOCK_THREAD_CPUTIME_ID)) / 1e9
    }

    private static func processCPU() -> Double {
        var usage = rusage()
        getrusage(RUSAGE_SELF, &usage)
        return Double(usage.ru_utime.tv_sec + usage.ru_stime.tv_sec)
            + Double(usage.ru_utime.tv_usec + usage.ru_stime.tv_usec) / 1e6
    }

    // MARK: - Checks

    private static func check(_ name: String, _ ok: Bool) {
        if !ok { failures += 1 }
        print("\(ok ? "PASS" : "FAIL") \(name)")
        fflush(stdout)
    }

    private static func check(_ name: String, within seconds: Double, _ condition: @MainActor () -> Bool) async {
        let deadline = Date().addingTimeInterval(seconds)
        while !condition(), Date() < deadline {
            try? await Task.sleep(for: .milliseconds(200))
        }
        check(name, condition())
    }

    private static func keyFiles(_ model: AppModel) -> [String] {
        let folder = AppModel.appDataDirectory.appendingPathComponent("report")
        return ((try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []).filter { !$0.hasPrefix(".") }
    }

    private static func fileMode(_ model: AppModel, _ name: String) -> Int? {
        let path = AppModel.appDataDirectory.appendingPathComponent("report").appendingPathComponent(name).path
        return (try? FileManager.default.attributesOfItem(atPath: path)[.posixPermissions] as? Int) ?? nil
    }
}

/// Counts the requests the controller makes (discovery and API), passing them on unchanged.
private final class CountingReportTransport: ReportTransport, @unchecked Sendable {
    let base: any ReportTransport
    private let lock = NSLock()
    private var calls = 0

    init(base: any ReportTransport) {
        self.base = base
    }

    var count: Int { lock.withLock { calls } }

    private func bump() { lock.withLock { calls += 1 } }

    func get(_ url: URL, userAgent: String, maxBytes: Int, timeout: TimeInterval)
        async throws(ReportTransportError) -> ReportHTTPResponse {
        bump()
        return try await base.get(url, userAgent: userAgent, maxBytes: maxBytes, timeout: timeout)
    }

    func send(_ request: ReportRequest, maxBytes: Int, timeout: TimeInterval)
        async throws(ReportTransportError) -> ReportHTTPResponse {
        bump()
        return try await base.send(request, maxBytes: maxBytes, timeout: timeout)
    }
}
#endif
