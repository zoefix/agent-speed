import Foundation

/// Result of a one-shot read of today's transcripts.
public struct TodayScanReport: Sendable {
    public var todayStartMs: Int64
    /// Responses ending today, sorted by end time then key.
    public var responses: [ResponseRecord]
    public var today: TokenTotals
    public var todayBySource: [SourceKind: TokenTotals]
    /// Today's unpriced facts per source (METRICS 9.10 *Live*); empty without `pricing`.
    public var todayUnpricedFacts: [SourceKind: [UnpricedFact]]
    /// Today's responses whose final usage was never written.
    public var unfinalized: Int
    public var seed: SeedSummary
    public var malformedLines: Int
    public var unreadableFiles: Int
    /// The `perf` rows of the seeded agents with a sample response and the `perfSets` row (METRICS 6.9 *Dumps*), JSON
    /// lines; empty unless asked for.
    public var perfRows: [String] = []
    public var perfAgents = 0
    /// The instant the performance scores were evaluated at.
    public var perfNowMs: Int64 = 0
}

/// One-shot seeding without watching, used by diagnostics such as the probe.
public enum TranscriptScan {
    /// With `pricing`, the engine prices every response as the app does (METRICS 9.3, 9.7).
    public static func today(
        configuration: MonitorConfiguration = MonitorConfiguration(),
        now: Date = Date(),
        calendar: Calendar = .current,
        pricing: PriceBook? = nil,
        perf: Bool = false,
        perfNowMs: Int64? = nil
    ) -> TodayScanReport {
        let nowMs = Clock.milliseconds(now)
        let todayStartMs = Clock.milliseconds(calendar.startOfDay(for: now))
        let session = TranscriptSession(configuration: configuration, pricing: pricing)
        let seed = session.seed(todayStartMs: todayStartMs)
        let responses = session.responses
            .filter { $0.endMs >= todayStartMs }
            .sorted { $0.endMs != $1.endMs ? $0.endMs < $1.endMs : $0.key.utf16.lexicographicallyPrecedes($1.key.utf16) }
        var perfRows: [String] = []
        let evaluatedAt = perfNowMs ?? Clock.milliseconds(Date())
        if perf {
            // Before the engine's first tick prunes nothing it needs: the sample looks back 30 minutes.
            let (agents, parents, stored) = session.perfAgents()
            perfRows = PerfDump.todayRows(agents.map { agent in
                PerfDump.Agent(id: agent.id, source: agent.source, phase: agent.phase, lastLineMs: agent.lastLineMs,
                               parentId: parents[agent.id], actions: agent.actions)
            }, responses: stored, nowMs: evaluatedAt)
        }
        let output = session.advance(nowMs: nowMs, todayStartMs: todayStartMs, emitsEvents: false).output
        return TodayScanReport(
            todayStartMs: todayStartMs,
            responses: responses,
            today: output.today,
            todayBySource: output.todayBySource,
            todayUnpricedFacts: output.todayUnpricedFacts,
            unfinalized: output.unfinalizedToday,
            seed: seed,
            malformedLines: session.malformedLines,
            unreadableFiles: session.unreadableFileCount,
            perfRows: perfRows,
            perfAgents: max(0, perfRows.count - 1),
            perfNowMs: evaluatedAt
        )
    }
}

enum Clock {
    static func milliseconds(_ date: Date) -> Int64 {
        Int64((date.timeIntervalSince1970 * 1000).rounded(.down))
    }

    /// Milliseconds that keep counting while the Mac sleeps (live reporting's clock, METRICS 11.9).
    static func monotonicMs() -> Int64 {
        Int64(clock_gettime_nsec_np(CLOCK_MONOTONIC) / 1_000_000)
    }
}
