import AgentSpeedCore
import Foundation

/// `--dump-history`: one JSON object per (day, source), then one `perf` row per performance record (METRICS 6.11), in
/// the format of `reference.py --dump-history`.
/// Timing goes to standard error so standard output can be diffed.
enum DumpHistory {
    static func run(configuration: MonitorConfiguration, utc: Bool, storePath: String?, table: PriceTable) {
        let report: HistoryScanReport
        do {
            report = try HistoryScan.run(
                configuration: configuration,
                timeZone: utc ? TimeZone(identifier: "UTC")! : .current,
                storePath: storePath,
                pricing: PriceBook(tables: [table])
            )
        } catch {
            FileHandle.standardError.write(Data("history analysis failed: \(error)\n".utf8))
            exit(1)
        }
        var output = ""
        for day in report.days {
            var line = JSONLine()
            line.add("date", day.day)
            line.add("source", day.source.rawValue)
            line.add("requests", day.totals.requests)
            line.add("input", day.totals.input)
            line.add("cachedInput", day.totals.cachedInput)
            line.add("cacheWriteInput", day.totals.cacheWriteInput)
            line.add("output", day.totals.output)
            line.add("reasoning", day.totals.reasoning)
            line.add("measuredOutput", day.measuredOutput)
            line.add("measuredMs", Int64?.some(day.measuredMs))
            line.add("unfinalized", day.unfinalized)
            line.add("costMicroUsd", day.cost?.costMicroUsd)
            line.add("unpricedRequests", day.cost.map { Int64($0.unpricedRequests) })
            line.add("priceVersion", day.priceVersion.map(Int64.init))
            line.add("agents", day.agents)
            var models = JSONLine()
            for model in day.models {
                var tally = JSONLine()
                tally.add("requests", model.requests)
                tally.add("output", model.output)
                tally.add("measuredOutput", model.measuredOutput)
                tally.add("measuredMs", Int64?.some(model.measuredMs))
                tally.add("costMicroUsd", model.cost?.costMicroUsd)
                tally.add("unpricedRequests", model.cost.map { Int64($0.unpricedRequests) })
                models.add(model.model, raw: tally.text)
            }
            line.add("models", raw: models.text)
            line.add("unpriced", raw: JSONLine.array(day.unpriced.facts.map { fact in
                var object = JSONLine()
                object.add("model", fact.model)
                object.add("provider", fact.provider)
                object.add("fast", fact.fast)
                object.add("requests", fact.requests)
                return object.text
            }))
            output += line.text + "\n"
        }
        for row in PerfDump.historyRows(report.perf) { output += row + "\n" }
        FileHandle.standardOutput.write(Data(output.utf8))
        let summary = "history: \(report.days.count) rows, \(report.filesDiscovered) files discovered, "
            + "\(report.filesAnalyzed) files (\(report.bytesAnalyzed) bytes) and \(report.sessionsAnalyzed) database sessions "
            + "analysed in \(Int(report.durationMs.rounded())) ms\n"
        FileHandle.standardError.write(Data(summary.utf8))
    }
}
