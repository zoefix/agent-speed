import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// Swift port of the board prototypes' shared simulation (`spec/prototypes/board/sim.js`) for the developer
/// harnesses: the same scenarios, seeded generator, agent phases and mode changes, the live metrics of each agent
/// (a steady performance score, thinking, cost rate), ranked by the production `BoardRanking`. Step for step it shows
/// what the prototypes show (`?scenario=team&steps=40`: 707 tok/s, 思考 174, 0.024 US$/s). `speedScale` multiplies every agent's
/// speed (the benchmark's cruise and redline states); `recordsReasoning` false simulates sources that never report
/// reasoning apart from output (the prototypes' `noreasoning=1`).
struct BoardSimulation {
    enum Phase {
        case idle, generating, tools
    }

    struct Agent {
        var id: String
        var parentID: String?
        var project: String
        var label: String
        var source: String
        var model: String
        var base: Double
        var phase = Phase.idle
        var phaseLeft: Double
        var burst = 1.0
        var inst = 0.0
        var window: [Double] = []
        var live = 0.0
        var lastActive = 0.0
        var lazy: Bool
        /// sim.js `effort` and `speedMode` tokens (DESIGN 4 capsule).
        var effort: String?
        var speed: String?
        /// sim.js `hashUnit(id)`, 0..<1.
        var seed = 0.0
        /// The live metrics (sim.js `measure`): performance score, thinking tok/s, cost µUSD/s, priced.
        var score: Double?
        var thinking = 0.0
        var recordsReasoning = true
        var costRate = 0.0
        var priced = true
    }

    struct Group {
        var id: String
        var root: Agent
        var subs: [Agent]
        var live: Double
        var generating: Int
        var lastActive: Double
    }

    struct Snapshot {
        var time: Double
        var groups: [Group]
        var events: [BoardRankEvent]
        var live: Double
        var generating: Int
        var tools: Int
        var peak: Double
        var today: Double
        var cost: Double
        /// sim.js `costToday`: today's cost of the agents' cost rates, USD.
        var costToday: Double
        /// `live` at every tick, oldest first, at most 120 (sim.js `history`).
        var history: [Double]
    }

    static let sources: [String: UInt32] = [
        "codex": 0x34D399, "claudeCode": 0xFF8A65, "claudeDesktop": 0xB39DFF, "deepseek": 0x4D6BFE,
        "opencode": 0xF4B13E, "gemini": 0x34B8F0,
    ]
    /// sim.js `PRICES`: output list prices, USD per million tokens.
    static let prices: [String: Double] = [
        "claude-opus-5-5": 20, "claude-sonnet-5-5": 10, "claude-haiku-5-5": 0.5, "gpt-6.1-sol": 20, "deepseek-flash": 1.2,
        "gemini-3.5-pro": 12,
    ]
    private static let effortLevels: [String: Int] = [
        "off": 0, "minimal": 1, "low": 1, "medium": 2, "high": 3, "xhigh": 4, "max": 5, "ultra": 5, "ultracode": 5,
    ]
    private static let bolts: [String: Int] = ["fast": 1, "highspeed": 1, "ultrafast": 2]
    private static let models: [String: (Double, Double)] = [
        "claude-opus-5-5": (55, 115), "claude-sonnet-5-5": (80, 160), "claude-haiku-5-5": (150, 260),
        "gpt-6.1-sol": (90, 210), "deepseek-flash": (110, 180), "gemini-3.5-pro": (100, 190),
    ]

    private struct Root {
        var project: String
        var source: String
        var model: String
        var subs: Int
        var workflow = false
        var lazy = false
        var effort: String?
        var speed: String?
    }

    private static func roots(_ name: String) -> [Root] {
        switch name {
        case "duel":
            [Root(project: "opencdn", source: "claudeCode", model: "claude-opus-5-5", subs: 2, effort: "max"),
             Root(project: "claude-manager", source: "codex", model: "gpt-6.1-sol", subs: 2, effort: "ultra", speed: "ultrafast")]
        case "team":
            [Root(project: "opencdn", source: "claudeCode", model: "claude-opus-5-5", subs: 3, effort: "max"),
             Root(project: "claude-manager", source: "codex", model: "gpt-6.1-sol", subs: 2, effort: "xhigh", speed: "fast"),
             Root(project: "agent-speed", source: "claudeCode", model: "claude-opus-5-5", subs: 4, effort: "ultracode"),
             Root(project: "nebula", source: "opencode", model: "claude-sonnet-5-5", subs: 0, effort: "high"),
             Root(project: "rain", source: "deepseek", model: "deepseek-flash", subs: 0)]
        case "calm":
            [Root(project: "agent-speed", source: "claudeCode", model: "claude-opus-5-5", subs: 0, lazy: true, effort: "xhigh"),
             Root(project: "notes", source: "codex", model: "gpt-6.1-sol", subs: 0, lazy: true, effort: "medium")]
        default:
            [Root(project: "tihu", source: "claudeDesktop", model: "deepseek-flash", subs: 100, workflow: true),
             Root(project: "opencdn", source: "claudeCode", model: "claude-opus-5-5", subs: 3, effort: "max"),
             Root(project: "claude-manager", source: "codex", model: "gpt-6.1-sol", subs: 2, effort: "ultra", speed: "ultrafast"),
             Root(project: "agent-speed", source: "claudeCode", model: "claude-opus-5-5", subs: 1, effort: "ultracode")]
        }
    }

