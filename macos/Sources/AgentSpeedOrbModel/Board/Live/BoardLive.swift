import AgentSpeedCore
import Foundation

/// What a board shows for one snapshot (DESIGN 2c Content; METRICS 6.8 over the board's sources). Formatting and the
/// title are the app's; everything here is a plain value.
public struct BoardLiveFrame: Equatable, Sendable {
    /// `live_A` in `metric` (0 while every source is unavailable).
    public var value: Double
    public var metric: SpeedMetric
    public var generating: Int
    public var tools: Int
    /// `live_A` once per second, oldest first, at most 60.
    public var history: [Double]
    /// Ranked groups, first place first, and the rows planned for them.
    public var groups: [BoardGroup]
    public var rows: [BoardPlannedRow]
    /// The passes of this tick (none when the snapshot was already seen).
    public var events: [BoardRankEvent]
    public var newPeak: Bool
    /// The hero number's relative rise when it punches (more than 8 % + 20 tok/s in one tick).
    public var punch: Double?
    /// Today's output of the board's sources (`boards.today`).
    public var todayOutput: Int
    /// `peakToday_A` in `metric` (output-based for a subset; see `BoardLive`).
    public var peak: Double
    public var state: SubsetState
    /// Members that are disabled or not detected, in source order.
    public var unavailable: [SourceKind]
    /// The METRICS 6.9 sums over the board's sources (the hero's 性能 and 思考).
    public var intelligence: IntelligenceSums

    public init(value: Double = 0, metric: SpeedMetric = .output, generating: Int = 0, tools: Int = 0,
                history: [Double] = [], groups: [BoardGroup] = [], rows: [BoardPlannedRow] = [],
                events: [BoardRankEvent] = [], newPeak: Bool = false, punch: Double? = nil, todayOutput: Int = 0,
                peak: Double = 0, state: SubsetState = .available, unavailable: [SourceKind] = [],
                intelligence: IntelligenceSums = .zero) {
        self.value = value
        self.metric = metric
        self.generating = generating
        self.tools = tools
        self.history = history
        self.groups = groups
        self.rows = rows
        self.events = events
        self.newPeak = newPeak
        self.punch = punch
        self.todayOutput = todayOutput
        self.peak = peak
        self.state = state
        self.unavailable = unavailable
        self.intelligence = intelligence
    }
}

/// One board's engine-side state across ticks (DESIGN 2c Content and Ranking stability; METRICS 6.8): the stable
/// ranking, the 60 s history, `peakToday_A` with `newPeak_A`, and the punch of the hero number.
///
/// A board tracking all sources reads the snapshot's own values and celebrates the engine's `newPeak`; a subset board
/// runs `SubsetPeakTracker`. Either stays quiet for 60 s after it is created or its sources change. `peakToday_A` is
/// defined on output; with the total metric a subset board shows the highest total `live_A` it observed today.
public struct BoardLive: Sendable {
    public static let quietPeriodMs = SubsetPeakTracker.quietPeriodMs
    public static let punchShare = 0.08
    public static let punchMinimum = 20.0

    public private(set) var sources: OrbSourceSelection
    public private(set) var frame = BoardLiveFrame()
    private var ranking = BoardRanking()
    private var history = BoardHistory()
    private var tracker = SubsetPeakTracker()
    private var quietUntilMs: Int64?
    private var armed = false
    private var lastTickMs: Int64?
    private var lastValue: Double?
    private var observedTotal = (dayStartMs: Int64(0), value: 0.0)

    public init(sources: OrbSourceSelection) {
        self.sources = sources
    }

    /// A new source set starts over: ranking, history and peak, with 60 s of quiet.
    public mutating func setSources(_ sources: OrbSourceSelection) {
        guard sources != self.sources else { return }
        self.sources = sources
        ranking.reset()
        history.reset()
        armed = false
        lastValue = nil
        observedTotal = (0, 0)
    }

