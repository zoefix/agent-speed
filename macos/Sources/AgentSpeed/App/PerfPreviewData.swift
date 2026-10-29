import AgentSpeedCore
import Foundation

/// The prototype's agents (`spec/prototypes/perf-score`) as monitor output: each one's measures run through the real
/// 6.9 formula, so previews, cards and the History section show the numbers the prototype shows.
@MainActor
enum PerfPreviewData {
    struct Spec {
        var id: String
        var source: SourceKind
        var project: String
        var model: String
        var phase: AgentPhase
        var live: Double
        var n: Int
        var tools: Int
        var baseline: Double?
        var measures: [PerfComponentId: Double]
        var effort: String?
        var parent: String?
        var signals = WorkSignals.none
    }

    static let specs: [Spec] = [
        Spec(id: "nova", source: .claudeCode, project: "nova", model: "claude-opus-5-5", phase: .generating, live: 162, n: 20,
             tools: 42, baseline: 1123, measures: [.T: 77, .L: 5400, .N: 3.5, .U: 292, .S: 0.97, .H: 0.99], effort: "xhigh",
             signals: WorkSignals(looping: nil, repeating: nil, novelty: 0.78, failed: 2, recovered: 2)),
        Spec(id: "nova-sub", source: .claudeCode, project: "nova", model: "claude-sonnet-5-5", phase: .generating, live: 48,
             n: 7, tools: 9, baseline: 1290, measures: [.T: 58, .L: 6100, .N: 2.4, .U: 520, .S: 0.89, .H: 0.97],
             effort: "medium", parent: "nova"),
        Spec(id: "vega", source: .pi, project: "vega", model: "gpt-6.1-mini", phase: .generating, live: 41, n: 14, tools: 26,
             baseline: nil, measures: [.T: 40, .N: 4.2, .U: 180, .S: 1.0], effort: "high",
             signals: WorkSignals(looping: nil, repeating: nil, novelty: 0.66, failed: 0, recovered: 0)),
        Spec(id: "rocket", source: .codex, project: "rocket", model: "gpt-6.1", phase: .tools, live: 0, n: 20, tools: 37,
             baseline: 893, measures: [.T: 30, .L: 7400, .R: 20, .N: 2.9, .U: 442, .S: 0.96, .H: 0.995], effort: "xhigh",
             signals: WorkSignals(looping: 4, repeating: nil, novelty: 0.46, failed: 5, recovered: 1)),
        Spec(id: "orbit", source: .claudeCode, project: "orbit", model: "claude-opus-5-5", phase: .idle, live: 0, n: 20,
             tools: 51, baseline: 1452, measures: [.T: 92, .L: 3600, .N: 5.1, .U: 160, .S: 0.98, .H: 0.99], effort: "high",
             signals: WorkSignals(looping: nil, repeating: nil, novelty: 0.81, failed: 1, recovered: 1)),
        Spec(id: "lumen", source: .gemini, project: "lumen", model: "gemini-3-pro", phase: .generating, live: 22, n: 2,
             tools: 0, baseline: nil, measures: [:]),
        Spec(id: "quasar", source: .opencode, project: "quasar", model: "qwen4-coder-flash", phase: .generating, live: 640,
             n: 20, tools: 0, baseline: 11880, measures: [.T: 642]),
    ]

    /// The 6.9 evaluation of measures given straight (absent ids are absent, for the reason the source implies).
    static func evaluation(_ spec: Spec) -> PerfEvaluation {
        var measures: [PerfComponentId: (measure: Double?, samples: Int)] = [:]
        for id in PerfComponentId.allCases {
            let samples = [.N, .U, .S].contains(id) ? spec.tools : spec.n
            measures[id] = (spec.n >= PerfFormula.minimumSamples ? spec.measures[id] : nil, samples)
        }
        let (components, score) = PerfFormula.combine(source: spec.source, measures: measures)
        return PerfEvaluation(score: spec.n >= PerfFormula.minimumSamples ? score : nil, n: spec.n, tools: spec.tools,
                              activeMs: 600_000, spanStartMs: nil, model: spec.model, components: components,
                              baseline: spec.baseline)
    }

    /// An evaluation scoring exactly `score`: every component the source records at `score / 1000` of its reference,
    /// throughput (uncapped) taking up what the clamps leave.
    static func evaluation(score: Double, source: SourceKind, model: String, n: Int, tools: Int,
                           baseline: Double? = nil) -> PerfEvaluation {
        let level = max(score, 1) / 1000
        let references: [PerfComponentId: Double] = [.T: 50 * level, .L: 5000 / level, .N: 3 * level, .R: 20 * level,
                                                      .H: min(1, 0.95 * level), .U: 400 / level, .S: min(1, 0.95 * level)]
        var measures: [PerfComponentId: (measure: Double?, samples: Int)] = [:]
        for id in PerfComponentId.allCases {
            let samples = [.N, .U, .S].contains(id) ? tools : n
            let present = !PerfFormula.notRecorded(id, source: source) && samples >= PerfFormula.minimumSamples
            measures[id] = (present ? references[id] : nil, samples)
        }
        var (components, _) = PerfFormula.combine(source: source, measures: measures)
        let others = components.filter { $0.id != .T && $0.isPresent }
        let weights = components.filter(\.isPresent).reduce(0) { $0 + $1.id.weight }
        let rest = others.reduce(0) { $0 + $1.id.weight * log($1.ratio ?? 1) }
        let ratioT = max(0.01, exp((log(level) * weights - rest) / PerfComponentId.T.weight))
        measures[.T] = (50 * ratioT, n)
        let combined = PerfFormula.combine(source: source, measures: measures)
        components = combined.components
        return PerfEvaluation(score: n >= PerfFormula.minimumSamples ? combined.score : nil, n: n, tools: tools,
                              activeMs: 600_000, spanStartMs: nil, model: model, components: components, baseline: baseline)
    }

