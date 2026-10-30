import AgentSpeedCore
import Foundation

/// `--dump-today`: one JSON object per response that ended today, the performance rows of the seeded agents (METRICS
/// 6.9 *Dumps*), then a summary line.
enum DumpToday {
    /// Costs use `table` for every response, like `reference.py --dump-today` (no day bindings). Performance scores
    /// are evaluated at `now` (Unix ms), else at the capture time.
    static func run(configuration: MonitorConfiguration, table: PriceTable, now: Int64? = nil) {
        let report = TranscriptScan.today(configuration: configuration, perf: true, perfNowMs: now)
        var output = ""
        output.reserveCapacity(report.responses.count * 300)
        var counts: [SourceKind: Int] = [:]
        var costMicroUsd: Int64 = 0
        var unpriced = 0
        for response in report.responses {
            counts[response.source, default: 0] += 1
            let cost = table.cost(of: response)
            if let cost { costMicroUsd += cost } else { unpriced += response.requestCount }
            var line = JSONLine()
            line.add("key", response.key)
            line.add("requestCount", response.requestCount)
            line.add("source", response.source.rawValue)
            line.add("agentId", response.agentId)
            line.add("model", response.model)
            line.add("startMs", response.startMs)
            line.add("endMs", Int64?.some(response.endMs))
            line.add("firstMs", response.firstMs)
            line.add("input", response.usage.input)
            line.add("cachedInput", response.usage.cachedInput)
            line.add("cacheWriteInput", response.usage.cacheWriteInput)
            line.add("cacheWrite1hInput", response.usage.cacheWrite1hInput)
            line.add("output", response.usage.output)
            line.add("reasoning", response.usage.reasoning)
            line.add("final", response.isFinal)
            line.add("provider", response.provider)
            line.add("fast", response.fast)
            if !response.modes.facts.isEmpty, let data = try? JSONEncoder().encode(response.modes), let json = String(data: data, encoding: .utf8) {
                line.add("modes", raw: json)
            }
            line.add("costMicroUsd", cost)
            let view = response.modes.effortAndSpeed
            line.add("effort", raw: attribution(view.effort))
            line.add("speed", raw: attribution(view.speed))
            output += line.text + "\n"
        }

        for row in report.perfRows { output += row + "\n" }

        var countsLine = JSONLine()
        for source in SourceKind.allCases { countsLine.add(source.rawValue, counts[source] ?? 0) }
        var today = JSONLine()
        today.add("requests", report.today.requests)
        today.add("input", report.today.input)
        today.add("cachedInput", report.today.cachedInput)
        today.add("cacheWriteInput", report.today.cacheWriteInput)
        today.add("output", report.today.output)
        today.add("reasoning", report.today.reasoning)
        var summary = JSONLine()
        summary.add("counts", raw: countsLine.text)
        summary.add("output", report.responses.reduce(0) { $0 + $1.usage.output })
        summary.add("costMicroUsd", Int64?.some(costMicroUsd))
        summary.add("unpricedRequests", unpriced)
        summary.add("input", report.responses.reduce(0) { $0 + $1.usage.input })
        summary.add("filesRead", report.seed.filesRead)
        summary.add("bytesRead", Int64?.some(report.seed.bytesRead))
        summary.add("seedMs", Int64?.some(Int64(report.seed.durationMs.rounded())))
        summary.add("unfinalized", report.unfinalized)
        summary.add("today", raw: today.text)
        summary.add("filesDiscovered", report.seed.filesDiscovered)
        summary.add("malformedLines", report.malformedLines)
        summary.add("unreadableFiles", report.unreadableFiles)
        summary.add("partialHistory", report.seed.partialHistory)
        summary.add("nowMs", Int64?.some(report.perfNowMs))
        summary.add("perfAgents", report.perfAgents)
        var wrapper = JSONLine()
        wrapper.add("summary", raw: summary.text)
        output += wrapper.text + "\n"
        FileHandle.standardOutput.write(Data(output.utf8))
    }

    /// `{mode, evidence[, provider]}`, or null when unknown, like `reference.py`.
    static func attribution(_ value: ExecutionModeAttribution?) -> String {
        guard let value else { return "null" }
        var object = JSONLine()
        object.add("mode", value.mode)
        object.add("evidence", value.evidence)
        if let provider = value.provider { object.add("provider", provider) }
        return object.text
    }
}
