import Foundation

/// One agent's performance score at an instant with every fact the score card shows (METRICS 6.9 *Sample*,
/// *Components*, *Facts for the card*).
public struct PerfEvaluation: Equatable, Sendable {
    public static let sampleSize = 20
    public static let windowMs: Int64 = 30 * 60_000
    /// A score from fewer timed requests than this is provisional.
    public static let provisionalBelow = 10
    static let toolMaxMs: Int64 = 15 * 60_000
    static let toolFloorMs: Int64 = 50

    /// Unrounded; nil when the sample has fewer than 3 responses.
    public var score: Double?
    /// The sample size (有效計時請求).
    public var n: Int
    /// `|C|`, the completed tool calls in the sample's span.
    public var tools: Int
    public var activeMs: Int64
    public var spanStartMs: Int64?
    /// The model of the newest sample response, as logged.
    public var model: String?
    /// T, L, N, R, H, U, S.
    public var components: [PerfComponent]
    /// The 14-day pooled score of the agent's source and model (METRICS 6.11 *Baseline*); nil without one.
    public var baseline: Double?

    public init(score: Double?, n: Int, tools: Int, activeMs: Int64, spanStartMs: Int64?, model: String?,
                components: [PerfComponent], baseline: Double? = nil) {
        self.score = score
        self.n = n
        self.tools = tools
        self.activeMs = activeMs
        self.spanStartMs = spanStartMs
        self.model = model
        self.components = components
        self.baseline = baseline
    }

    /// No sample: every component absent.
    public static func none(source: SourceKind) -> PerfEvaluation {
        PerfEvaluation(score: nil, n: 0, tools: 0, activeMs: 0, spanStartMs: nil, model: nil,
                       components: PerfFormula.combine(source: source, measures: [:]).components)
    }

    public var shown: Int? { score.map(perfRoundHalfAway) }

    public var provisional: Bool { score != nil && n < Self.provisionalBelow }

    /// `score / baseline − 1`; nil without a baseline.
    public var vsUsual: Double? {
        guard let score, let baseline else { return nil }
        return score / baseline - 1
    }

    /// `100 × vsUsual` rounded half away from zero.
    public var vsUsualPercent: Int? { vsUsual.map { perfRoundHalfAway(100 * $0) } }

    public func component(_ id: PerfComponentId) -> PerfComponent? { components.first { $0.id == id } }

    /// The agent's value in its form: live while not idle (after the staleness rule), resting while idle.
    public func value(agentId: String, lastLineMs: Int64?, idle: Bool) -> PerfSetValue {
        PerfSetValue(form: score == nil ? .none : (idle ? .resting : .live), score: score, agentId: agentId,
                     lastLineMs: lastLineMs, provisional: provisional)
    }

    // MARK: - Evaluation

    /// `responses`: the agent's responses (any; only measured ones in the window count). `actions`: its stored
    /// actions in file order.
    public static func evaluate(source: SourceKind, responses: some Sequence<ResponseRecord>, actions: [ToolAction],
                                nowMs: Int64) -> PerfEvaluation {
        let sample = sampleOf(responses, nowMs: nowMs)
        let n = sample.count
        let spanStart = sample.map(\.startMs).min()
        let calls = n >= PerfFormula.minimumSamples ? callsOf(actions, spanStart: spanStart ?? .max, nowMs: nowMs) : []
        var intervals = sample.map { ($0.startMs, $0.endMs) }
        intervals += calls.map { ($0.callMs, $0.resultMs ?? $0.callMs) }
        let active = unionLength(intervals)
        var measures: [PerfComponentId: (measure: Double?, samples: Int)] = [:]
        if n >= PerfFormula.minimumSamples {
            measures = componentMeasures(source: source, sample: sample, calls: calls, activeMs: active)
        } else {
            measures[.T] = (nil, n)
        }
        let (components, score) = PerfFormula.combine(source: source, measures: measures)
        return PerfEvaluation(score: score, n: n, tools: calls.count, activeMs: active, spanStartMs: spanStart,
                              model: sample.first?.record.model, components: components)
    }

