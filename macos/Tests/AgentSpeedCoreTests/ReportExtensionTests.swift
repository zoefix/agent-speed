import Foundation
import Testing
@testable import AgentSpeedCore

@Suite("Report extensions")
struct ReportExtensionTests {
    @Test func sharedCapabilitiesAreExactIntegers() throws {
        let root = try Fixtures.json("execution-mode-cases.json")
        for item in try #require(root["capabilities"] as? [[String: Any]]) {
            let data = try JSONSerialization.data(withJSONObject: try #require(item["extensions"]))
            let caps = ReportExtensions(try JSONParser.parse(data))
            let enabled = item["modelsEnabled"] as? Bool == true
            #expect((caps.executionModes && enabled) == (item["expected"] as? Bool == true))
            #expect((enabled ? caps.executionModesVersion : 0) == item.int("version"), "\(item.string("id") ?? "")")
        }
        for raw in ["true", "null", "1.0", "1e0", "3", "\"1\""] {
            let value = try JSONParser.parse(Data("{\"executionModes\":\(raw),\"profileAvatar\":\(raw)}".utf8))
            #expect(ReportExtensions(value) == .none)
        }
        // Version 2 is the execution modes' own: other extensions know only 1.
        let two = ReportExtensions(try JSONParser.parse(Data("{\"executionModes\":2,\"profileAvatar\":2}".utf8)))
        #expect(two.executionModesVersion == 2 && !two.profileAvatar)
        for version in [1, 2] {
            let caps = ReportExtensions(modelHarnessRequests: true, executionModesVersion: version, profileAvatar: true,
                                        sharedIdentity: true)
            let bytes = try JSONEncoder().encode(caps)
            #expect(try JSONDecoder().decode(ReportExtensions.self, from: bytes) == caps)
            #expect(String(decoding: bytes, as: UTF8.self).contains("\"executionModes\":\(version)"))
        }
    }

