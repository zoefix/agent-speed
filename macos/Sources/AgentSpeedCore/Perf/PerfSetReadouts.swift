import Foundation

/// A model the agents of an orb's set run (METRICS 6.9 *Readouts of a set*): one per model id, however many tasks
/// run it.
public struct RunningModel: Equatable, Sendable {
    /// The model id as logged.
    public var model: String
    /// The highest `recentSpeed` (6.2, the agents' own responses, no fallback) of its agents, in output tok/s; nil
    /// while none of them has a timed response.
    public var speed: Double?
    /// How many of the set's running agents use it.
    public var agents: Int
    /// The source of the agent its speed comes from (else of its first agent), which names its vendor when the price
    /// table does not (CHARACTERS 7).
    public var source: SourceKind

    public init(model: String, speed: Double?, agents: Int, source: SourceKind) {
        self.model = model
        self.speed = speed
        self.agents = agents
        self.source = source
    }

    /// The speed rounded half away from zero.
    public var shown: Int? { speed.map(perfRoundHalfAway) }
}

/// What a `character` orb's 性能 card and models card read from its set `A` (METRICS 6.9 *Readouts of a set*,
/// DESIGN 2a.6): the first-content latency and the running models. They never drive heat, titles, peaks or the
/// character choice.
public enum PerfSetReadouts {
    public struct Latency: Equatable, Sendable {
        /// The median in ms; nil with fewer than 3 values.
        public var ms: Double?
        /// How many responses gave a value.
        public var n: Int

        public init(ms: Double?, n: Int) {
            self.ms = ms
            self.n = n
        }
    }

    /// The median `firstMs − startMs` over the union of the agents' samples (an even count: the mean of the two middle
    /// values). `agents` are the set's listed agents with their evaluations; their phases do not matter.
    public static func firstLatency(of agents: [AgentStatus]) -> Latency {
        var values: [Int64] = []
        for agent in agents {
            if let latencies = agent.perf?.latencies { values.append(contentsOf: latencies) }
        }
        guard values.count >= PerfFormula.minimumSamples else { return Latency(ms: nil, n: values.count) }
        values.sort()
        let mid = values.count / 2
        let median = values.count % 2 == 1 ? Double(values[mid]) : (Double(values[mid - 1]) + Double(values[mid])) / 2
        return Latency(ms: median, n: values.count)
    }

    /// The set's agents that are not idle (their phase after the staleness rule) and name a model, grouped by that exact
    /// model id: fastest first, models without a speed last, ties by model id (UTF-16 code units).
    public static func runningModels(of agents: [AgentStatus]) -> [RunningModel] {
        var rows: [RunningModel] = []
        var index: [OrdinalKey: Int] = [:]
        for agent in agents where agent.phase != .idle {
            guard let model = agent.model, !model.isEmpty else { continue }
            let speed = agent.recentSpeed.flatMap { $0.isFinite ? $0 : nil }
            let key = OrdinalKey(model)
            guard let at = index[key] else {
                index[key] = rows.count
                rows.append(RunningModel(model: model, speed: speed, agents: 1, source: agent.source))
                continue
            }
            rows[at].agents += 1
            if let speed, rows[at].speed.map({ speed > $0 }) ?? true {
                rows[at].speed = speed
                rows[at].source = agent.source
            }
        }
        return rows.sorted { lhs, rhs in
            switch (lhs.speed, rhs.speed) {
            case let (a?, b?) where a != b: a > b
            case (nil, _?): false
            case (_?, nil): true
            default: AgentParents.ordinal(lhs.model, precedes: rhs.model)
            }
        }
    }
}
