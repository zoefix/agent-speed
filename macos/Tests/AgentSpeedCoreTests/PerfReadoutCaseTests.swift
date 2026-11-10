import Foundation
import Testing
@testable import AgentSpeedCore

/// The `readouts` cases of `spec/fixtures/perf-score-cases.json` (METRICS 6.9 *Readouts of a set*): each case's agents
/// and responses go through the engine, and the set's first-content latency and running models come from the
/// snapshot's agents, as an orb reads them.
@Suite("Readouts of a set fixture cases")
struct PerfReadoutCaseTests {
    static func snapshot(_ spec: [String: Any]) throws -> SpeedSnapshot {
        var engine = SpeedEngine()
        for agent in try #require(spec["agents"] as? [[String: Any]]) {
            engine.setAgent(EngineAgent(
                id: try #require(agent.string("id")),
                source: try PerfFixture.source(agent),
                project: "fixture",
                model: agent.string("model"),
                phase: try #require(AgentPhase(rawValue: agent.string("phase") ?? "")),
                inFlightStartMs: nil,
                lastLineMs: agent.int64("lastLineMs"),
                parentId: agent.string("parentId")
            ))
        }
        for response in try #require(spec["responses"] as? [[String: Any]]) {
            engine.upsert(try PerfFixture.response(response), project: "fixture", isCompletion: true, emitsEvents: false)
        }
        let now = try #require(spec.int64("nowMs"))
        let output = engine.advance(nowMs: now, todayStartMs: 0, emitsEvents: false)
        return SnapshotBuilder.running(output, context: .fixture(todayStartMs: 0))
    }

    @Test(arguments: PerfFixture.names("readouts"))
    func readouts(_ name: String) throws {
        let spec = try PerfFixture.spec("readouts", name)
        let agents = try Self.snapshot(spec).agents
        let latency = PerfSetReadouts.firstLatency(of: agents)
        let models = PerfSetReadouts.runningModels(of: agents)
        let actual: [String: Any] = [
            "firstLatency": ["ms": latency.ms ?? NSNull(), "n": latency.n] as [String: Any],
            "models": models.map { row -> [String: Any] in
                ["model": row.model, "speed": row.speed ?? NSNull(), "agents": row.agents, "shown": row.shown ?? NSNull()]
            },
        ]
        let differences = PerfFixture.differences(actual, spec["expect"])
        #expect(differences.isEmpty, "\(name): \(differences.prefix(8))")
    }

    @Test func aGroupTakesTheSourceOfItsFastestAgent() {
        func agent(_ id: String, _ source: SourceKind, speed: Double?, phase: AgentPhase = .generating) -> AgentStatus {
            AgentStatus(id: id, source: source, project: "p", model: "kimi-k3", phase: phase, isSubagent: false,
                        live: .zero, recentSpeed: speed, lastActivity: Date())
        }
        let rows = PerfSetReadouts.runningModels(of: [agent("a", .opencode, speed: nil), agent("b", .kimi, speed: 40),
                                                      agent("c", .opencode, speed: 30), agent("d", .omp, speed: 90, phase: .idle)])
        #expect(rows == [RunningModel(model: "kimi-k3", speed: 40, agents: 3, source: .kimi)])
        let alone = PerfSetReadouts.runningModels(of: [agent("a", .opencode, speed: nil)])
        #expect(alone.first?.source == .opencode && alone.first?.shown == nil)
    }

    @Test func modelIdsGroupByTheirCodeUnits() {
        // "é" precomposed and decomposed are equal Swift strings but different model ids.
        func agent(_ id: String, _ model: String, _ speed: Double) -> AgentStatus {
            AgentStatus(id: id, source: .codex, project: "p", model: model, phase: .tools, isSubagent: false, live: .zero,
                        recentSpeed: speed, lastActivity: Date())
        }
        let rows = PerfSetReadouts.runningModels(of: [agent("a", "mod\u{E9}le", 10), agent("b", "mode\u{301}le", 10),
                                                      agent("c", "", 99)])
        #expect(rows.count == 2)
        #expect(rows.map(\.shown) == [10, 10])
    }

    @Test func theShownSpeedRoundsHalfAwayFromZero() {
        #expect(RunningModel(model: "m", speed: 106.5, agents: 1, source: .codex).shown == 107)
        #expect(RunningModel(model: "m", speed: 99.49, agents: 1, source: .codex).shown == 99)
    }
}
