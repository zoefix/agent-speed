import Foundation
import Testing
@testable import AgentSpeedCore

/// The `thinking` cases of `spec/fixtures/perf-score-cases.json` (METRICS 6.9 *Live thinking*), as
/// `spec/tools/live_thinking.py` computes them. The engine's sums are per second of the 3-second window, so the
/// fixture's `weight` (tokens in the window) is `IntelligenceSums.output × 3`.
@Suite("Live thinking fixture cases")
struct IntelligenceCaseTests {
    static let windowSeconds = 3.0

    static func cases(_ group: String) -> [[String: Any]] {
        let thinking = (try? Fixtures.json("perf-score-cases.json"))?["thinking"] as? [String: Any]
        return thinking?[group] as? [[String: Any]] ?? []
    }

    static func names(_ group: String) -> [String] { cases(group).compactMap { $0.string("name") } }

    static var windowNames: [String] { names("windows") }
    static var agentWindowNames: [String] { names("agentWindows") }
    static var groupWindowNames: [String] { names("groupWindows") }
    static var modelWindowNames: [String] { names("modelWindows") }

    /// The case's agents and responses in an engine without a price table (nothing here depends on prices).
    static func engine(_ group: String, _ name: String) throws -> (SpeedEngine, [[String: Any]]) {
        let spec = try #require(cases(group).first { $0.string("name") == name })
        var engine = SpeedEngine()
        for agent in try #require(spec["agents"] as? [[String: Any]]) {
            engine.setAgent(EngineAgent(
                id: try #require(agent.string("id")),
                source: try #require(SourceKind(rawValue: agent.string("source") ?? "")),
                project: "fixture",
                model: agent.string("model"),
                phase: try #require(AgentPhase(rawValue: agent.string("phase") ?? "")),
                inFlightStartMs: agent.int64("inFlightStartMs"),
                lastLineMs: agent.int64("lastLineMs"),
                parentId: agent.string("parentId")
            ))
        }
        for response in try #require(spec["responses"] as? [[String: Any]]) {
            engine.upsert(ResponseRecord(
                key: try #require(response.string("key")),
                source: try #require(SourceKind(rawValue: response.string("source") ?? "")),
                agentId: try #require(response.string("agentId")),
                model: response.string("model"),
                startMs: response.int64("startMs"),
                endMs: try #require(response.int64("endMs")),
                usage: TokenUsage(input: response.int("input"), output: response.int("output"),
                                  reasoning: response.int("reasoning")),
                isFinal: response["final"] as? Bool ?? true
            ), project: "fixture", isCompletion: true, emitsEvents: false)
        }
        return (engine, try #require(spec["queries"] as? [[String: Any]]))
    }

    static func snapshot(_ engine: inout SpeedEngine, _ now: Int64) -> SpeedSnapshot {
        let output = engine.advance(nowMs: now, todayStartMs: 0, emitsEvents: false)
        return SnapshotBuilder.running(output, context: .fixture(todayStartMs: 0))
    }

    static func expect(_ sums: IntelligenceSums, _ expected: [String: Any], _ label: String,
                       sourceLocation: SourceLocation = #_sourceLocation) {
        EngineCaseTests.expectClose(sums.thinking, expected.double("liveThinking"), "\(label) liveThinking",
                                    sourceLocation: sourceLocation)
        EngineCaseTests.expectClose(sums.thinkingShare, expected.double("thinkingShare"), "\(label) thinkingShare",
                                    sourceLocation: sourceLocation)
        EngineCaseTests.expectClose(sums.output * windowSeconds, expected.double("weight"), "\(label) weight",
                                    sourceLocation: sourceLocation)
    }

    /// Σ of the output, thinking and base sums (the parts that add; the share does not).
    static func total(_ parts: [IntelligenceSums]) -> (output: Double, thinking: Double, base: Double) {
        parts.reduce((0, 0, 0)) { ($0.output + $1.output, $0.thinking + $1.thinking, $0.base + $1.thinkingBase) }
    }

    static func expectAddUp(_ parts: [IntelligenceSums], _ whole: IntelligenceSums, _ label: String,
                            sourceLocation: SourceLocation = #_sourceLocation) {
        let added = total(parts)
        EngineCaseTests.expectClose(added.output, whole.output, "\(label): output", sourceLocation: sourceLocation)
        EngineCaseTests.expectClose(added.thinking, whole.thinking, "\(label): thinking", sourceLocation: sourceLocation)
        EngineCaseTests.expectClose(added.base, whole.thinkingBase, "\(label): thinkingBase", sourceLocation: sourceLocation)
    }

    @Test func everyGroupIsInTheFixture() {
        #expect(Self.windowNames.count >= 6)
        #expect(Self.agentWindowNames.count >= 5)
        #expect(Self.groupWindowNames.count >= 2)
        #expect(Self.modelWindowNames.count >= 2)
    }

    /// The model sums (for live reporting): a response counts for its own model, an in-flight estimate for its agent's
    /// current model, keyed by the raw model ("" when unknown); they add up to the set's sums, and the snapshot's
    /// per-id thinking (METRICS 11.10) adds up to the set's thinking.
    @Test(arguments: modelWindowNames)
    func modelWindow(_ name: String) throws {
        var (engine, queries) = try Self.engine("modelWindows", name)
        for query in queries {
            let now = try #require(query.int64("nowMs"))
            var byModel: [String: IntelligenceSums] = [:]
            for (key, sums) in engine.liveModelSums(nowMs: now) { byModel[key.model ?? "", default: .zero].add(sums) }
            let snapshot = Self.snapshot(&engine, now)
            Self.expect(snapshot.intelligence, try #require(query["expect"] as? [String: Any]), "\(name) @ \(now)")
            Self.expectAddUp(Array(byModel.values), snapshot.intelligence, "\(name) @ \(now) models add up")
            let expected = try #require(query["models"] as? [String: [String: Any]])
            #expect(Set(byModel.keys) == Set(expected.keys), "\(name) @ \(now) models")
            for (model, values) in expected {
                Self.expect(byModel[model] ?? .zero, values, "\(name) @ \(now) \(model)")
            }
            let perf = snapshot.report.perf
            EngineCaseTests.expectClose(perf.models.values.reduce(0) { $0 + $1.thinking }, snapshot.intelligence.thinking,
                                        "\(name) @ \(now) resolved ids add up")
            EngineCaseTests.expectClose(perf.thinking, snapshot.intelligence.thinking, "\(name) @ \(now) all sources")
        }
    }

    /// The set's sums, and an orb's own sources' sums.
    @Test(arguments: windowNames)
    func window(_ name: String) throws {
        var (engine, queries) = try Self.engine("windows", name)
        for query in queries {
            let now = try #require(query.int64("nowMs"))
            let snapshot = Self.snapshot(&engine, now)
            let kinds = try (query["sources"] as? [String])?.map { try #require(SourceKind(rawValue: $0)) }
            let sums = OrbSubset.reading(snapshot, sources: kinds.map { SourceSet($0) }).intelligence
            Self.expect(sums, try #require(query["expect"] as? [String: Any]),
                        "\(name) @ \(now)\(kinds.map { " \($0)" } ?? "")")
        }
    }

    /// Every listed agent's own sums, which add up to the set's.
    @Test(arguments: agentWindowNames)
    func agentWindow(_ name: String) throws {
        var (engine, queries) = try Self.engine("agentWindows", name)
        for query in queries {
            let now = try #require(query.int64("nowMs"))
            let snapshot = Self.snapshot(&engine, now)
            Self.expect(snapshot.intelligence, try #require(query["expect"] as? [String: Any]), "\(name) @ \(now)")
            Self.expectAddUp(snapshot.agents.map(\.intelligence), snapshot.intelligence, "\(name) @ \(now) agents add up")
            let expectedAgents = try #require(query["agents"] as? [String: [String: Any]])
            #expect(snapshot.agents.count == expectedAgents.count, "\(name) @ \(now) listed agents")
            for (id, expected) in expectedAgents {
                let agent = try #require(snapshot.agents.first { $0.id == id }, "\(name) @ \(now) \(id) listed")
                Self.expect(agent.intelligence, expected, "\(name) @ \(now) \(id)")
            }
        }
    }

    /// A board group adds its members' agent sums (never an average of their values), and the groups add up to the
    /// set's sums.
    @Test(arguments: groupWindowNames)
    func groupWindow(_ name: String) throws {
        var (engine, queries) = try Self.engine("groupWindows", name)
        for query in queries {
            let now = try #require(query.int64("nowMs"))
            let snapshot = Self.snapshot(&engine, now)
            Self.expect(snapshot.intelligence, try #require(query["expect"] as? [String: Any]), "\(name) @ \(now)")
            var groupSums: [IntelligenceSums] = []
            for (root, expected) in try #require(query["groups"] as? [String: [String: Any]]) {
                let label = "\(name) @ \(now) group \(root)"
                var sums = AgentGroupSums()
                for id in try #require(expected["members"] as? [String]) {
                    sums.add(try #require(snapshot.agents.first { $0.id == id }, "\(label) \(id) listed"))
                }
                Self.expect(sums.intelligence, expected, label)
                groupSums.append(sums.intelligence)
            }
            Self.expectAddUp(groupSums, snapshot.intelligence, "\(name) @ \(now) groups add up")
        }
    }
}