    /// Feeds one snapshot. `engineNewPeak`: the engine emitted `newPeak` since the last call (all-sources boards).
    /// While the effects preview runs every board shows all sources. A snapshot seen before (a settings change)
    /// re-evaluates the values in the current order without advancing the ranking, history or peaks.
    @discardableResult
    public mutating func ingest(_ snapshot: SpeedSnapshot, metric: SpeedMetric, layout: BoardLayoutMetrics,
                                expanded: String? = nil, engineNewPeak: Bool = false,
                                previewing: Bool = false) -> BoardLiveFrame {
        let set = previewing ? nil : sources.sourceSet
        let reading = OrbSubset.reading(snapshot, sources: set)
        let available = reading.state == .available
        let value = available ? max(0, reading.live.value(for: metric)) : 0
        let nowMs = Int64((snapshot.timestamp.timeIntervalSince1970 * 1000).rounded())
        let fresh = lastTickMs != nowMs
        lastTickMs = nowMs
        if !armed {
            armed = true
            quietUntilMs = nowMs + Self.quietPeriodMs
            tracker.restart(nowMs: nowMs)
        }

        let groups = BoardGrouping.groups(reading.agents, metric: metric)
        var events: [BoardRankEvent] = []
        let order: [String]
        if fresh {
            let result = ranking.tick(BoardGrouping.rankEntries(groups))
            order = result.order
            events = result.events
        } else {
            order = ranking.order
        }
        var byID: [String: BoardGroup] = [:]
        for group in groups where byID[group.id] == nil { byID[group.id] = group }
        let known = Set(order)
        let ranked = order.compactMap { byID[$0] } + groups.filter { !known.contains($0.id) }
        let plan = ranked.map { group in
            BoardPlanGroup(id: group.id, live: group.live,
                           subs: group.subs.map { BoardPlanSub(id: $0.id, live: max(0, $0.live.value(for: metric))) })
        }
        let rows = BoardLayoutPlanner.plan(plan, metrics: layout, expanded: expanded.flatMap { byID[$0] == nil ? nil : $0 })

        var newPeak = false
        var punch: Double?
        let running = snapshot.state == .running
        let quiet = quietUntilMs.map { nowMs < $0 } ?? false
        if fresh {
            history.record(value, atMs: nowMs)
            if let lastValue, lastValue > 0, value > lastValue * (1 + Self.punchShare) + Self.punchMinimum {
                punch = (value - lastValue) / max(1, lastValue)
            }
            lastValue = value
            if set == nil {
                newPeak = engineNewPeak && running && !quiet
            } else if running {
                let dayStartMs = Int64((snapshot.dayStart.timeIntervalSince1970 * 1000).rounded())
                newPeak = tracker.update(liveOutput: reading.live.output, measuredPeakOutput: reading.measuredPeakOutput,
                                         dayStartMs: dayStartMs, nowMs: nowMs)
                if observedTotal.dayStartMs != dayStartMs { observedTotal = (dayStartMs, 0) }
                observedTotal.value = max(observedTotal.value, available ? reading.live.total : 0)
            }
        }
        let peak: Double = if set == nil {
            snapshot.peakToday.value(for: metric)
        } else {
            metric == .output ? tracker.peakTodayOutput : observedTotal.value
        }

        let agents = reading.agents
        frame = BoardLiveFrame(
            value: value,
            metric: metric,
            generating: agents.reduce(0) { $0 + ($1.phase == .generating ? 1 : 0) },
            tools: agents.reduce(0) { $0 + ($1.phase == .tools ? 1 : 0) },
            history: history.values,
            groups: ranked,
            rows: rows,
            events: events,
            newPeak: newPeak,
            punch: punch,
            todayOutput: available ? reading.today.output : 0,
            peak: peak,
            state: reading.state,
            unavailable: reading.unavailable,
            intelligence: available ? reading.intelligence : .zero
        )
        return frame
    }

    /// Plans the rows again for a new expansion without a new tick.
    public mutating func replan(layout: BoardLayoutMetrics, expanded: String?) -> BoardLiveFrame {
        let metric = frame.metric
        let plan = frame.groups.map { group in
            BoardPlanGroup(id: group.id, live: group.live,
                           subs: group.subs.map { BoardPlanSub(id: $0.id, live: max(0, $0.live.value(for: metric))) })
        }
        let present = expanded.flatMap { id in frame.groups.contains { $0.id == id } ? id : nil }
        frame.rows = BoardLayoutPlanner.plan(plan, metrics: layout, expanded: present)
        frame.events = []
        frame.newPeak = false
        frame.punch = nil
        return frame
    }
}
