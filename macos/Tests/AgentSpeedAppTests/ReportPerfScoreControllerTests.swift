import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeed

/// METRICS 11.10 (performance score) in the report controller: the watch follows the service, the switches and the user's state and
/// reaches the monitor as an epoch; completions of that watch join the ledger once; a build takes it; a break empties it
/// after the tick's leaving report took it.
@Suite("Report performance score controller")
@MainActor
struct ReportPerfScoreControllerTests {
    static let service = ReportController.previewService
    static let advertising = ReportController.previewDiscovery
    static let silent: ReportDiscovery = {
        let text = """
        {"protocol":"agent-speed-report","versions":[1],
         "service":{"name":"x","terms":"https://example.com/terms","privacy":"https://example.com/privacy"},
         "api":"https://api.example.com/agent-speed","sections":["sources","models","cost","traffic"]}
        """
        return try! ReportDiscovery.parse(Data(text.utf8), service: service).get()
    }()

    /// A measured response of `output` tokens over 2 s, its first content after 0.6 s.
    static func response(output: Int) -> ReportPerfCompletion.Kind {
        .response(startMs: 1_800_000_000_000, endMs: 1_800_000_002_000, firstMs: 1_800_000_000_600, output: output,
                  reasoning: 0, input: 10_000, cachedInput: 9000)
    }

    @Test func theWatchFollowsTheSwitchesAndReachesTheMonitor() {
        let rig = Rig()
        defer { rig.tearDown() }
        rig.register(Self.advertising)
        #expect(Self.advertising.extensions?.perfScore == true)
        rig.tick()
        #expect(rig.epochs == [1])
        #expect(rig.report.perfWatchKey == Self.service.origin)

        rig.report.setSection(.models, on: false)
        #expect(rig.epochs == [1, nil], "the models switch off is a break")
        rig.report.setSection(.models, on: true)
        #expect(rig.epochs == [1, nil, 2])

        rig.report.updateIdentity(Self.service.origin) { $0.discovery = Self.silent }
        rig.tick()
        #expect(rig.epochs.last == .some(nil), "a document without the capability")
        rig.report.updateIdentity(Self.service.origin) { $0.discovery = Self.advertising }
        rig.tick()
        #expect(rig.epochs.last == 3)

        rig.report.settings.networkEnabled = false
        rig.tick()
        #expect(rig.epochs.last == .some(nil), "network off")
    }

    @Test func seedingAgainIsABreak() {
        let rig = Rig()
        defer { rig.tearDown() }
        rig.register(Self.advertising)
        rig.tick()
        var starting = PreviewData.snapshot(speed: 0, redline: 2000)
        starting.state = .starting(progress: 0.5)
        rig.report.handleLive(starting)
        #expect(rig.epochs == [1, nil])
        rig.tick()
        #expect(rig.epochs == [1, nil, 2])
    }

    @Test func completionsOfTheWatchJoinTheLedgerOnce() {
        let rig = Rig()
        defer { rig.tearDown() }
        rig.register(Self.advertising)
        rig.tick()
        let epoch = rig.report.perfTracker.epoch!
        rig.log.append(epoch: epoch, agentId: "a1", modelId: "claude-opus-5-5", source: .claudeCode,
                       kind: Self.response(output: 864))
        rig.log.append(epoch: epoch + 9, agentId: "a2", modelId: "gpt-5.5", source: .codex, kind: Self.response(output: 50))
        rig.tick()
        rig.tick()
        #expect(rig.report.perfTracker.ledger.responses == 1)
        #expect(rig.report.perfTracker.pending["claude-opus-5-5"]?[.claudeCode]?.outputSum == 864)

        // The preview shows what the next build takes, without taking it.
        let previewText = rig.report.preview().map { String(decoding: $0.body.compactData, as: UTF8.self) } ?? ""
        #expect(previewText.contains(#""outputSum":864"#))
        #expect(rig.report.perfTracker.ledger.responses == 1)
    }

    /// Pausing sends a leaving report that carries the pending completions; the break then empties the ledger.
    @Test func aLeavingReportTakesTheLedgerBeforeTheBreak() async throws {
        let rig = Rig()
        defer { rig.tearDown() }
        rig.register(Self.advertising)
        rig.tick()
        rig.log.append(epoch: rig.report.perfTracker.epoch!, agentId: "a1", modelId: "claude-opus-5-5", source: .claudeCode,
                       kind: .call(callMs: 1_800_000_000_000, resultMs: 1_800_000_000_940, failed: false))
        rig.tick()
        #expect(rig.report.perfTracker.ledger.calls == 1)
        rig.report.pause(for: 3600)
        #expect(rig.report.perfTracker.ledger.isEmpty && rig.epochs.last == .some(nil))
        try await rig.waitUntil { !rig.transport.requests.isEmpty }
        let request = try #require(rig.transport.requests.last)
        let body = try #require(try JSONSerialization.jsonObject(with: request.body) as? [String: Any])
        #expect(body["activity"] as? String == "leaving")
        let models = try #require(body["models"] as? [[String: Any]])
        let opus = try #require(models.first { $0["id"] as? String == "claude-opus-5-5" })
        let rows = try #require(opus["perfStats"] as? [[String: Any]])
        #expect(rows.count == 1 && rows.first?["toolN"] as? Int == 1 && rows.first?["source"] as? String == "claudeCode")
        #expect(rows.first?["activeMs"] as? Int == 940)
        #expect(body["perfScore"] != nil && body["taskScore"] == nil)
    }