    let scenario: String
    let tick: Double
    var speedScale = 1.0
    var recordsReasoning = true
    /// Holds every agent idle (the benchmark's idle state): no speed, every phase idle and none changing.
    var paused = false
    private(set) var agents: [Agent] = []
    private(set) var time = 0.0
    private var state: UInt32
    private var ranking = BoardRanking()
    private var today = 128.4e6
    private var cost = 42.1
    private var costToday = 42.1
    private var peak = 9120.0
    private var history: [Double] = []
    /// The snapshot taken last (without ticking the ranking again).
    private(set) var last: Snapshot?

    init(scenario: String = "swarm", seed: UInt32 = 7, tick: Double = 0.5) {
        self.scenario = scenario
        self.tick = tick
        state = seed &+ UInt32(scenario.count) &* 101
        if state == 0 { state = 1 }
        for (r, root) in Self.roots(scenario).enumerated() {
            let rootID = "a\(r)"
            agents.append(makeAgent(rootID, parent: nil, root: root, label: root.project))
            for i in 0..<root.subs {
                let label = root.workflow ? "count-\(i + 1)" : ["explore", "review", "tests", "docs"][i % 4] + (i >= 4 ? "-\(i)" : "")
                var sub = makeAgent(rootID + "s\(i)", parent: rootID, root: root, label: label)
                if root.workflow {
                    sub.phaseLeft = random() * 3
                    sub.base *= 0.75 + random() * 0.4
                }
                agents.append(sub)
            }
        }
        for _ in 0..<6 { advance(silent: true) }
        _ = snapshot()
    }

    /// sim.js `hashUnit`: FNV-1a over the UTF-16 units, then a murmur finalizer, as a unit fraction.
    static func hashUnit(_ text: String) -> Double {
        var h: UInt32 = 2_166_136_261
        for unit in text.utf16 {
            h ^= UInt32(unit)
            h = h &* 16_777_619
        }
        h ^= h >> 16
        h = h &* 0x85EB_CA6B
        h ^= h >> 13
        h = h &* 0xC2B2_AE35
        h ^= h >> 16
        return Double(h) / 4_294_967_296
    }

    /// A simulated performance score (METRICS 6.9): steady per agent, around the reference 1000.
    static func score(_ agent: Agent) -> Double? {
        guard SourceKind(rawValue: agent.source) != nil else { return nil }
        return (620 + 1180 * agent.seed).rounded() + 0.25
    }

    /// sim.js `measure`: the agent's performance score, its reasoning (breathing ±20 % over ~7 s), its cost rate (Ultrafast has no
    /// list price, Fast is billed at 2×).
    private func measure(_ a: inout Agent) {
        let level = a.effort.flatMap { effort in Self.effortLevels[effort].map { $0 + (effort == "ultra" || effort == "ultracode" ? 1 : 0) } } ?? 2
        a.score = Self.score(a)
        a.recordsReasoning = recordsReasoning && a.source != "deepseek"
        let breath = 0.8 + 0.4 * (0.5 + 0.5 * sin(time * 0.9 + a.seed * 6.283))
        a.thinking = a.recordsReasoning ? a.live * (0.07 + 0.05 * Double(level)) * (0.7 + 0.6 * a.seed) * breath : 0
        a.priced = a.speed != "ultrafast"
        let bolts = a.speed.flatMap { Self.bolts[$0] } ?? 0
        a.costRate = a.priced ? a.live * (Self.prices[a.model] ?? 0) * (1.3 + 0.6 * a.seed) * (bolts > 0 ? 2 : 1) : 0
    }

    /// sim.js `rng`: xorshift32.
    private mutating func random() -> Double {
        state ^= state << 13
        state ^= state >> 17
        state ^= state << 5
        return Double(state) / 4_294_967_296
    }

    private mutating func makeAgent(_ id: String, parent: String?, root: Root, label: String) -> Agent {
        let (lo, hi) = Self.models[root.model] ?? (60, 120)
        let base = lo + (hi - lo) * random()
        let left = 1 + random() * 6
        // sim.js draws the agent's `todayOutput` next; the boards do not show it, but the sequence must stay the same.
        _ = random()
        return Agent(id: id, parentID: parent, project: root.project, label: label, source: root.source, model: root.model,
                     base: base, phaseLeft: left, lazy: root.lazy, effort: parent != nil && root.workflow ? nil : root.effort,
                     speed: root.speed, seed: Self.hashUnit(id))
    }

