import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeed

/// DESIGN 11, What's sent and Preview window: the models switch's caption follows the service's known document —
/// `report.section.models.perfScore` with the `perfScore` extension and before any document is known,
/// `report.section.models.thinking` with only the retired `taskScore` — and the preview assumes `perfScore` too.
@Suite("Report performance score caption")
@MainActor
struct ReportPerfScoreCaptionTests {
    static let service = ReportController.previewService
    static let allSections = #"["sources","models","cost","traffic"]"#
    static let perf = "report.section.models.perfScore"
    static let thinking = "report.section.models.thinking"

    static func document(sections: String = allSections, extensions: String?) throws -> ReportDiscovery {
        let tail = extensions.map { #","extensions":\#($0)"# } ?? ""
        let text = """
        {"protocol":"agent-speed-report","versions":[1],
         "service":{"name":"x","terms":"https://example.com/t","privacy":"https://example.com/p"},
         "api":"https://api.example.com","sections":\(sections)\(tail)}
        """
        return try ReportDiscovery.parse(Data(text.utf8), service: service).get()
    }

    // MARK: - The caption

    @Test func namesThePerformanceScoreWhileTheKnownDocumentAdvertisesIt() throws {
        #expect(ReportSectionToggles.modelsCaptionKey(try Self.document(extensions: #"{"perfScore":1}"#)) == Self.perf)
        #expect(ReportSectionToggles.modelsCaptionKey(
            try Self.document(extensions: #"{"reportWebSocket":1,"perfScore":1,"taskScore":1}"#)) == Self.perf)
    }

    @Test func namesTheThinkingSpeedAloneForTheRetiredTaskScore() throws {
        #expect(ReportSectionToggles.modelsCaptionKey(try Self.document(extensions: #"{"taskScore":1}"#)) == Self.thinking)
    }

    @Test func hidesWhenTheKnownDocumentAdvertisesNeither() throws {
        for extensions in [nil, "{}", #"{"perfScore":0}"#, #"{"perfScore":true}"#, #"{"reportWebSocket":1}"#] {
            #expect(ReportSectionToggles.modelsCaptionKey(try Self.document(extensions: extensions)) == nil,
                    "\(extensions ?? "no extensions")")
        }
    }

    @Test func namesThePerformanceScoreBeforeAnyDocumentIsKnown() {
        #expect(ReportSectionToggles.modelsCaptionKey(nil) == Self.perf)
        #expect(ReportSectionToggles.modelsCaptionKey(ReportDiscovery.assumed(for: Self.service, name: "x")) == Self.perf)
    }

    /// `report.section.notAccepted` stands there instead.
    @Test func hidesWhileTheModelsSectionIsNotAccepted() throws {
        let document = try Self.document(sections: #"["sources","cost"]"#, extensions: #"{"perfScore":1}"#)
        #expect(!document.sections.contains(.models))
        #expect(ReportSectionToggles.modelsCaptionKey(document) == nil)
    }

    // MARK: - The preview

    /// Before the document is known (the dashboard invitation) the body carries the extension's fields; the demo
    /// service advertises it, so its pane and consent sheet previews carry them too.
    @Test(arguments: [ReportController.PreviewState.invite, .reporting, .consent])
    func thePreviewCarriesThePerformanceScore(_ state: ReportController.PreviewState) throws {
        let rig = Rig()
        defer { rig.tearDown() }
        rig.report.preview(state, username: "zoe-mbp")
        let preview = try #require(rig.report.preview(draft: rig.report.consent))
        #expect(preview.assumed == (state == .invite))
        let body = try #require(try JSONSerialization.jsonObject(with: preview.body.compactData) as? [String: Any])
        let top = try #require(body["perfScore"] as? [String: Any])
        #expect(top["score"] as? Int == 1286 && top["form"] as? String == "live")
        #expect((top["thinkingTokS"] as? NSNumber)?.doubleValue == 38.6)
        #expect(body["taskScore"] == nil)
        let models = try #require(body["models"] as? [[String: Any]])
        let scores = Dictionary(uniqueKeysWithValues: models.compactMap { model -> (String, Int)? in
            guard let id = model["id"] as? String, let score = (model["perfScore"] as? [String: Any])?["score"] as? Int
            else { return nil }
            return (id, score)
        })
        #expect(scores == ["claude-opus-5-5": 1286, "gpt-5.5": 802])
    }
}

/// A controller on its own defaults and folder whose transports never reach the network.
@MainActor
private struct Rig {
    let suite = "AgentSpeedReportPerfScoreCaptionTests-\(UUID().uuidString)"
    let folder: URL
    let report: ReportController

    init() {
        let defaults = UserDefaults(suiteName: suite)!
        folder = FileManager.default.temporaryDirectory.appendingPathComponent(suite, isDirectory: true)
        report = ReportController(
            settings: AppSettings(defaults: defaults), store: SpeedStore(), traffic: TrafficStore(),
            priceBook: PriceBook(), appData: folder, transport: OfflineTransport(), streamTransport: OfflineStream(),
            version: "1.4.1")
    }

    func tearDown() {
        report.shutdown()
        UserDefaults.standard.removePersistentDomain(forName: suite)
        try? FileManager.default.removeItem(at: folder)
    }
}

private final class OfflineTransport: ReportTransport, @unchecked Sendable {
    func get(_ url: URL, userAgent: String, maxBytes: Int, timeout: TimeInterval)
        async throws(ReportTransportError) -> ReportHTTPResponse {
        ReportHTTPResponse(status: 404)
    }

    func send(_ request: ReportRequest, maxBytes: Int, timeout: TimeInterval)
        async throws(ReportTransportError) -> ReportHTTPResponse {
        ReportHTTPResponse(status: 503)
    }
}

private final class OfflineStream: ReportStreamTransport, @unchecked Sendable {
    func link(
        settings: ReportStreamSettings, queue: DispatchQueue,
        onEvent: @escaping @Sendable (ReportStreamEvent) -> Void,
        onSigningFailure: @escaping @Sendable () -> Void
    ) -> any ReportStreamLink {
        OfflineLink()
    }
}

private final class OfflineLink: ReportStreamLink, @unchecked Sendable {
    func start(_ makeRequest: @escaping @Sendable () throws -> ReportRequest) {}
    func send(_ makeText: @escaping @Sendable () throws -> String) {}
    func close() {}
    func abort() {}
}