    @Test func aBuildTakesTheLedgerOnlyWhileTheExtensionIsActive() {
        let rig = Rig()
        defer { rig.tearDown() }
        rig.register(Self.advertising)
        rig.tick()
        rig.log.append(epoch: rig.report.perfTracker.epoch!, agentId: "a3", modelId: "gpt-5.5", source: .codex,
                       kind: Self.response(output: 400))
        rig.tick()
        let inactive = ReportOptions(sections: [.sources])
        #expect(rig.report.takePerfStats(inactive).isEmpty)
        #expect(rig.report.perfTracker.ledger.responses == 1)
        let legacy = ReportOptions(sections: [.models], extensions: ReportExtensions(taskScore: true))
        #expect(rig.report.takePerfStats(legacy).isEmpty, "the retired task score takes no statistics")
        let active = ReportOptions(sections: [.models], extensions: ReportExtensions(perfScore: true))
        #expect(rig.report.takePerfStats(active)["gpt-5.5"]?[.codex]?.n == 1)
        #expect(rig.report.perfTracker.ledger.isEmpty)
    }
}

/// A controller on its own settings suite and folder, with transports that never leave the process, and a completion
/// log the ticks' snapshots carry.
@MainActor
private final class Rig {
    let suite = "AgentSpeedReportPerfScoreControllerTests-\(UUID().uuidString)"
    let folder: URL
    let transport = RecordingTransport()
    let report: ReportController
    var log = ReportPerfCompletionLog()
    var epochs: [Int?] = []

    init() {
        let defaults = UserDefaults(suiteName: suite)!
        folder = FileManager.default.temporaryDirectory.appendingPathComponent(suite, isDirectory: true)
        report = ReportController(
            settings: AppSettings(defaults: defaults), store: SpeedStore(), traffic: TrafficStore(),
            priceBook: PriceBook(), appData: folder, transport: transport, streamTransport: SilentStreamTransport(),
            version: "1.4.1")
        report.perfWatchHandler = { [weak self] epoch in self?.epochs.append(epoch) }
    }

    func tearDown() {
        report.shutdown()
        UserDefaults.standard.removePersistentDomain(forName: suite)
        try? FileManager.default.removeItem(at: folder)
    }

    /// Registered with the example service on `document` over HTTP, reporting on, a software key.
    func register(_ document: ReportDiscovery) {
        let now = Date()
        var preferences = ReportPreferences()
        preferences.enabled = true
        preferences.preset = .custom
        preferences.customURL = ReportController.previewService.string
        preferences.invite = .accepted
        preferences.identities = [ReportIdentity(
            service: ReportController.previewService, deviceId: ReportCanonical.newUUID(), username: "racer-test",
            profileURL: nil, pin: ReportConsentPin(document), discovery: document, discoveryFetchedAt: now,
            keyCreatedAt: now, lastUsedAt: now)]
        preferences.setTransport(.http, for: ReportController.previewService.origin)
        report.preferences = preferences
        report.signer = SoftwareReportSigner()
    }

    /// One engine tick with the monitor running, an agent generating with a live score, and the completion log.
    func tick() {
        var snapshot = PreviewData.snapshot(speed: 240, redline: 2000)
        let value = PerfSetValue(form: .live, score: 1286.4, agentId: "a1", lastLineMs: nil)
        snapshot.report = ReportLiveFacts(
            agents: [.claudeCode: ReportAgentCounts(generating: 1, active: 1)],
            generatingModels: [ReportModelKey(modelId: "claude-opus-5-5", source: .claudeCode): 1],
            counters: ReportCounters(),
            perf: ReportPerfFacts(value: value, thinking: 3.25,
                                  models: ["claude-opus-5-5": ReportPerfModel(value: value, thinking: 3.25,
                                                                              liveSources: [.claudeCode])],
                                  completions: log))
        report.handleLive(snapshot)
    }

    func waitUntil(_ condition: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(5)
        while !condition(), Date() < deadline {
            try await Task.sleep(for: .milliseconds(20))
        }
        #expect(condition())
    }
}

/// Answers every API request with `accepted` and keeps it; discovery is not fetched in these tests.
private final class RecordingTransport: ReportTransport, @unchecked Sendable {
    private let lock = NSLock()
    private var sent: [ReportRequest] = []

    var requests: [ReportRequest] { lock.withLock { sent } }

    func get(_ url: URL, userAgent: String, maxBytes: Int, timeout: TimeInterval)
        async throws(ReportTransportError) -> ReportHTTPResponse {
        ReportHTTPResponse(status: 404)
    }

    func send(_ request: ReportRequest, maxBytes: Int, timeout: TimeInterval)
        async throws(ReportTransportError) -> ReportHTTPResponse {
        lock.withLock { sent.append(request) }
        return ReportHTTPResponse(status: 200, headers: ["content-type": "application/json"],
                                  body: Data(#"{"status":"accepted"}"#.utf8))
    }
}

private final class SilentStreamTransport: ReportStreamTransport, @unchecked Sendable {
    func link(
        settings: ReportStreamSettings, queue: DispatchQueue,
        onEvent: @escaping @Sendable (ReportStreamEvent) -> Void,
        onSigningFailure: @escaping @Sendable () -> Void
    ) -> any ReportStreamLink {
        SilentStreamLink()
    }
}

private final class SilentStreamLink: ReportStreamLink, @unchecked Sendable {
    func start(_ makeRequest: @escaping @Sendable () throws -> ReportRequest) {}
    func send(_ makeText: @escaping @Sendable () throws -> String) {}
    func close() {}
    func abort() {}
}
