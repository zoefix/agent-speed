import Foundation

/// When a report carries the performance score, or the retired task score (METRICS 11.10 *Active*, *Legacy*).
public enum ReportPerfScoreRule {
    /// A completion older than this when it is read is history (METRICS 11.4, 11.10 *Counting*).
    public static let firstSeenMs: Int64 = 120_000

    /// `models` is sent by consent: the document lists it with `maxModels` ≥ 1 and the user's switch is on.
    static func sharesModels(_ document: ReportDiscovery, switches: Set<ReportSection>) -> Bool {
        document.sections.contains(.models) && document.limits.maxModels >= 1 && switches.contains(.models)
    }

    /// The document advertises `perfScore: 1` and the report carries `models` by consent.
    public static func isActive(_ document: ReportDiscovery, switches: Set<ReportSection>) -> Bool {
        document.extensions?.perfScore == true && sharesModels(document, switches: switches)
    }

    /// Only the retired `taskScore: 1` is advertised: the report carries the thinking speed in its shape.
    public static func isLegacyActive(_ document: ReportDiscovery, switches: Set<ReportSection>) -> Bool {
        document.extensions?.taskScore == true && document.extensions?.perfScore != true
            && sharesModels(document, switches: switches)
    }

    /// A tool call counts when its first result is read live, at most 2 minutes old, within 15 minutes of its call.
    public static func countsCall(_ completion: ToolCompletion, nowMs: Int64) -> Bool {
        guard !completion.seeded else { return false }
        let (age, ageOverflow) = nowMs.subtractingReportingOverflow(completion.resultMs)
        guard ageOverflow ? nowMs < completion.resultMs : age <= firstSeenMs else { return false }
        let (duration, overflow) = completion.resultMs.subtractingReportingOverflow(completion.callMs)
        return !overflow && duration >= 0 && duration <= PerfEvaluation.toolMaxMs
    }
}

/// A `perfScore` object as sent: the score rounded half away from zero (at most 10 000 000), null exactly when the
/// form is none, and the thinking speed rounded to 0.1.
public struct ReportPerfScoreValue: Equatable, Sendable {
    public static let maxScore = 10_000_000

    public var score: Int?
    public var form: PerfValueForm
    public var thinkingTokS: Double

    public init(_ value: PerfSetValue, thinking: Double) {
        let shown = value.form == .none ? nil : value.score.map { min(max(perfRoundHalfAway($0), 0), Self.maxScore) }
        form = shown == nil ? .none : value.form
        score = shown
        thinkingTokS = ReportBody.rounded(min(max(thinking.isFinite ? thinking : 0, 0), ReportBody.maxTokS), 1)
    }

    /// The retired `taskScore` object: no score, the thinking speed only.
    public static func legacy(thinking: Double) -> ReportPerfScoreValue {
        ReportPerfScoreValue(.empty, thinking: thinking)
    }

    var json: ReportJSON {
        .object(["score": score.map { .int($0) } ?? .null, "form": .string(form.rawValue),
                 "thinkingTokS": .decimal(thinkingTokS, decimals: 1)])
    }
}

/// The score objects a report would carry, rounded as sent (the changed rule of METRICS 11.10 *WebSocket*).
public struct ReportPerfScoreValues: Equatable, Sendable {
    public var top: ReportPerfScoreValue
    /// By entry id, `other` included.
    public var entries: [String: ReportPerfScoreValue]
    /// The retired `taskScore` objects rather than `perfScore`.
    public var legacy: Bool

    public init(top: ReportPerfScoreValue, entries: [String: ReportPerfScoreValue], legacy: Bool = false) {
        self.top = top
        self.entries = entries
        self.legacy = legacy
    }
}

/// The live values of one resolved model id (METRICS 11.10 *Body*, *Membership*).
public struct ReportPerfModel: Equatable, Sendable {
    /// The set value of the agents whose current model resolves to this id.
    public var value: PerfSetValue
    /// `liveThinking` of the model sums of every raw model that resolves to this id (tok/s, unrounded).
    public var thinking: Double
    /// The sources of the agents live with this id.
    public var liveSources: Set<SourceKind>

    public init(value: PerfSetValue = .empty, thinking: Double = 0, liveSources: Set<SourceKind> = []) {
        self.value = value
        self.thinking = thinking
        self.liveSources = liveSources
    }
}

/// What each snapshot carries for the `perfScore` extension: the value and thinking speed of all enabled sources,
/// those of each resolved model id, and the engine's recent counted completions.
public struct ReportPerfFacts: Equatable, Sendable {
    /// The highest score of every agent of all enabled sources (6.9 *Sets and groups*).
    public var value: PerfSetValue
    /// `liveThinking` of all enabled sources (tok/s, unrounded).
    public var thinking: Double
    public var models: [String: ReportPerfModel]
    public var completions: ReportPerfCompletionLog

    public init(value: PerfSetValue = .empty, thinking: Double = 0, models: [String: ReportPerfModel] = [:],
                completions: ReportPerfCompletionLog = ReportPerfCompletionLog(id: 0)) {
        self.value = value
        self.thinking = thinking
        self.models = models
        self.completions = completions
    }

    public static let empty = ReportPerfFacts()
}