    @Test func sharedRowsFollowTheNegotiatedVersion() throws {
        let root = try Fixtures.json("execution-mode-cases.json")
        func usage(_ item: [String: Any]) -> ReportUsage {
            ReportUsage(requests: Int64(item.int("requests")), output: Int64(item.int("output")),
                        measuredOutput: Int64(item.int("measuredOutput")), measuredMs: Int64(item.int("measuredMs")))
        }
        for item in try #require(root["reportRows"] as? [[String: Any]]) {
            let id = item.string("id") ?? ""
            var sources: [SourceKind: ReportUsage] = [:]
            for (name, total) in try #require(item["sources"] as? [String: [String: Any]]) {
                sources[try #require(SourceKind(rawValue: name))] = usage(total)
            }
            var groups: [ReportModeGroup: ReportUsage] = [:]
            for group in try #require(item["groups"] as? [[String: Any]]) {
                let attribution = ExecutionModeAttribution(
                    kind: try #require(ExecutionModeKind(rawValue: group.string("kind") ?? "")), mode: group.string("mode") ?? "",
                    evidence: group.string("evidence") ?? "", provider: group.string("provider"))
                groups[ReportModeGroup(source: try #require(SourceKind(rawValue: group.string("source") ?? "")),
                                       attribution: attribution), default: .zero] += usage(group)
            }
            let actual = ReportModelBreakdown.modes(sources, groups: groups, version: item.int("version"))
            guard let expected = item["expected"] as? [[String: Any]] else {
                #expect(actual == nil, "\(id)")
                continue
            }
            let json = try #require(actual, "\(id)")
            let rows = try #require(try JSONSerialization.jsonObject(with: json.compactData) as? [[String: Any]])
            #expect(rows.count == expected.count, "\(id)")
            for (row, want) in zip(rows, expected) {
                #expect(NSDictionary(dictionary: row).isEqual(to: want), "\(id): \(row) != \(want)")
            }
        }
    }

    @Test func lateMetadataStaysInOriginalReportAttribution() throws {
        var ledger = ReportLedger()
        var response = ReportLedgerTests.record("late", start: ReportLedgerTests.base - 1000, end: ReportLedgerTests.base,
                                               output: 20, final: false)
        ledger.count(response, modelId: "m")
        let baseline = ledger.counters
        response.modes = ExecutionModeAdapters.claude("fast")
        response.usage.output = 100
        response.isFinal = true
        ledger.grow(response, modelId: "m")
        let delta = ledger.counters.since(baseline)
        #expect(delta.total.requests == 0 && delta.total.output == 80)
        #expect(delta.byMode.isEmpty)
        let groups = try #require(ReportModelBreakdown.modes([.claudeCode: delta.total], groups: [:]))
        let rows = try #require(try JSONSerialization.jsonObject(with: groups.compactData) as? [[String: Any]])
        #expect(rows.first?.string("mode") == "unknown")
        #expect(rows.first?.int("measuredOutput") == 100 && rows.first?.int("measuredMs") == 1000)
        #expect(response.modes.attribution(for: .serviceTier).mode == "fast")
    }

    @Test func effortIsReportedOnlyWithVersionTwoAndSpeedNever() throws {
        var ledger = ReportLedger()
        var response = ReportLedgerTests.record("effort", start: ReportLedgerTests.base - 1000, end: ReportLedgerTests.base,
                                               output: 50, final: true)
        response.modes = ExecutionModeAdapters.claude("fast")
        response.modes.insert(try #require(ExecutionModeAdapters.effort("ultracode", evidence: .recorded, scope: .request)))
        ledger.count(response, modelId: "m")
        // The ledger freezes the effort with the service tier, whatever the service negotiates later.
        #expect(Set(ledger.counters.byMode.keys.map(\.attribution.kind)) == [.serviceTier, .effort])
        var groups = ledger.counters.byMode.reduce(into: [ReportModeGroup: ReportUsage]()) {
            $0[ReportModeGroup(source: $1.key.model.source, attribution: $1.key.attribution)] = $1.value
        }
        groups[ReportModeGroup(source: .claudeCode, attribution: .init(kind: .speed, mode: "fast", evidence: "served"))] =
            ledger.counters.total
        func kinds(_ version: Int) throws -> [String: String] {
            let rows = try #require(ReportModelBreakdown.modes([.claudeCode: ledger.counters.total], groups: groups, version: version))
            let decoded = try #require(try JSONSerialization.jsonObject(with: rows.compactData) as? [[String: Any]])
            return Dictionary(uniqueKeysWithValues: decoded.compactMap { row in row.string("kind").map { ($0, row.string("mode") ?? "") } })
        }
        #expect(try kinds(1) == ["serviceTier": "fast"])
        #expect(try kinds(2) == ["serviceTier": "fast", "effort": "ultracode"])
    }

    @Test func completeNativeAndUnknownBreakdownsPreserveOriginalModelIDs() throws {
        var counters = ReportCounters()
        let claude = ReportModelKey(modelId: "m", source: .claudeCode)
        let codex = ReportModelKey(modelId: "m", source: .codex)
        counters.add(ReportUsage(requests: 2, output: 200, measuredOutput: 200, measuredMs: 1000), to: claude,
                     modes: ExecutionModeAdapters.claude("fast").reportedAttributions)
        counters.add(ReportUsage(requests: 1, output: 80, measuredOutput: 0, measuredMs: 0), to: codex)
        let content = ReportWindowContent(usage: counters, agentBytes: [:], peak: .zero, peakCost: 0, peakRate: nil)
        let sample = ReportSample()
        var options = ReportOptions(sections: [.models], extensions: .init(modelHarnessRequests: true, executionModes: true))
        let legacy = ReportBody.json(ReportBodyTests.envelope, content, sample, .init(sections: [.models]))
        let json = ReportBody.json(ReportBodyTests.envelope, content, sample, options)
        let models = try #require(try JSONSerialization.jsonObject(with: json.compactData) as? [String: Any])["models"] as? [[String: Any]]
        let model = try #require(models?.first)
        #expect(models?.count == 1 && model.string("id") == "m")
        #expect((model["harnessRequests"] as? [[String: Any]])?.reduce(0) { $0 + $1.int("requests") } == model.int("requests"))
        let rows = try #require(model["modeUsage"] as? [[String: Any]])
        #expect(rows.count == 2)
        for field in ["requests", "output", "measuredOutput", "measuredMs"] {
            #expect(rows.reduce(0) { $0 + $1.int(field) } == model.int(field))
        }
        #expect(rows.contains { $0.string("mode") == "unknown" && $0.string("source") == "codex" })
        options.sections = []
        #expect(ReportBody.json(ReportBodyTests.envelope, content, sample, options)["models"] == nil)
        options.sections = [.models]
        options.bodyLimit = legacy.compactData.count + 2
        let fitted = ReportBody.fitted(ReportBodyTests.envelope, content, sample, options)
        #expect(fitted.data == legacy.compactData)
    }

    @Test func tooManyOrInvalidPartsAreOmittedCompletely() throws {
        var sources: [SourceKind: ReportUsage] = [:]
        for kind in SourceKind.allCases.prefix(9) { sources[kind] = ReportUsage(requests: 1) }
        #expect(ReportModelBreakdown.harness(sources, total: .init(requests: 9)) == nil)
        var groups: [ReportModeGroup: ReportUsage] = [:]
        for index in 0..<65 {
            let key = ReportModeGroup(source: .codex, attribution: .init(kind: .serviceTier, mode: "tier\(index)", evidence: "served", provider: "openai"))
            groups[key] = ReportUsage(requests: 1)
        }
        #expect(ReportModelBreakdown.modes([.codex: .init(requests: 65)], groups: groups) == nil)
        let bad = ReportModeGroup(source: .codex, attribution: .init(kind: .serviceTier, mode: "fast", evidence: "configured"))
        #expect(ReportModelBreakdown.modes([.codex: .init(requests: 1)], groups: [bad: .init(requests: 1)]) == nil)
    }
}
