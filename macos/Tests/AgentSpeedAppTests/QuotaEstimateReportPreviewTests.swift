@testable import AgentSpeed
import AgentSpeedCore
import Foundation
import Testing

/// DESIGN 11 *Preview window* and REPORTING-EXTENSIONS 11: the preview shows the `quotas` the next report carries
/// while the 額度 switch is on and the service advertises `quotaEstimate`, each account by its tag for the service and
/// never by its key.
@Suite("Quota estimate report preview")
@MainActor
struct QuotaEstimateReportPreviewTests {
    static let now: Int64 = 1_794_338_773_000

    private static func quotas(_ preview: ReportController.Preview?) throws -> [[String: Any]]? {
        let preview = try #require(preview)
        let body = try #require(try JSONSerialization.jsonObject(with: preview.body.compactData) as? [String: Any])
        return body["quotas"] as? [[String: Any]]
    }

    @Test func thePreviewCarriesTheQuotasWhileTheSwitchIsOnAndTheServiceTakesThem() throws {
        let rig = PreviewRig()
        defer { rig.tearDown() }
        let build = QuotaEstimatePreviews.demoBuild(now: Self.now)
        var origins: [String] = []
        rig.report.quotaEntries = { _, origin in
            origins.append(origin)
            return build.entries(nowMs: Self.now, origin: origin)
        }
        rig.report.preview(.reporting, username: "zoe-mbp")
        #expect(rig.report.discovery?.extensions?.quotaEstimate == true)

        let entries = try #require(try Self.quotas(rig.report.preview()))
        #expect(entries.map { $0["source"] as? String } == ["codex", "claudeCode"])
        #expect(entries.map { $0["plan"] as? String } == ["pro", "claude_max"])
        let api = try #require(rig.report.discovery?.api.origin)
        #expect(origins.last == api, "tagged for the origin reports go to")
        for entry in entries {
            let source = try #require(entry["source"] as? String)
            let key = try #require(build.account(of: QuotaEstimateSource(source: source)))
            #expect(entry["account"] as? String == QuotaAccounts.reportTag(origin: api, key: key))
            #expect(entry["account"] as? String != key && entry["key"] == nil, "no account key is ever reported")
            let estimate = try #require(entry["estimate"] as? [String: Any])
            let cap = try #require((estimate["capMicroUsd"] as? NSNumber)?.doubleValue)
            let truth = entry["source"] as? String == "codex" ? 397e6 : 5742e6
            #expect(abs(cap / truth - 1) < 0.02, "the demo's caps come back within 2 %")
        }

        rig.report.setShareQuota(false)
        #expect(try Self.quotas(rig.report.preview()) == nil, "the 額度 switch off leaves them out")
        rig.report.setShareQuota(true)
        #expect(try Self.quotas(rig.report.preview())?.count == 2)

        // The consent sheet follows its own switch and the service's document.
        rig.report.preview(.consent, username: "zoe-mbp")
        #expect(try Self.quotas(rig.report.preview(draft: rig.report.consent))?.count == 2)
        rig.report.consent?.shareQuota = false
        #expect(try Self.quotas(rig.report.preview(draft: rig.report.consent)) == nil)
        rig.report.consent?.shareQuota = true
        rig.report.consent?.discovery = ReportController.previewDiscovery.withoutQuotaEstimate()
        #expect(try Self.quotas(rig.report.preview(draft: rig.report.consent)) == nil,
                "a service that does not advertise quotaEstimate gets none")
    }
}

private extension ReportDiscovery {
    func withoutQuotaEstimate() -> ReportDiscovery {
        var copy = self
        copy.extensions?.quotaEstimate = false
        return copy
    }
}

/// A controller on its own defaults and folder whose transports never reach the network.
@MainActor
private struct PreviewRig {
    let suite = "QuotaEstimateReportPreviewTests-\(UUID().uuidString)"
    let folder: URL
    let report: ReportController

    init() {
        let defaults = UserDefaults(suiteName: suite)!
        folder = FileManager.default.temporaryDirectory.appendingPathComponent(suite, isDirectory: true)
        report = ReportController(
            settings: AppSettings(defaults: defaults), store: SpeedStore(), traffic: TrafficStore(),
            priceBook: PriceBook(), appData: folder, transport: NoNetwork(), streamTransport: NoStream(),
            version: "1.7.0")
    }

    func tearDown() {
        report.shutdown()
        UserDefaults.standard.removePersistentDomain(forName: suite)
        try? FileManager.default.removeItem(at: folder)
    }
}

private final class NoNetwork: ReportTransport, @unchecked Sendable {
    func get(_ url: URL, userAgent: String, maxBytes: Int, timeout: TimeInterval)
        async throws(ReportTransportError) -> ReportHTTPResponse {
        ReportHTTPResponse(status: 404)
    }

    func send(_ request: ReportRequest, maxBytes: Int, timeout: TimeInterval)
        async throws(ReportTransportError) -> ReportHTTPResponse {
        ReportHTTPResponse(status: 503)
    }
}

private final class NoStream: ReportStreamTransport, @unchecked Sendable {
    func link(
        settings: ReportStreamSettings, queue: DispatchQueue,
        onEvent: @escaping @Sendable (ReportStreamEvent) -> Void,
        onSigningFailure: @escaping @Sendable () -> Void
    ) -> any ReportStreamLink {
        NoLink()
    }
}

private final class NoLink: ReportStreamLink, @unchecked Sendable {
    func start(_ makeRequest: @escaping @Sendable () throws -> ReportRequest) {}
    func send(_ makeText: @escaping @Sendable () throws -> String) {}
    func close() {}
    func abort() {}
}
