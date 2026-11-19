import Foundation

/// The report ledger of METRICS 11.4: which responses count for live reporting and what each one already added.
/// Increments accumulate in `counters`; windows are differences of it.
struct ReportLedger: Sendable {
    static let maxCountedKeys = 65_536

    /// The values a counted response has added so far, field by field.
    private struct Reported: Sendable {
        var usage: ReportUsage
        var traceRound: String?
        var endMs: Int64
        var modes: [ExecutionModeAttribution]
    }

    private var traceRounds: [String: (usage: ReportUsage, members: Int)] = [:]
    private var counted: [String: Reported] = [:]
    /// Insertion order for the FIFO bound; entries already removed are skipped.
    private var order: [String] = []
    private var head = 0
    private(set) var counters = ReportCounters()

    var countedKeys: Int { counted.count }

    func isCounted(_ key: String) -> Bool { counted[key] != nil }

    /// Counts a response at its first live emission. `currentEffort` is the agent's current effort at that moment, which
    /// a response without an effort of its own freezes (EXECUTION-MODES, Current values, Ledger).
    mutating func count(_ record: ResponseRecord, modelId: String, currentEffort: ExecutionModeAttribution? = nil) {
        guard counted[record.key] == nil else {
            grow(record, modelId: modelId)
            return
        }
        var values = Self.values(of: record)
        values.modes = Self.freezing(currentEffort, into: values.modes)
        let added = reportGrowth(values.usage, record: record, previous: nil)
        counters.add(added, to: ReportModelKey(modelId: modelId, source: record.source), modes: values.modes)
        counted[record.key] = values
        order.append(record.key)
        evictOverflow()
    }

    /// Growth of a counted response (an upsert or a re-pricing): each field adds what exceeds what it already added.
    mutating func grow(_ record: ResponseRecord, modelId: String) {
        guard var reported = counted[record.key] else { return }
        let values = Self.values(of: record)
        let growth = values.usage - reported.usage
        let added = reportGrowth(growth, record: record, previous: reported)
        reported.usage += growth
        reported.traceRound = reported.traceRound ?? record.traceRoundKey
        reported.endMs = max(reported.endMs, record.endMs)
        counted[record.key] = reported
        counters.add(added, to: ReportModelKey(modelId: modelId, source: record.source), modes: reported.modes)
    }

    /// A native round and the sum of its diagnostic attempts describe the same usage (METRICS 4.21).
    private mutating func reportGrowth(_ growth: ReportUsage, record: ResponseRecord, previous: Reported?) -> ReportUsage {
        guard record.source == .openbitfun else { return growth }
        let old = previous?.usage ?? .zero
        if let round = previous?.traceRound ?? record.traceRoundKey {
            var traces = traceRounds[round] ?? (.zero, 0)
            if previous?.traceRound == nil {
                traces.usage += old  // A legacy trace may first acquire a round when its turn is written.
                traces.members += 1
            }
            let covered = (counted[round]?.usage ?? .zero) - traces.usage
            traces.usage += growth
            traceRounds[round] = traces
            return growth - covered
        }
        return traceRounds[record.key].map { growth - ($0.usage - old) } ?? growth
    }

    private mutating func forget(_ key: String) {
        if let round = counted.removeValue(forKey: key)?.traceRound {
            traceRounds[round]?.members -= 1
            if traceRounds[round]?.members == 0 && counted[round] == nil { traceRounds[round] = nil }
        }
        if traceRounds[key]?.members == 0 { traceRounds[key] = nil }
    }

    /// Forgets counted keys whose response left the engine's retention (by its latest end).
    mutating func prune(before cutoffMs: Int64) {
        guard counted.values.contains(where: { $0.endMs < cutoffMs }) else { return }
        for (key, value) in counted where value.endMs < cutoffMs { forget(key) }
        order = order[head...].filter { counted[$0] != nil }
        head = 0
    }

    private mutating func evictOverflow() {
        while counted.count > Self.maxCountedKeys, head < order.count {
            forget(order[head])
            head += 1
        }
        if head > 4096, head * 2 > order.count {
            order.removeFirst(head)
            head = 0
        }
    }

