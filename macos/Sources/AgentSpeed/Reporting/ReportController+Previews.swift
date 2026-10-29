import AgentSpeedCore
import Foundation

/// Fixed states for `--render-previews` (group `report`): no key is created, nothing is stored and nothing is sent.
extension ReportController {
    enum PreviewState {
        case off, reporting, paused, attention, consent
        /// The dashboard invitation, offered; and its done line right after `report.invite.accept`.
        case invite, inviteDone
        /// Accepted, but the preset does not serve its document (the pane and the footer's attention dot).
        case setupFailed
        /// Accepted, but the document needs the consent sheet (REPORTING 3.3).
        case review
        /// WebSocket chosen (DESIGN 11, item 2b): streaming with the stats line; a service that throttles it; one
        /// without the capability, which reports over HTTP meanwhile; reconnecting after a failure; replaced by another
        /// connection (Try Again). The other states of the example service have HTTP chosen.
        case streaming, streamThrottled, streamUnsupported, streamReconnecting, streamReplaced
    }

    static let streamStates: Set<PreviewState> = [
        .streaming, .streamThrottled, .streamUnsupported, .streamReconnecting, .streamReplaced,
    ]

    /// The spec's example service (spec/fixtures/report-examples/discovery.json), which takes an optional API key and
    /// every section.
    static let previewService = try! ReportServiceURL.parse("https://example.com").get()

    static var previewDiscovery: ReportDiscovery { discovery(websocket: nil) }

