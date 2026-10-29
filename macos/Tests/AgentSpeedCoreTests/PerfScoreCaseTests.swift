import Foundation
import Testing
@testable import AgentSpeedCore

/// `spec/fixtures/perf-score-cases.json` (METRICS 6.9, 6.11) through the app's own evaluation, set values, recorder,
/// pooled scores, baseline and History window rows. The `thinking` cases are `IntelligenceCaseTests`.
@Suite("Performance score fixture cases")
struct PerfScoreCaseTests {
    @Test func everyGroupIsInTheFixture() {
        #expect(PerfFixture.group("latencyBins").count == 16)
        #expect(PerfFixture.group("agents").count == 7)
        #expect(PerfFixture.group("sets").count == 3)
        #expect(PerfFixture.group("days").count == 1)
        #expect(PerfFixture.group("pooled").count == 4)
        #expect(PerfFixture.group("baseline").count == 3)
        #expect(PerfFixture.group("range").count == 2)
    }

    @Test func theEdgesAreTheFixtures() {
        let edges = (PerfFixture.cases["edges"] as? [NSNumber])?.map(\.int64Value)
        #expect(edges == PerfLatency.edges)
    }

    @Test func latencyBuckets() throws {
        for item in PerfFixture.group("latencyBins") {
            let ms = try #require(item.int64("ms"))
            #expect(PerfLatency.bucket(ms) == item.int("expect"), "\(ms) ms")
        }
    }

