import Foundation

/// The seven components of the performance score (METRICS 6.9 *Components*), in the order the sums run.
public enum PerfComponentId: String, CaseIterable, Sendable {
    case T, L, N, R, H, U, S

    /// The weight `w` of the weighted geometric mean.
    public var weight: Double {
        switch self {
        case .T: 0.30
        case .L: 0.20
        case .N: 0.15
        case .R, .H, .U: 0.10
        case .S: 0.05
        }
    }

    /// The ratio `r` of a measure, clamped: throughput has a floor and no ceiling, the others stay in [0.05, 20].
    func ratio(_ measure: Double) -> Double {
        let raw: Double = switch self {
        case .T: measure / 50
        case .L: 5000 / max(measure, 250)
        case .N: measure / 3
        case .R: measure / 20
        case .H: measure / 0.95
        case .U: 400 / measure
        case .S: measure / 0.95
        }
        return self == .T ? max(PerfFormula.throughputFloor, raw) : min(PerfFormula.high, max(PerfFormula.low, raw))
    }
}

/// Why a component is left out of the score.
public enum PerfAbsence: String, Sendable {
    /// The source never records what the component measures.
    case notRecorded
    /// Too few values in the sample.
    case fewSamples
}

/// One component as the score card shows it: its measure, ratio and sub-score when present, else the reason.
public struct PerfComponent: Equatable, Sendable {
    public var id: PerfComponentId
    public var samples: Int
    public var measure: Double?
    public var ratio: Double?
    public var reason: PerfAbsence?

    public init(id: PerfComponentId, samples: Int, measure: Double?, ratio: Double?, reason: PerfAbsence?) {
        self.id = id
        self.samples = samples
        self.measure = measure
        self.ratio = ratio
        self.reason = reason
    }

    public var isPresent: Bool { ratio != nil }

    /// `1000 × r`: the score this component alone would give.
    public var subScore: Double? { ratio.map { 1000 * $0 } }
}

/// The shared formula of METRICS 6.9: presence, clamps and the weighted geometric mean.
public enum PerfFormula {
    static let throughputFloor = 0.01
    static let low = 0.05
    static let high = 20.0
    /// Fewer values than this leave a component out (and fewer sample responses leave the score out).
    public static let minimumSamples = 3

    /// Sources whose logs say when the first content was written (METRICS 4.1, 4.2).
    static let latencySources: Set<SourceKind> = [.codex, .claudeCode, .claudeDesktop]
    /// Sources with tool calls (METRICS 6.10).
    static let actionSources: Set<SourceKind> = [.codex, .claudeCode, .claudeDesktop, .omp, .pi, .openclaw, .droid]

    /// The source never records what the component measures (6.9 *Absent components*).
    public static func notRecorded(_ id: PerfComponentId, source: SourceKind) -> Bool {
        switch id {
        case .L: !latencySources.contains(source)
        case .N, .U, .S: !actionSources.contains(source)
        case .R: !source.recordsReasoningApart
        case .H: !source.recordsCacheReads
        case .T: false
        }
    }

    /// Sources with tool calls take the tool components from their actions.
    static func hasActions(_ source: SourceKind) -> Bool { actionSources.contains(source) }

    /// The components and the unrounded score from each component's measure (nil when absent) and samples.
    public static func combine(source: SourceKind, measures: [PerfComponentId: (measure: Double?, samples: Int)])
        -> (components: [PerfComponent], score: Double?) {
        var components: [PerfComponent] = []
        components.reserveCapacity(PerfComponentId.allCases.count)
        var logSum = 0.0
        var weights = 0.0
        for id in PerfComponentId.allCases {
            let (measure, samples) = measures[id] ?? (nil, 0)
            guard let measure else {
                let reason: PerfAbsence = notRecorded(id, source: source) ? .notRecorded : .fewSamples
                components.append(PerfComponent(id: id, samples: samples, measure: nil, ratio: nil, reason: reason))
                continue
            }
            let ratio = id.ratio(measure)
            components.append(PerfComponent(id: id, samples: samples, measure: measure, ratio: ratio, reason: nil))
            logSum = logSum + id.weight * Foundation.log(ratio)
            weights = weights + id.weight
        }
        let score = weights > 0 ? 1000 * Foundation.exp(logSum / weights) : nil
        return (components, score)
    }
}

extension SourceKind {
    /// Hermes Agent's log states a cache read only when the provider reported one (METRICS 4.15): its cache hit rate is
    /// not recorded.
    public var recordsCacheReads: Bool { self != .hermes }
}