    private mutating func nextPhase(_ a: inout Agent) {
        let busy = a.lazy ? 0.25 : a.parentID != nil ? 0.72 : 0.6
        if a.phase == .generating {
            a.phase = random() < 0.8 ? .tools : .idle
            a.phaseLeft = a.phase == .tools ? 0.6 + random() * 3.5 : 2 + random() * 8
        } else {
            a.phase = random() < busy ? .generating : a.phase == .tools ? .idle : .tools
            a.phaseLeft = a.phase == .generating ? 2.5 + random() * 12 : 0.8 + random() * 5
            if a.phase == .generating { a.burst = 0.75 + random() * 0.55 }
        }
    }

    /// One engine tick; the first ones of a scenario are silent (no mode changes), as sim.js `reset`.
    mutating func advance(silent: Bool = false) {
        time += tick
        let duel = scenario == "duel"
        for index in agents.indices {
            var a = agents[index]
            if !paused {
                a.phaseLeft -= tick
                if a.phaseLeft <= 0 { nextPhase(&a) }
            }
            let wobble = 0.82 + 0.36 * random()
            a.inst = paused ? 0 : a.inst
            var target = a.phase == .generating && !paused ? a.base * a.burst * wobble * speedScale : 0
            if duel, a.parentID == nil { target *= 1 + 0.55 * sin(time / 3.1 + (a.id == "a0" ? 0 : .pi)) }
            a.inst += (target - a.inst) * (a.phase == .generating ? 0.6 : 0.85)
            if a.inst < 0.05 { a.inst = 0 }
            a.window.append(a.inst)
            if a.window.count > 6 { a.window.removeFirst() }
            a.live = paused ? 0 : a.window.reduce(0, +) / 6
            if a.live < 0.05 { a.live = 0 }
            if a.live > 0 { a.lastActive = time }
            measure(&a)
            agents[index] = a
        }
        // Occasional mode changes so capsules animate.
        if !silent, random() < 0.04 {
            let roots = agents.indices.filter { agents[$0].parentID == nil }
            let index = roots[Int(random() * Double(roots.count))]
            let ladder = ["medium", "high", "xhigh", "max"]
            if let effort = agents[index].effort, effort != "ultra", effort != "ultracode" {
                agents[index].effort = ladder[Int(random() * Double(ladder.count))]
            }
            if agents[index].source == "codex" { agents[index].speed = ["fast", "ultrafast", nil][Int(random() * 3)] }
        }
        let total = agents.reduce(0) { $0 + $1.live }
        today += total * tick
        cost += total * tick * 15e-6
        costToday += agents.reduce(0) { $0 + $1.costRate } * tick / 1e6
        peak = max(peak, total)
        history.append(total)
        if history.count > 120 { history.removeFirst() }
    }

    /// The groups ranked by the production ranking, with this tick's events.
    mutating func snapshot() -> Snapshot {
        let agents = paused ? self.agents.map { agent in
            var idle = agent
            idle.phase = .idle
            return idle
        } : self.agents
        var groups: [String: Group] = [:]
        var order: [String] = []
        for a in agents where a.parentID == nil {
            groups[a.id] = Group(id: a.id, root: a, subs: [], live: a.live, generating: a.phase == .generating ? 1 : 0,
                                 lastActive: a.lastActive)
            order.append(a.id)
        }
        for a in agents {
            guard let parent = a.parentID, var group = groups[parent] else { continue }
            group.subs.append(a)
            group.live += a.live
            group.generating += a.phase == .generating ? 1 : 0
            group.lastActive = max(group.lastActive, a.lastActive)
            groups[parent] = group
        }
        for id in order {
            groups[id]?.subs.sort { x, y in
                x.live != y.live ? x.live > y.live : x.lastActive != y.lastActive ? x.lastActive > y.lastActive : x.id < y.id
            }
        }
        let entries = order.compactMap { groups[$0] }.map {
            BoardRankEntry(id: $0.id, live: $0.live, lastActiveMs: $0.lastActive * 1000)
        }
        let result = ranking.tick(entries)
        let live = agents.reduce(0) { $0 + $1.live }
        let snapshot = Snapshot(time: time, groups: result.order.compactMap { groups[$0] }, events: result.events, live: live,
                                generating: agents.filter { $0.phase == .generating }.count,
                                tools: agents.filter { $0.phase == .tools }.count, peak: peak, today: today, cost: cost,
                                costToday: costToday, history: history)
        last = snapshot
        return snapshot
    }
}