    /// Every agent at every query, with the facts of the card and, with a history, the baseline.
    @Test(arguments: PerfFixture.names("agents"))
    func agents(_ name: String) throws {
        let spec = try PerfFixture.spec("agents", name)
        let responses = try #require(spec["responses"] as? [[String: Any]]).map(PerfFixture.response)
        let actions = spec["actions"] as? [String: [[String: Any]]] ?? [:]
        let history = try (spec["history"] as? [[String: Any]] ?? []).map(PerfFixture.record)
        let today = spec.string("today")
        for query in try #require(spec["queries"] as? [[String: Any]]) {
            let now = try #require(query.int64("nowMs"))
            let expected = try #require(query["expect"] as? [String: [String: Any]])
            for agent in try #require(spec["agents"] as? [[String: Any]]) {
                let id = try #require(agent.string("id"))
                let source = try PerfFixture.source(agent)
                var evaluation = PerfEvaluation.evaluate(
                    source: source, responses: responses.filter { $0.agentId == id },
                    actions: try (actions[id] ?? []).map(PerfFixture.action), nowMs: now)
                if let today, evaluation.score != nil, spec["history"] != nil {
                    evaluation.baseline = PerfHistory.baseline(history, source: source, model: evaluation.model ?? "",
                                                               today: today)
                }
                let phase = try #require(AgentPhase(rawValue: agent.string("phase") ?? ""))
                let dump = PerfDump.Agent(id: id, source: source, phase: phase, lastLineMs: agent.int64("lastLineMs"),
                                          parentId: nil, actions: [])
                let idle = PerfDump.effectivePhase(dump, nowMs: now) == .idle
                let value = evaluation.value(agentId: id, lastLineMs: nil, idle: idle)
                let actual: [String: Any] = [
                    "form": value.form.rawValue, "score": evaluation.score ?? NSNull(), "shown": evaluation.shown ?? NSNull(),
                    "provisional": evaluation.provisional, "n": evaluation.n, "tools": evaluation.tools,
                    "activeMs": evaluation.activeMs, "spanStartMs": evaluation.spanStartMs ?? NSNull(),
                    "model": evaluation.model ?? NSNull(), "components": PerfFixture.components(evaluation.components),
                    "baseline": evaluation.baseline ?? NSNull(), "vsUsual": evaluation.vsUsual ?? NSNull(),
                    "vsUsualPercent": evaluation.vsUsualPercent ?? NSNull(),
                ]
                let wanted = try #require(expected[id])
                let differences = PerfFixture.differences(actual, wanted)
                #expect(differences.isEmpty, "\(name) @ \(now) \(id): \(differences.prefix(6))")
            }
        }
    }

    /// The whole set's value and every board group's, naming the supplier.
    @Test(arguments: PerfFixture.names("sets"))
    func sets(_ name: String) throws {
        let spec = try PerfFixture.spec("sets", name)
        let agents = try #require(spec["agents"] as? [[String: Any]])
        let values = try #require(spec["values"] as? [String: [String: Any]])
        var byId: [String: PerfSetValue] = [:]
        for agent in agents {
            let id = try #require(agent.string("id"))
            let value = try #require(values[id])
            let form = try #require(PerfValueForm(rawValue: value.string("form") ?? ""))
            byId[id] = PerfSetValue(form: form, score: value.double("score"), agentId: id,
                                    lastLineMs: agent.int64("lastLineMs"))
        }
        func json(_ value: PerfSetValue) -> [String: Any] {
            ["form": value.form.rawValue, "score": value.score ?? NSNull(), "shown": value.shown ?? NSNull(),
             "agent": value.agentId ?? NSNull()]
        }
        let ids = try agents.map { try #require($0.string("id")) }
        let expected = try #require(spec["expect"] as? [String: Any])
        let all = PerfSetValue.highest(ids.compactMap { byId[$0] })
        #expect(PerfFixture.close(json(all), expected["all"]), "\(name) all: \(json(all))")
        let groups = PerfGroups.boardGroups(agents.map { ($0.string("id") ?? "", $0.string("parentId")) })
        var actual: [String: Any] = [:]
        for group in groups {
            var object = json(PerfSetValue.highest(group.members.compactMap { byId[$0] }))
            object["members"] = group.members
            actual[group.root] = object
        }
        let differences = PerfFixture.differences(actual, expected["groups"])
        #expect(differences.isEmpty, "\(name) groups: \(differences)")
    }

    /// Completions in the order they complete → records per UTC day, source and model.
    @Test(arguments: PerfFixture.names("days"))
    func days(_ name: String) throws {
        let spec = try PerfFixture.spec("days", name)
        var recorder = PerfRecorder(timeZone: TimeZone(identifier: "UTC")!)
        for agent in try #require(spec["agents"] as? [[String: Any]]) {
            let id = try #require(agent.string("id"))
            let source = try PerfFixture.source(agent)
            for event in try #require(agent["events"] as? [[String: Any]]) {
                if let response = event["response"] as? [String: Any] {
                    recorder.response(try PerfFixture.response(response), agentId: id, source: source)
                } else if let call = event["action"] as? [String: Any] {
                    let action = try PerfFixture.action(call)
                    guard let result = action.resultMs else { continue }
                    recorder.call(ToolCompletion(callMs: action.callMs, resultMs: result, failed: action.failed,
                                                 model: action.model, seeded: false), agentId: id, source: source)
                }
            }
        }
        let actual = recorder.rows.map { row -> [String: Any] in
            var object = PerfFixture.record(row.record)
            object["day"] = row.day
            object["source"] = row.source.rawValue
            object["model"] = row.model
            return object
        }
        let differences = PerfFixture.differences(actual, spec["expect"])
        #expect(differences.isEmpty, "\(name): \(differences.prefix(8))")
    }

    @Test(arguments: PerfFixture.names("pooled"))
    func pooled(_ name: String) throws {
        let spec = try PerfFixture.spec("pooled", name)
        let records = try #require(spec["records"] as? [[String: Any]]).map(PerfFixture.record)
        let pooled = PerfPooled(records.map(\.record), source: try PerfFixture.source(spec))
        let differences = PerfFixture.differences(PerfFixture.pooled(pooled), spec["expect"])
        #expect(differences.isEmpty, "\(name): \(differences.prefix(8))")
    }

    @Test(arguments: PerfFixture.names("baseline"))
    func baseline(_ name: String) throws {
        let spec = try PerfFixture.spec("baseline", name)
        let records = try #require(spec["records"] as? [[String: Any]]).map(PerfFixture.record)
        let value = PerfHistory.baseline(records, source: try PerfFixture.source(spec), model: spec.string("model") ?? "",
                                         today: try #require(spec.string("today")))
        #expect(PerfFixture.close(value, spec["expect"]), "\(name): \(String(describing: value))")
    }

    @Test(arguments: PerfFixture.names("range"))
    func range(_ name: String) throws {
        let spec = try PerfFixture.spec("range", name)
        let records = try #require(spec["records"] as? [[String: Any]]).map(PerfFixture.record)
        let rows = PerfHistory.range(records, first: try #require(spec.string("first")),
                                     last: try #require(spec.string("last")),
                                     source: spec.string("source").flatMap(SourceKind.init(rawValue:)))
        let actual = rows.map { row -> [String: Any] in
            var object = PerfFixture.pooled(row.pooled)
            object["source"] = row.source.rawValue
            object["model"] = row.model
            object["trend"] = row.trend.map { ["day": $0.day, "score": $0.score ?? NSNull()] as [String: Any] }
            return object
        }
        let differences = PerfFixture.differences(actual, spec["expect"])
        #expect(differences.isEmpty, "\(name): \(differences.prefix(8))")
    }

    /// Calendar labels across months, years and leap days.
    @Test func dayLabels() {
        #expect(PerfDay.adding(-14, to: "2027-01-10") == "2026-12-27")
        #expect(PerfDay.adding(1, to: "2028-02-28") == "2028-02-29")
        #expect(PerfDay.adding(1, to: "2027-02-28") == "2027-03-01")
        #expect(PerfDay.days(from: "2026-12-30", to: "2027-01-02") == ["2026-12-30", "2026-12-31", "2027-01-01", "2027-01-02"])
        #expect(PerfDay.days(from: "2027-01-02", to: "2027-01-01").isEmpty)
    }
}