    /// The effort the ledger freezes: the response's own when known, else a current value that is `selected` or better,
    /// without its provider. A configured value is never reported.
    static func frozenEffort(response: ExecutionModeAttribution, current: ExecutionModeAttribution?) -> ExecutionModeAttribution {
        guard response.isUnknown, let current, !current.isUnknown,
              let evidence = ExecutionModeEvidence(rawValue: current.evidence),
              ExecutionModeEvidence.attributable.contains(evidence) else { return response }
        return ExecutionModeAttribution(kind: .effort, mode: current.mode, evidence: current.evidence)
    }

    private static func freezing(_ current: ExecutionModeAttribution?, into modes: [ExecutionModeAttribution]) -> [ExecutionModeAttribution] {
        guard !modes.contains(where: { $0.kind == .effort }) else { return modes }
        let frozen = frozenEffort(response: ExecutionModeAttribution(kind: .effort), current: current)
        return frozen.isUnknown ? modes : modes + [frozen]
    }

    private static func values(of record: ResponseRecord) -> Reported {
        let usage = record.usage
        let measured = record.isFinal ? TimedResponse(record) : nil
        let totals = ReportUsage(
            requests: Int64(record.requestCount), output: Int64(usage.output), input: Int64(usage.input),
            cachedInput: Int64(usage.cachedInput), cacheWriteInput: Int64(usage.cacheWriteInput), reasoning: Int64(usage.reasoning),
            measuredOutput: measured == nil ? 0 : Int64(usage.output), measuredMs: measured?.durationMs ?? 0,
            costMicroUsd: record.costMicroUsd ?? 0, unpricedRequests: record.costMicroUsd == nil ? Int64(record.requestCount) : 0
        )
        return Reported(usage: totals, traceRound: record.traceRoundKey, endMs: record.endMs, modes: record.modes.reportedAttributions)
    }
}

extension PriceTable {
    /// The id a response's model is reported as (METRICS 11.3): a hit on a table entry (priced or free) is that
    /// entry's id, a local runtime (`freeProviders`) is `local`, everything else is `unknown`. Raw names never leave.
    public func reportModelId(model: String?, provider: String?) -> String {
        let (resolution, key) = resolver.resolveWithKey(model: model, provider: provider)
        let id: String?
        switch resolution {
        case .priced(let index, _):
            id = models[index].id
        case .free:
            if let key {
                id = resolver.entryIndex(forKey: key).map { models[$0].id }
            } else {
                id = resolver.isLocalProvider(model: model, provider: provider) ? ReportModelIds.local : nil
            }
        case .unknown:
            id = nil
        }
        guard let id, ReportModelIds.isValid(id) else { return ReportModelIds.unknown }
        return id
    }
}

/// The reserved model ids of REPORTING 6.6.4.
public enum ReportModelIds {
    public static let local = "local"
    public static let unknown = "unknown"
    public static let other = "other"

    /// `$defs/modelId`: `^[a-z0-9][a-z0-9._:-]{0,63}$`.
    public static func isValid(_ id: String) -> Bool {
        let bytes = Array(id.utf8)
        guard (1...64).contains(bytes.count) else { return false }
        func lowerAlnum(_ byte: UInt8) -> Bool {
            (UInt8(ascii: "a")...UInt8(ascii: "z")).contains(byte) || (UInt8(ascii: "0")...UInt8(ascii: "9")).contains(byte)
        }
        guard lowerAlnum(bytes[0]) else { return false }
        return bytes.dropFirst().allSatisfy { lowerAlnum($0) || $0 == UInt8(ascii: ".") || $0 == UInt8(ascii: "_")
            || $0 == UInt8(ascii: ":") || $0 == UInt8(ascii: "-") }
    }
}

extension PriceBook {
    /// `cost.priceVersion` (METRICS 11.2): the table bound to the local date of `ms`, else the current one. Reading it
    /// never binds a date.
    public func reportPriceVersion(atMs ms: Int64) -> Int {
        var labeler = DayLabeler(timeZone: .current)
        return boundVersion(forDay: labeler.label(forMs: ms)) ?? currentVersion
    }
}