    static func agent(_ spec: Spec, now: Date) -> AgentStatus {
        let id = "/preview/\(spec.id).jsonl"
        let evaluation = evaluation(spec)
        var modes: [ExecutionModeFact] = []
        if let effort = spec.effort,
           let fact = ExecutionModeFact(kind: .effort, rawValue: effort, evidence: .recorded, provider: nil, scope: .turn) {
            modes.append(fact)
        }
        let share = spec.source.recordsReasoningApart ? 0.3 : 0
        let sums = PreviewData.intelligence(output: spec.live, score: evaluation.score, id: id, idle: spec.phase == .idle,
                                            provisional: evaluation.provisional, thinkingShare: share, source: spec.source)
        return AgentStatus(
            id: id, source: spec.source, project: spec.project, model: spec.model, phase: spec.phase,
            isSubagent: spec.parent != nil, live: Rate(output: spec.live, total: spec.live * 26),
            recentSpeed: spec.live > 0 ? spec.live : nil, lastActivity: now,
            parentId: spec.parent.map { "/preview/\($0).jsonl" }, modes: ExecutionModes(modes), intelligence: sums,
            perf: evaluation, signals: spec.signals)
    }

    /// The prototype's agents without the gallery-only one.
    static func agents(now: Date) -> [AgentStatus] {
        specs.filter { $0.id != "quasar" }.map { agent($0, now: now) }
    }

    static func spec(_ id: String) -> Spec { specs.first { $0.id == id } ?? specs[0] }

    /// The card gallery of the prototype: name and card.
    static func cards(_ l10n: Localizer) -> [(name: String, model: PerfCardModel)] {
        let now = Date()
        func card(_ id: String, topCount: Int = 1, subAgents: Int = 0) -> PerfCardModel {
            let spec = spec(id)
            var facts: [String] = []
            if let effort = spec.effort { facts.append(l10n("effort.tooltip", effort)) }
            return PerfCardModel(agent: agent(spec, now: now), facts: facts, subAgents: subAgents, topCount: topCount)
        }
        return [
            ("live", card("nova", subAgents: 1)),
            ("thinking", card("rocket")),
            ("absent", card("vega")),
            ("provisional", card("nova-sub")),
            ("resting", card("orbit")),
            ("hero", card("nova", topCount: 4)),
            ("veryfast", card("quasar")),
            ("none", card("lumen")),
        ]
    }

    // MARK: - History

    /// Thirty days of records for the History section, close to the prototype's rows: each `(source, model)` with its
    /// timed requests per day (a day below 20 is a gap in the trend) around its level.
    static func historyRecords(today: DayKey) -> [PerfDayRecord] {
        let rows: [(source: SourceKind, model: String, level: Double, n: (Int) -> Int)] = [
            (.claudeCode, "claude-sonnet-5-5", 1.18, { $0 % 2 == 0 ? 24 : 4 }),
            (.claudeCode, "claude-opus-5-5", 1.0, { [17, 18, 24].contains($0) ? 8 : 82 }),
            (.codex, "gpt-6.1-mini", 0.98, { $0 % 3 == 0 ? 21 : 0 }),
            (.pi, "gpt-6.1-mini", 0.95, { $0 == 29 ? 32 : 0 }),
            (.codex, "gpt-6.1", 0.72, { [5, 6, 12].contains($0) ? 9 : 54 }),
            (.gemini, "gemini-3-pro", 1.0, { $0 == 25 || $0 == 29 ? 6 : 0 }),
        ]
        var records: [PerfDayRecord] = []
        for row in rows {
            for slot in 0..<30 {
                let n = row.n(slot)
                guard n > 0 else { continue }
                let day = today.adding(days: slot - 29)
                let wave = 1 + 0.07 * sin(Double(slot) * 0.7) + 0.04 * cos(Double(slot) * 1.9)
                records.append(PerfDayRecord(key: PerfRecordKey(day: day.description, source: row.source, model: row.model),
                                             record: record(n: n, level: row.level * wave, source: row.source)))
            }
        }
        return records
    }

    /// A day's record whose pooled score sits near `level` × 1000.
    private static func record(n: Int, level: Double, source: SourceKind) -> PerfRecord {
        var record = PerfRecord()
        record.n = n
        record.durationMsSum = Int64(n) * 6000
        record.outputSum = Int64(Double(n) * 6 * 50 * level)
        if !PerfFormula.notRecorded(.L, source: source) {
            let latency = 5000 / level
            record.latencyBins[PerfLatency.bucket(Int64(latency))] = n
        }
        if !PerfFormula.notRecorded(.H, source: source), source != .pi {
            record.cacheN = n
            record.cacheInput = Int64(n) * 40_000
            record.cacheCached = Int64(Double(record.cacheInput) * 0.95 * min(1, level))
        }
        if !PerfFormula.notRecorded(.R, source: source) {
            record.reasonN = n
            record.reasonOutput = Int64(n) * 120
            record.reasonDurationMsSum = Int64(Double(n) * 6000 / level)
        }
        if !PerfFormula.notRecorded(.N, source: source) {
            let tools = n * 2
            record.toolN = tools
            record.toolLnSum = Double(tools) * log(400 / level)
            record.toolFailed = level < 0.8 ? tools / 25 : tools / 50
            record.activeMs = Int64(Double(tools) / (3 * level) * 60_000)
        }
        return record
    }
}