    private static func componentMeasures(source: SourceKind, sample: [TimedResponse], calls: [ToolAction],
                                          activeMs: Int64) -> [PerfComponentId: (measure: Double?, samples: Int)] {
        let minimum = PerfFormula.minimumSamples
        var measures: [PerfComponentId: (measure: Double?, samples: Int)] = [:]
        let output = sample.reduce(0) { $0 + $1.record.usage.output }
        let duration = sample.reduce(Int64(0)) { $0 + $1.durationMs }
        measures[.T] = (Double(output) / (Double(duration) / 1000), sample.count)

        let latencies = sample.compactMap { PerfLatency.latency(startMs: $0.record.startMs, firstMs: $0.record.firstMs) }
        measures[.L] = (latencies.count >= minimum ? median(latencies) : nil, latencies.count)

        if source.recordsReasoningApart {
            let thought = sample.filter { min($0.record.usage.reasoning, $0.record.usage.output) > 0 }
            let reasoning = thought.reduce(0) { $0 + min($1.record.usage.reasoning, $1.record.usage.output) }
            let reasoningMs = thought.reduce(Int64(0)) { $0 + $1.durationMs }
            measures[.R] = (thought.count >= minimum ? Double(reasoning) / (Double(reasoningMs) / 1000) : nil, thought.count)
        } else {
            measures[.R] = (nil, 0)
        }

        if source.recordsCacheReads {
            let read = sample.filter { $0.record.usage.input > 0 }
            let input = read.reduce(0) { $0 + $1.record.usage.input }
            let cached = read.reduce(0) { $0 + $1.record.usage.cachedInput }
            measures[.H] = (read.count >= minimum ? Double(cached) / Double(input) : nil, read.count)
        } else {
            measures[.H] = (nil, 0)
        }

        let k = calls.count
        if PerfFormula.hasActions(source), k >= minimum {
            measures[.N] = (activeMs > 0 ? Double(k) / (Double(activeMs) / 60_000) : nil, k)
            var logs = 0.0
            for call in calls { logs = logs + Foundation.log(Double(max(call.resultMs! - call.callMs, toolFloorMs))) }
            measures[.U] = (Foundation.exp(logs / Double(k)), k)
            let failed = calls.reduce(0) { $0 + ($1.failed == true ? 1 : 0) }
            measures[.S] = (1 - Double(failed) / Double(k), k)
        } else {
            measures[.N] = (nil, k)
            measures[.U] = (nil, k)
            measures[.S] = (nil, k)
        }
        return measures
    }

    /// The measured responses with `nowMs − 30 min ≤ endMs ≤ nowMs`, the newest 20 by `endMs`, ties by key (UTF-16
    /// ordinal: the smaller key is newer).
    static func sampleOf(_ responses: some Sequence<ResponseRecord>, nowMs: Int64) -> [TimedResponse] {
        var chosen: [TimedResponse] = []
        for record in responses where record.isFinal && record.endMs <= nowMs && record.endMs >= nowMs - windowMs {
            if let timed = TimedResponse(record) { chosen.append(timed) }
        }
        chosen.sort { lhs, rhs in
            lhs.endMs != rhs.endMs ? lhs.endMs > rhs.endMs : AgentParents.ordinal(lhs.record.key, precedes: rhs.record.key)
        }
        return Array(chosen.prefix(sampleSize))
    }

    /// `C`: the completed actions of the stored list (the last 64) inside the sample's span, within 15 minutes.
    static func callsOf(_ actions: [ToolAction], spanStart: Int64, nowMs: Int64) -> [ToolAction] {
        actions.suffix(ActionLog.capacity).filter { action in
            guard let result = action.resultMs, action.callMs >= spanStart, result <= nowMs else { return false }
            let (duration, overflow) = result.subtractingReportingOverflow(action.callMs)
            return !overflow && duration >= 0 && duration <= toolMaxMs
        }
    }

    /// The length of the union of the intervals; empty ones add nothing.
    static func unionLength(_ intervals: [(Int64, Int64)]) -> Int64 {
        let sorted = intervals.sorted { $0.0 != $1.0 ? $0.0 < $1.0 : $0.1 < $1.1 }
        var total: Int64 = 0
        var current: (start: Int64, end: Int64)?
        for (start, end) in sorted where end > start {
            if let open = current, start <= open.end {
                current?.end = max(open.end, end)
            } else {
                if let open = current { total += open.end - open.start }
                current = (start, end)
            }
        }
        if let open = current { total += open.end - open.start }
        return total
    }

    private static func median(_ values: [Int64]) -> Double {
        let ordered = values.sorted()
        let mid = ordered.count / 2
        return ordered.count % 2 == 1 ? Double(ordered[mid]) : Double(ordered[mid - 1] + ordered[mid]) / 2
    }
}
