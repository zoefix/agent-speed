#if DEBUG
import AgentSpeedCore
import Foundation

/// The `perfScore` part of `--report-e2e` (`AGENT_SPEED_REPORT_E2E_PERFSCORE=1`, server with `--perf-score`), over the
/// transport the run uses: the real controller with the harness's snapshots standing in for the monitor (a live score
/// and a completion log marked with the controller's watch epoch). The server's merged model table
/// (`GET {api}/public/models`) must count every completion once and show the latest live values.
@MainActor
enum ReportPerfScoreHarness {
    typealias Check = ReportStreamHarness.Check
    typealias Wait = ReportStreamHarness.Wait

    static let enabled = ProcessInfo.processInfo.environment["AGENT_SPEED_REPORT_E2E_PERFSCORE"] == "1"
    static let opus = "claude-opus-5-5"
    static let gpt = "gpt-5.5"

    /// The completions the harness's snapshots carry, as the engine's log would.
    private static var log = ReportPerfCompletionLog()
    private static var lastTable: [String: Any] = [:]

    /// The facts of a harness snapshot: one Claude Code agent on Opus, live while busy, resting while idle.
    static func facts(busy: Bool) -> ReportPerfFacts {
        guard enabled else { return .empty }
        let value = PerfSetValue(form: busy ? .live : .resting, score: 1286.4, agentId: "harness-agent", lastLineMs: nil)
        let thinking = busy ? 3.25 : 0
        return ReportPerfFacts(value: value, thinking: thinking,
                               models: [opus: ReportPerfModel(value: value, thinking: thinking,
                                                              liveSources: busy ? [.claudeCode] : [])],
                               completions: log)
    }

    static func run(_ model: AppModel, publish: @escaping @MainActor () -> Void, check: Check, wait: Wait) async {
        let report = model.report
        check("perfScore: the service advertises it", report.discovery?.extensions?.perfScore == true)
        await wait("perfScore: the watch is on", 10) { report.perfTracker.epoch != nil }
        guard let epoch = report.perfTracker.epoch, let api = report.discovery?.api.string,
              let tableURL = URL(string: api + "/public/models") else { return }

        respond(epoch, opus, .claudeCode, output: 400, reasoning: 120)
        respond(epoch, opus, .claudeCode, output: 300, reasoning: 0)
        respond(epoch, opus, .claudeCode, output: 500, reasoning: 200)
        call(epoch, opus, .claudeCode, ms: 820, failed: false)
        call(epoch, opus, .claudeCode, ms: 1400, failed: nil)
        respond(epoch, gpt, .codex, output: 900, reasoning: 0)
        respond(epoch, gpt, .codex, output: 700, reasoning: 0)
        call(epoch, gpt, .codex, ms: 2600, failed: true)
        respond(epoch + 7, gpt, .codex, output: 100, reasoning: 0)
        await waitForTable("perfScore: the completions reach the service", tableURL, wait) {
            n($0, opus) == 3 && tools($0, opus) == 2 && n($0, gpt) == 2 && tools($0, gpt) == 1
        }
        let delivered = deliveries(report)
        await wait("perfScore: three more reports", 30) { deliveries(report) >= delivered + 3 }
        await waitForTable("perfScore: each completion is counted once", tableURL, wait) {
            n($0, opus) == 3 && tools($0, opus) == 2 && n($0, gpt) == 2 && tools($0, gpt) == 1
        }
        check("perfScore: the rows add up by source", {
            let opusSources = bySource(lastTable, opus)
            let gptSources = bySource(lastTable, gpt)
            return opusSources.count == 1 && opusSources.first?["source"] as? String == "claudeCode"
                && gptSources.first?["source"] as? String == "codex" && gptSources.first?["n"] as? Int == 2
        }())
        check("perfScore: the live values are accepted", {
            let top = lastTable["live"] as? [String: Any]
            let live = row(lastTable, opus)?["live"] as? [String: Any]
            return top?["score"] as? Int == 1286 && top?["form"] as? String == "live"
                && (top?["thinkingTokS"] as? NSNumber)?.doubleValue == 3.3
                && live?["score"] as? Int == 1286 && live?["devices"] as? Int == 1
        }())
        check("perfScore: no report was refused", report.problem == nil
              && (report.stream?.scheduler.counters.rejected ?? 0) == 0)

        // A completion just before pausing goes out with the leaving report; the break then empties the ledger.
        respond(epoch, opus, .claudeCode, output: 250, reasoning: 0)
        publish()
        report.pause(for: 3600)
        check("perfScore: paused, the watch is off and the ledger empty", report.perfTracker.epoch == nil
              && report.perfTracker.ledger.isEmpty)
        await waitForTable("perfScore: the leaving report carried the last completion", tableURL, wait) {
            n($0, opus) == 4
        }
        report.resume()
        await wait("perfScore: resumed, a new watch", 10) { (report.perfTracker.epoch ?? 0) > epoch }
        let resumed = report.perfTracker.epoch ?? 0
        respond(epoch, opus, .claudeCode, output: 600, reasoning: 0)
        respond(resumed, gpt, .codex, output: 800, reasoning: 0)
        await waitForTable("perfScore: only the new watch's completion counts", tableURL, wait) {
            n($0, gpt) == 3 && n($0, opus) == 4
        }

        report.setSection(.models, on: false)
        check("perfScore: the models switch off ends the watch", report.perfTracker.epoch == nil)
        let off = deliveries(report)
        await wait("perfScore: reports go on without models", 30) { deliveries(report) >= off + 2 }
        await waitForTable("perfScore: the service clears the live values and keeps the sums", tableURL, wait) {
            row($0, opus)?["live"] is NSNull && n($0, opus) == 4 && n($0, gpt) == 3
        }
        report.setSection(.models, on: true)
        await wait("perfScore: models on again, a new watch", 10) { (report.perfTracker.epoch ?? 0) > resumed }
        await waitForTable("perfScore: live values again", tableURL, wait) {
            (row($0, opus)?["live"] as? [String: Any])?["score"] as? Int == 1286
        }
        check("perfScore: still nothing refused", report.problem == nil
              && (report.stream?.scheduler.counters.rejected ?? 0) == 0)
    }

