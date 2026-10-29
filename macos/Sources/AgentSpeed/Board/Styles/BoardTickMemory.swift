import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// What the previous tick showed, to tell this tick's accents (DESIGN 2c Motion): digits that roll, values that
/// flash, ranks that roll, passes and new peaks. A tick laid out again (a board that comes back into view) is not
/// fresh and plays nothing.
struct BoardTickMemory {
    private var signature: Int?
    /// The tick being laid out is new.
    private(set) var fresh = false
    private(set) var number: String?
    private(set) var value: Double?
    private var values: [String: Double] = [:]
    private var ranks: [String: Int] = [:]
    /// The table readouts' texts (思考, 費用) the last fresh tick showed, for their rolls (性能 never rolls).
    private var metrics: [String: String] = [:]
    /// The rows' 性能 and the hero's on the last fresh tick (their crossfades and the hero's count-up).
    private var scores: [String: BoardRowScore] = [:]
    private(set) var heroScore: PerfSetReading?

    mutating func begin(_ tick: BoardTick) {
        var hasher = Hasher()
        hasher.combine(tick.valueText)
        hasher.combine(tick.value)
        hasher.combine(tick.history)
        hasher.combine(tick.rows.map(\.key))
        hasher.combine(tick.events.map { "\($0.kind.rawValue)\($0.id)\($0.from)\($0.to)" })
        hasher.combine(tick.newPeak)
        hasher.combine(tick.metrics.intelligence.shown)
        hasher.combine(tick.metrics.intelligence.form)
        hasher.combine(tick.metrics.intelligence.provisional)
        hasher.combine(tick.metrics.thinking)
        hasher.combine(tick.metrics.cost)
        let signature = hasher.finalize()
        fresh = self.signature != nil && signature != self.signature
        self.signature = signature
    }

    /// What a live metric showed on the last fresh tick (nil before the first).
    func previousMetric(_ key: String) -> String? { metrics[key] }

    /// The row's value change since the last fresh tick.
    func change(_ key: String, live: Double) -> BoardValueChange {
        fresh ? BoardValueChange(from: values[key], to: live) : .steady
    }

    /// The row's 性能 on the last fresh tick, while this one is fresh (nil: nothing to change from).
    func previousScore(_ key: String) -> BoardRowScore? {
        fresh ? scores[key] : nil
    }

    /// The hero's 性能 on the last fresh tick, while this one is fresh.
    var previousHeroScore: PerfSetReading? { fresh ? heroScore : nil }

    /// The group's previous rank when it moved.
    func rankChange(_ id: String, rank: Int) -> Int? {
        guard fresh, let old = ranks[id], old != rank else { return nil }
        return old
    }

    /// The pass of this tick that moved `id`, if any.
    func event(_ tick: BoardTick, for id: String) -> BoardRankEvent? {
        fresh ? tick.events.first { $0.id == id } : nil
    }

    mutating func end(_ tick: BoardTick, rows: [(key: String, live: Double)], scores: [String: BoardRowScore] = [:]) {
        guard fresh || number == nil else { return }
        self.scores = scores
        heroScore = tick.metrics.intelligence
        number = tick.valueText
        value = tick.value
        values = Dictionary(rows.map { ($0.key, $0.live) }, uniquingKeysWith: { a, _ in a })
        ranks = Dictionary(tick.groups.enumerated().map { ($1.id, $0 + 1) }, uniquingKeysWith: { a, _ in a })
        metrics = Self.metricTexts(tick.metrics)
    }

    /// The texts a tick's table readouts show, by readout key.
    static func metricTexts(_ metrics: BoardMetricsTick) -> [String: String] {
        ["think": metrics.thinking, "cost": metrics.cost.number]
    }
}