    /// The example service, which also advertises the performance score (METRICS 11.10); `websocket` adds the
    /// WebSocket capability with these settings (METRICS 11.9).
    static func discovery(websocket: String?) -> ReportDiscovery {
        let extensions = (websocket == nil ? "" : #""reportWebSocket":1,"#) + #""perfScore":1"#
        let stream = websocket.map { #","websocket":\#($0)"# } ?? ""
        let document = """
        {"protocol":"agent-speed-report","versions":[1],
         "service":{"name":"Example Leaderboard","description":"Live token speed of AI coding agents, updated every few seconds.",
                    "homepage":"https://example.com/","terms":"https://example.com/terms","privacy":"https://example.com/privacy",
                    "leaderboard":"https://example.com/live","contact":"privacy@example.com","retentionDays":30},
         "api":"https://api.example.com/agent-speed","apiKey":{"mode":"optional","url":"https://example.com/account/keys"},
         "username":{"minLength":2,"maxLength":24,"charset":"unicode","spaces":true},
         "limits":{"intervalSeconds":5,"heartbeatSeconds":60,"maxAgeSeconds":30,"maxBodyBytes":16384,"maxModels":16},
         "sections":["sources","models","cost","traffic"],"extensions":{\(extensions)}\(stream)}
        """
        return try! ReportDiscovery.parse(Data(document.utf8), service: previewService).get()
    }

    func preview(_ state: PreviewState, username: String) {
        let now = Date()
        var preferences = ReportPreferences()
        let onPreset: Set<PreviewState> = [.off, .invite, .inviteDone, .setupFailed, .review]
        let settingUp: Set<PreviewState> = [.inviteDone, .setupFailed, .review]
        preferences.preset = onPreset.contains(state) ? .codexRadar : .custom
        preferences.customURL = Self.previewService.string
        preferences.username = username
        let streaming = Self.streamStates.contains(state)
        let discovery = switch state {
        case .streamUnsupported: Self.previewDiscovery
        case .streamThrottled: Self.discovery(websocket: #"{"path":"/v1/reports/ws","subprotocol":"agent-speed-report.v1","minIntervalMs":200}"#)
        case _ where streaming: Self.discovery(websocket: #"{"path":"/v1/reports/ws","subprotocol":"agent-speed-report.v1"}"#)
        default: Self.previewDiscovery
        }
        if !streaming { preferences.setTransport(.http, for: Self.previewService.origin) }
        let deviceId = "6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b"
        if settingUp.contains(state) {
            preferences.enabled = true
            preferences.invite = .accepted
        } else if !onPreset.contains(state) && state != .consent {
            preferences.enabled = true
            preferences.identities = [ReportIdentity(
                service: Self.previewService, deviceId: deviceId, username: username,
                profileURL: URL(string: "https://example.com/u/zoe"), pin: ReportConsentPin(discovery),
                discovery: discovery, discoveryFetchedAt: now, keyCreatedAt: now, hasAPIKey: true, lastUsedAt: now)]
        }
        if state == .paused { preferences.pausedUntil = now.addingTimeInterval(3600) }
        self.preferences = preferences
        let registered = !onPreset.contains(state) && state != .consent
        serviceState = switch state {
        case .off, .invite: .unknown
        case .inviteDone: .checking
        case .setupFailed: .notSupported
        default: .ready(discovery)
        }
        signer = registered ? SoftwareReportSigner() : nil
        apiKey = registered ? "sk-preview-7Qd2" : nil
        problem = state == .attention ? .keyMismatch : nil
        inviteGap = state == .review ? .apiHost : nil
        inviteJustAccepted = state == .inviteDone
        registrationFailure = nil
        renameState = .idle
        let delivering: Set<PreviewState> = [
            .reporting, .streaming, .streamThrottled, .streamUnsupported, .streamReconnecting, .streamReplaced,
        ]
        lastDeliveryAt = delivering.contains(state) ? now.addingTimeInterval(state == .streamReconnecting ? -42 : -1) : nil
        rank = state == .reporting || state == .streaming ? 12 : nil
        mode = delivering.contains(state) ? .active : .idle
        rateLimitedUntil = nil
        retryReason = nil
        streamState = switch state {
        case .streaming, .streamThrottled: ReportStreamState(open: true)
        case .streamReconnecting: ReportStreamState(open: false, failing: true)
        case .streamReplaced: ReportStreamState(open: false, stop: .replaced)
        default: nil
        }
        streamStats = streaming && state != .streamUnsupported ? Self.previewStats(state) : nil
        cachedSample = Self.previewSample
        lastSnapshot = nil
        consent = state == .consent ? ConsentDraft(
            service: Self.previewService, discovery: discovery, deviceId: deviceId,
            username: ReportUsername.fit(computerName: "Zoe’s MacBook Pro", rules: discovery.usernameRules) ?? username,
            sections: ReportPreferences.defaultSections, isReview: false) : nil
        previewWindow = ReportWindow(ReportSample())
        previewStartedAt = ReportClock.monotonicMs - 4200
    }

    /// A session that delivered most of its windows within the real-time level.
    static func previewStats(_ state: PreviewState) -> ReportStreamStats {
        let slow = state == .streamThrottled
        return ReportStreamStats(
            counters: .init(built: 1287, accepted: 1279, duplicate: 2, rejected: 0, stale: 1, saturated: 0, unknown: 3),
            p50: slow ? 236 : 38, p95: slow ? 412 : 87)
    }

    /// Three agents of two sources and two models, as in the spec's active example, with the performance score of its
    /// `perfScore` example (spec/fixtures/report-examples/report-perfscore.json).
    static var previewSample: ReportSample {
        var counters = ReportCounters()
        counters.add(ReportUsage(requests: 3, output: 1500, input: 800_000, cachedInput: 780_000, cacheWriteInput: 12_050,
                                 measuredOutput: 1400, measuredMs: 3300, costMicroUsd: 18_003),
                     to: ReportModelKey(modelId: "claude-opus-5-5", source: .claudeCode))
        counters.add(ReportUsage(requests: 1, output: 340, input: 112_400, cachedInput: 100_120, reasoning: 310,
                                 measuredOutput: 302, measuredMs: 820, costMicroUsd: 3000),
                     to: ReportModelKey(modelId: "gpt-5.5", source: .codex))
        func value(_ id: String, _ form: PerfValueForm, _ score: Double) -> PerfSetValue {
            PerfSetValue(form: form, score: score, agentId: id, lastLineMs: nil)
        }
        let perf = ReportPerfFacts(
            value: value("a1", .live, 1286.2), thinking: 38.64,
            models: ["claude-opus-5-5": ReportPerfModel(value: value("a1", .live, 1286.2), liveSources: [.claudeCode]),
                     "gpt-5.5": ReportPerfModel(value: value("a3", .live, 802.4), thinking: 38.64, liveSources: [.codex]),
                     "claude-sonnet-5-5": ReportPerfModel(value: value("a6", .resting, 940))])
        let facts = ReportLiveFacts(
            agents: [.codex: ReportAgentCounts(generating: 1, active: 2),
                     .claudeCode: ReportAgentCounts(generating: 2, tools: 1, active: 3)],
            generatingModels: [ReportModelKey(modelId: "claude-opus-5-5", source: .claudeCode): 2,
                               ReportModelKey(modelId: "gpt-5.5", source: .codex): 1],
            counters: counters, perf: perf)
        return ReportSample(
            live: Rate(output: 412.6, total: 182_340.2), estimatedShare: 0.215,
            liveCost: LiveCost(microUsdPerSecond: 4213, knownOutput: 412.6),
            sources: [
                .codex: .init(live: Rate(output: 112.2, total: 32_120.1), liveEstimated: Rate(output: 52.7, total: 0),
                              liveCost: LiveCost(microUsdPerSecond: 613, knownOutput: 112.2)),
                .claudeCode: .init(live: Rate(output: 300.4, total: 150_220.1), liveEstimated: Rate(output: 36, total: 0),
                                   liveCost: LiveCost(microUsdPerSecond: 3600, knownOutput: 300.4)),
            ],
            facts: facts,
            traffic: ReportTrafficSample(
                status: .ok, includesTools: true, rate: TrafficRate(rx: 24_310, tx: 1288),
                sourceRates: [.codex: TrafficRate(rx: 4200, tx: 288), .claudeCode: TrafficRate(rx: 20_110, tx: 1000)],
                totals: TrafficReportTotals(sources: [.codex: TrafficSplit(own: TrafficBytes(rx: 21_000, tx: 1440)),
                                                      .claudeCode: TrafficSplit(own: TrafficBytes(rx: 100_550, tx: 5000))])))
    }
}