    // MARK: - Helpers

    /// A measured response that ended a second ago: 3 s long, its first content after 0.8 s.
    private static func respond(_ epoch: Int, _ model: String, _ source: SourceKind, output: Int, reasoning: Int) {
        let end = ReportClock.unixMs - 1000
        log.append(epoch: epoch, agentId: "harness-agent", modelId: model, source: source,
                   kind: .response(startMs: end - 3000, endMs: end, firstMs: end - 2200, output: output,
                                   reasoning: reasoning, input: 40_000, cachedInput: 36_000))
    }

    private static func call(_ epoch: Int, _ model: String, _ source: SourceKind, ms: Int64, failed: Bool?) {
        let result = ReportClock.unixMs - 500
        log.append(epoch: epoch, agentId: "harness-agent", modelId: model, source: source,
                   kind: .call(callMs: result - ms, resultMs: result, failed: failed))
    }

    /// Delivered reports of either transport.
    private static func deliveries(_ report: ReportController) -> Int {
        report.stream.map { $0.scheduler.counters.accepted + $0.scheduler.counters.duplicate } ?? deliveredOverHTTP(report)
    }

    private static var httpDeliveries = 0
    private static var lastHTTPDelivery: Date?

    private static func deliveredOverHTTP(_ report: ReportController) -> Int {
        if let at = report.lastDeliveryAt, at != lastHTTPDelivery {
            lastHTTPDelivery = at
            httpDeliveries += 1
        }
        return httpDeliveries
    }

    private static func waitForTable(_ name: String, _ url: URL, _ wait: Wait,
                                     _ condition: @escaping @MainActor ([String: Any]) -> Bool) async {
        let deadline = Date().addingTimeInterval(20)
        while Date() < deadline {
            if let table = await fetch(url) {
                lastTable = table
                if condition(table) { break }
            }
            try? await Task.sleep(for: .milliseconds(400))
        }
        let table = lastTable
        await wait(name, 0) { condition(table) }
    }

    private static func fetch(_ url: URL) async -> [String: Any]? {
        let session = URLSession(configuration: .ephemeral)
        defer { session.finishTasksAndInvalidate() }
        guard let (data, response) = try? await session.data(from: url),
              (response as? HTTPURLResponse)?.statusCode == 200 else { return nil }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    private static func row(_ table: [String: Any], _ id: String) -> [String: Any]? {
        (table["models"] as? [[String: Any]])?.first { $0["id"] as? String == id }
    }

    private static func bySource(_ table: [String: Any], _ id: String) -> [[String: Any]] {
        row(table, id)?["bySource"] as? [[String: Any]] ?? []
    }

    private static func n(_ table: [String: Any], _ id: String) -> Int { row(table, id)?["n"] as? Int ?? 0 }

    private static func tools(_ table: [String: Any], _ id: String) -> Int { row(table, id)?["tools"] as? Int ?? 0 }
}
#endif
