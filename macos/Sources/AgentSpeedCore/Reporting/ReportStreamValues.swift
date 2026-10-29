import Foundation

/// The values of the changed rule (METRICS 11.9), rounded as a report sends them (METRICS 11.7): a tick whose values
/// differ from the last built report's is changed.
public struct ReportStreamValues: Equatable, Sendable {
    public let outputTokS: Double
    public let totalTokS: Double
    public let estimatedShare: Double
    public let generating: Int
    public let tools: Int
    public let active: Int
    /// The `perfScore` objects (or the retired `taskScore` objects) the report would carry while the extension is
    /// active (METRICS 11.10 *WebSocket*, *Legacy*).
    public let perfScore: ReportPerfScoreValues?

    public init(_ sample: ReportSample, perfScore: ReportPerfScoreValues? = nil) {
        self.perfScore = perfScore
        outputTokS = Self.tokS(sample.live.output)
        totalTokS = Self.tokS(sample.live.total)
        estimatedShare = ReportBody.rounded(min(max(sample.estimatedShare.isFinite ? sample.estimatedShare : 0, 0), 1), 3)
        let agents = sample.facts.agents.values
        generating = min(agents.reduce(0) { $0 + $1.generating }, ReportBody.maxCount)
        tools = min(agents.reduce(0) { $0 + $1.tools }, ReportBody.maxCount)
        active = min(agents.reduce(0) { $0 + $1.active }, ReportBody.maxCount)
    }

    /// The tick is changed against the last built report's values (METRICS 11.9): a live value differs, or, with the
    /// `perfScore` extension (or the legacy `taskScore`), the top-level object or an entry's object the report would
    /// carry differs from the last report's object for the same id (11.10 *WebSocket*). An entry that is no longer
    /// carried changes nothing.
    public func changed(since last: ReportStreamValues?) -> Bool {
        guard let last else { return true }
        if outputTokS != last.outputTokS || totalTokS != last.totalTokS || estimatedShare != last.estimatedShare
            || generating != last.generating || tools != last.tools || active != last.active { return true }
        guard let perfScore else { return false }
        guard let previous = last.perfScore, previous.legacy == perfScore.legacy else { return true }
        return perfScore.top != previous.top || perfScore.entries.contains { previous.entries[$0.key] != $0.value }
    }

    private static func tokS(_ value: Double) -> Double {
        ReportBody.rounded(min(max(value.isFinite ? value : 0, 0), ReportBody.maxTokS), 1)
    }
}
