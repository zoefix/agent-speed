import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// Performance scores for the boards' simulation and previews (METRICS 6.9): an agent's own value with components that
/// add up to it, so its rows, hero and score card read as the engine's would.
@MainActor
enum BoardScorePreviewData {
    /// The board's 性能 in each form (DESIGN 12), for the previews.
    enum Scenario: String, CaseIterable {
        /// Live scores on most rows: a provisional sub-agent, a resting one, none.
        case live
        /// Every agent idle after work: resting scores, pale.
        case resting
        /// No agent has a score: `–`, an empty arc and meter.
        case none
        /// The highest live score is provisional (the provisional mark on the hero).
        case provisional
        /// The highest live score has five digits: compact on the rows, full on the hero.
        case compact
    }

    /// `agent` with its own performance score: live while it works, resting while idle; nil leaves it with fewer than
    /// three timed requests.
    static func scored(_ agent: AgentStatus, score: Double?, provisional: Bool = false, idle: Bool? = nil) -> AgentStatus {
        var agent = agent
        let idle = idle ?? (agent.phase == .idle)
        guard let score else {
            var none = PerfEvaluation.none(source: agent.source)
            none.n = 2
            agent.perf = none
            agent.intelligence.score = .empty
            return agent
        }
        let evaluation = PerfPreviewData.evaluation(score: score, source: agent.source, model: agent.model ?? "",
                                                    n: provisional ? 6 : 20, tools: 24, baseline: score * 0.94)
        agent.perf = evaluation
        agent.intelligence.score = PerfSetValue(form: idle ? .resting : .live, score: evaluation.score, agentId: agent.id,
                                                lastLineMs: Int64(agent.lastActivity.timeIntervalSince1970 * 1000),
                                                provisional: evaluation.provisional)
        return agent
    }

    /// `tick` with the scores of `scenario` on its agents, its groups and its hero's 性能 made again from them.
    static func tick(_ tick: BoardTick, scenario: Scenario) -> BoardTick {
        var agents = tick.groups.flatMap(\.members)
        let roots = tick.groups.map(\.root.id)
        func assign(_ id: String?, _ score: Double?, provisional: Bool = false, idle: Bool? = nil) {
            guard let id, let index = agents.firstIndex(where: { $0.id == id }) else { return }
            agents[index] = scored(agents[index], score: score, provisional: provisional, idle: idle)
        }
        func root(_ index: Int) -> String? { index < roots.count ? roots[index] : nil }
        func sub(_ index: Int, _ sub: Int = 0) -> String? {
            index < tick.groups.count && sub < tick.groups[index].subs.count ? tick.groups[index].subs[sub].id : nil
        }
        for agent in agents { assign(agent.id, nil) }
        var idle = false
        switch scenario {
        case .live:
            assign(root(0), 1123.4)
            assign(sub(0), 1048.2, provisional: true)
            assign(sub(0, 1), 947)
            assign(root(1), 1286.4)
            assign(sub(1), 702)
            assign(root(2), 893.3)
            assign(root(4), 1452.4, idle: true)
        case .resting:
            idle = true
            assign(root(0), 1123.4)
            assign(sub(0), 1048.2, provisional: true)
            assign(root(1), 1452.4)
            assign(root(2), 893.3)
            assign(root(4), 760)
        case .none:
            break
        case .provisional:
            assign(root(0), 860, provisional: true)
            assign(root(1), 1048.2, provisional: true)
            assign(root(2), 1452.4, idle: true)
        case .compact:
            assign(root(0), 1286.4)
            assign(root(1), 12_840)
            assign(sub(1), 702)
            assign(root(2), 893.3)
        }
        if idle {
            agents = agents.map { agent in
                var agent = agent
                agent.phase = .idle
                agent.live = .zero
                agent.intelligence.output = 0
                agent.intelligence.thinking = 0
                agent.intelligence.thinkingBase = 0
                if let perf = agent.perf, perf.score != nil {
                    agent.intelligence.score = PerfSetValue(form: .resting, score: perf.score, agentId: agent.id,
                                                            lastLineMs: 0, provisional: perf.provisional)
                }
                return agent
            }
        }
        var next = tick
        let grouped = Dictionary(BoardGrouping.groups(agents, metric: tick.metric).map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        next.groups = tick.groups.compactMap { grouped[$0.id] }
        if idle {
            next.value = 0
            next.valueText = SpeedFormat.speed(0)
            next.heat = 0
            next.overdrive = 0
            next.generating = 0
            next.tools = 0
        }
        guard tick.metrics.readouts.hero else { return next }
        var sums = AgentGroupSums()
        for agent in agents { sums.add(agent) }
        next.metrics = .make(readouts: tick.metrics.readouts, intelligence: sums.intelligence, agents: agents, cost: tick.metrics.cost,
                             costUnit: tick.metrics.costUnit, thinkingHidden: tick.metrics.thinkingHidden)
        return next
    }
}
