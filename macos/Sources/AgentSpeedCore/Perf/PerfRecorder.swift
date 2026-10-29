import Foundation

/// The per-day records of the history analysis (METRICS 6.11 *Records*): agents' completions taken in the order they
/// complete, with the online union rule for `activeMs` (one `coveredUntil` per agent and model).
struct PerfRecorder {
    private(set) var records: [PerfRecordKey: PerfRecord] = [:]
    private var covered: [CoverKey: Int64] = [:]
    private var labeler: DayLabeler

    private struct CoverKey: Hashable {
        var agent: OrdinalKey
        var model: OrdinalKey
    }

    init(timeZone: TimeZone) {
        labeler = DayLabeler(timeZone: timeZone)
    }

    /// The local date of an instant; nil for an instant without one (METRICS 8), which is left out.
    private mutating func dayOf(_ ms: Int64) -> String? {
        DayLabeler.hasDay(ms) ? labeler.label(forMs: ms) : nil
    }

    /// A response that has become measured, with its final values.
    mutating func response(_ record: ResponseRecord, agentId: String, source: SourceKind) {
        guard record.isFinal, let timed = TimedResponse(record), let day = dayOf(record.endMs) else { return }
        let model = record.model ?? ""
        records[PerfRecordKey(day: day, source: source, model: model), default: PerfRecord()]
            .addResponse(timed, source: source)
        cover(agentId: agentId, source: source, model: model, start: timed.startMs, end: timed.endMs)
    }

    /// A tool call whose first result was read; calls over 15 minutes add nothing.
    mutating func call(_ completion: ToolCompletion, agentId: String, source: SourceKind) {
        let (duration, overflow) = completion.resultMs.subtractingReportingOverflow(completion.callMs)
        guard !overflow, duration >= 0, duration <= PerfEvaluation.toolMaxMs,
              let day = dayOf(completion.resultMs) else { return }
        let model = completion.model ?? ""
        records[PerfRecordKey(day: day, source: source, model: model), default: PerfRecord()]
            .addCall(durationMs: duration, failed: completion.failed)
        cover(agentId: agentId, source: source, model: model, start: completion.callMs, end: completion.resultMs)
    }

    /// The online union rule: `[s, e]` adds what lies past the agent's and model's `coveredUntil` to the day of `e`.
    private mutating func cover(agentId: String, source: SourceKind, model: String, start: Int64, end: Int64) {
        let key = CoverKey(agent: OrdinalKey(agentId), model: OrdinalKey(model))
        let previous = covered[key]
        let added = max(0, end - (previous.map { max(start, $0) } ?? start))
        covered[key] = previous.map { max($0, end) } ?? end
        guard added > 0, let day = dayOf(end) else { return }
        records[PerfRecordKey(day: day, source: source, model: model), default: PerfRecord()].activeMs += added
    }

    /// The records by day, source order, then model (UTF-8 bytes).
    var rows: [PerfDayRecord] {
        records.map { PerfDayRecord(key: $0.key, record: $0.value) }.sorted { PerfRecordKey.precedes($0.key, $1.key) }
    }
}

/// One agent's completions in the order they complete (METRICS 6.11 *Completion order*), kept while a file is read:
/// the keys of responses as they first become measured and the tool calls as their first result is read.
struct PerfCompletionOrder {
    enum Item {
        case response(String)
        case call(ToolCompletion)
    }

    private(set) var items: [Item] = []
    private var measured: Set<OrdinalKey> = []

    /// An upsert; the first one that makes the response measured is its completion.
    mutating func note(_ record: ResponseRecord) {
        guard record.isMeasured, measured.insert(OrdinalKey(record.key)).inserted else { return }
        items.append(.response(record.key))
    }

    mutating func note(_ completion: ToolCompletion) {
        items.append(.call(completion))
    }

    /// Feeds responses only, each under its own agent and source (a log of several agents, METRICS 4.15).
    func record(into recorder: inout PerfRecorder, latest: [String: ResponseRecord]) {
        for case .response(let key) in items {
            if let record = latest[key] { recorder.response(record, agentId: record.agentId, source: record.source) }
        }
    }

    /// Feeds the completions to `recorder`, each response with its final values from `latest`.
    func record(into recorder: inout PerfRecorder, latest: [String: ResponseRecord], agentId: String,
                source: SourceKind) {
        for item in items {
            switch item {
            case .response(let key):
                if let record = latest[key] { recorder.response(record, agentId: agentId, source: source) }
            case .call(let completion):
                recorder.call(completion, agentId: agentId, source: source)
            }
        }
    }
}
