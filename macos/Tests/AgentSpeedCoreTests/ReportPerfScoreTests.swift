import Foundation
import Testing
@testable import AgentSpeedCore

/// METRICS 11.10 against report-vectors.json `perfScore` (`capability`, `assembly`, `ledger`), run through the app's own
/// discovery parser, body builder, completion log, statistics ledger and tracker, and the four example bodies rebuilt
/// exactly.
@Suite struct ReportPerfScoreTests {
    static var vectors: [String: Any] {
        ((try? Fixtures.json("report-vectors.json"))?["perfScore"] as? [String: Any]) ?? [:]
    }

    static func cases(_ group: String) -> [[String: Any]] { vectors[group] as? [[String: Any]] ?? [] }
    static func names(_ group: String) -> [String] { cases(group).compactMap { $0.string("name") } }
    static func spec(_ group: String, _ name: String) throws -> [String: Any] {
        try #require(cases(group).first { $0.string("name") == name })
    }

    @Test func everyGroupIsInTheFixture() {
        #expect(Self.cases("capability").count == 11)
        #expect(Self.cases("assembly").count == 4)
        #expect(Self.cases("ledger").count == 4)
        #expect((Self.vectors["edges"] as? [NSNumber])?.map(\.int64Value) == PerfLatency.edges)
    }

    // MARK: - Capability

    @Test(arguments: names("capability"))
    func capability(_ name: String) throws {
        let spec = try Self.spec("capability", name)
        var document: [String: Any] = [
            "protocol": "agent-speed-report", "versions": [1],
            "service": ["name": "x", "terms": "https://example.com/t", "privacy": "https://example.com/p"],
            "api": "https://api.example.com",
        ]
        for (key, value) in try #require(spec["document"] as? [String: Any]) { document[key] = value }
        let data = try JSONSerialization.data(withJSONObject: document)
        let service = try ReportServiceURL.parse("https://example.com").get()
        let discovery = try ReportDiscovery.parse(data, service: service).get()
        let switches: Set<ReportSection> = spec["modelsSwitch"] as? Bool == true
            ? Set(ReportSection.allCases) : Set(ReportSection.allCases).subtracting([.models])
        let sent: String? = ReportPerfScoreRule.isActive(discovery, switches: switches) ? "perfScore"
            : ReportPerfScoreRule.isLegacyActive(discovery, switches: switches) ? "taskScore" : nil
        #expect(sent == spec.string("expect"), "\(name)")
    }

    // MARK: - Rows

    static func source(_ value: [String: Any]) throws -> SourceKind {
        try #require(SourceKind(rawValue: value.string("source") ?? ""))
    }

    static func object(_ json: ReportJSON) throws -> Any {
        try JSONSerialization.jsonObject(with: json.compactData, options: .fragmentsAllowed)
    }

    static func same(_ lhs: Any, _ rhs: Any) -> Bool {
        NSArray(array: [lhs]).isEqual(to: [rhs])
    }

    /// A row in the sent form (sparse buckets) as the ledger holds it.
    static func row(_ value: [String: Any]) throws -> PerfRecord {
        var row = PerfRecord()
        row.n = value.int("n")
        row.outputSum = value.int64("outputSum") ?? 0
        row.durationMsSum = value.int64("durationMsSum") ?? 0
        for pair in try #require(value["latencyBins"] as? [[Int]]) where pair.count == 2 {
            row.latencyBins[pair[0]] += pair[1]
        }
        row.reasonN = value.int("reasonN")
        row.reasonOutput = value.int64("reasonOutput") ?? 0
        row.reasonDurationMsSum = value.int64("reasonDurationMsSum") ?? 0
        row.cacheN = value.int("cacheN")
        row.cacheInput = value.int64("cacheInput") ?? 0
        row.cacheCached = value.int64("cacheCached") ?? 0
        row.toolN = value.int("toolN")
        row.toolLnSum = value.double("toolLnSum") ?? 0
        row.toolFailed = value.int("toolFailed")
        row.activeMs = value.int64("activeMs") ?? 0
        return row
    }

    // MARK: - Assembly

    /// The case's agents, thinking, models of 11.3 and statistics ledger as the engine and the controller hand them to
    /// the body builder.
    static func assembly(_ input: [String: Any]) throws -> (ReportWindowContent, ReportSample, ReportOptions) {
        var counters = ReportCounters()
        var generating: [ReportModelKey: Int] = [:]
        var agents: [SourceKind: ReportAgentCounts] = [:]
        for model in try #require(input["models"] as? [[String: Any]]) {
            let id = try #require(model.string("id"))
            let sources = try #require(model["sources"] as? [String]).map { try #require(SourceKind(rawValue: $0)) }
            let source = try #require(sources.first)
            counters.add(ReportUsage(requests: Int64(model.int("requests")), output: Int64(model.int("output")),
                                     measuredOutput: Int64(model.int("measuredOutput")),
                                     measuredMs: Int64(model.int("measuredMs"))),
                         to: ReportModelKey(modelId: id, source: source))
            if model.int("generating") > 0 {
                generating[ReportModelKey(modelId: id, source: source)] = model.int("generating")
                agents[source, default: .zero].generating += model.int("generating")
                agents[source, default: .zero].active += model.int("generating")
            }
        }
        let thinking = try #require(input["thinking"] as? [String: Any])
        var perf = ReportPerfFacts(thinking: try #require(thinking.double("total")))
        for (id, value) in thinking["models"] as? [String: Double] ?? [:] {
            perf.models[id, default: ReportPerfModel()].thinking = value
        }
        for agent in try #require(input["agents"] as? [[String: Any]]) {
            let id = try #require(agent.string("model"))
            let form = try #require(PerfValueForm(rawValue: agent.string("form") ?? ""))
            let agentId: String = try #require(agent.string("id"))
            let value = PerfSetValue(form: form, score: agent.double("score"), agentId: agentId, lastLineMs: nil)
            perf.value.merge(value)
            perf.models[id, default: ReportPerfModel()].value.merge(value)
            if form == .live { perf.models[id]?.liveSources.insert(try Self.source(agent)) }
        }
        let sample = ReportSample(facts: ReportLiveFacts(agents: agents, generatingModels: generating, counters: counters,
                                                         perf: perf))
        var content = ReportWindow(ReportSample()).content(sample)
        for (id, rows) in input["ledger"] as? [String: [[String: Any]]] ?? [:] {
            for row in rows { content.perfStats[id, default: [:]][try Self.source(row)] = try Self.row(row) }
        }
        let legacy = input.string("extension") == "taskScore"
        let options = ReportOptions(sections: [.models], maxModels: input.int("maxModels"),
                                    extensions: ReportExtensions(taskScore: legacy, perfScore: !legacy))
        return (content, sample, options)
    }

    static let envelope = ReportEnvelope(
        client: ReportClientInfo(version: "1.3.0", osMajor: 27), deviceId: "6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b",
        username: "zoe-mbp", runId: "c3d4e5f6-a7b8-4c9d-8e0f-1a2b3c4d5e6f", seq: 42, sentAtMs: 1_792_656_930_500,
        windowMs: 5000, activity: .active, titleLevel: 8)

    @Test(arguments: names("assembly"))
    func assembly(_ name: String) throws {
        let spec = try Self.spec("assembly", name)
        let (content, sample, options) = try Self.assembly(try #require(spec["input"] as? [String: Any]))
        let body = try #require(try Self.object(ReportBody.json(Self.envelope, content, sample, options)) as? [String: Any])
        let expected = try #require(spec["expect"] as? [String: Any])
        let key = expected["taskScore"] != nil ? "taskScore" : "perfScore"
        #expect(Self.same(try #require(body[key]), try #require(expected[key])), "\(name): \(body[key] ?? "")")
        #expect(Self.same(try #require(body["models"]), try #require(expected["models"])), "\(name): \(body["models"] ?? "")")
        #expect(body[key == "perfScore" ? "taskScore" : "perfScore"] == nil, "\(name): only one of the two")
    }

    /// The values the WebSocket changed rule compares are those the body carries.
    @Test(arguments: names("assembly"))
    func changedRuleValuesMatchTheBody(_ name: String) throws {
        let spec = try Self.spec("assembly", name)
        let (content, sample, options) = try Self.assembly(try #require(spec["input"] as? [String: Any]))
        let values = try #require(ReportBody.perfScoreValues(content, sample, options))
        let expected = try #require(spec["expect"] as? [String: Any])
        let key = values.legacy ? "taskScore" : "perfScore"
        for entry in try #require(expected["models"] as? [[String: Any]]) {
            let id = try #require(entry.string("id"))
            let value = try #require(values.entries[id])
            #expect(Self.same(try Self.object(value.json), try #require(entry[key])), "\(name)")
        }
        #expect(Self.same(try Self.object(values.top.json), try #require(expected[key])), "\(name)")
        var inactive = options
        inactive.extensions.perfScore = false
        inactive.extensions.taskScore = false
        #expect(ReportBody.perfScoreValues(content, sample, inactive) == nil)
    }

    @Test func theChangedRuleComparesTheObjectsTheReportWouldCarry() {
        func value(_ score: Double, _ thinking: Double) -> ReportPerfScoreValue {
            ReportPerfScoreValue(PerfSetValue(form: .live, score: score, agentId: "a", lastLineMs: nil), thinking: thinking)
        }
        func values(top: ReportPerfScoreValue, _ entries: [String: ReportPerfScoreValue], legacy: Bool = false)
            -> ReportStreamValues {
            ReportStreamValues(ReportSample(), perfScore: ReportPerfScoreValues(top: top, entries: entries, legacy: legacy))
        }
        let last = values(top: value(1280, 1.04), ["a": value(1280, 1.04), "b": value(640, 0)])
        #expect(values(top: value(1280, 1.04), [:]).changed(since: nil))
        #expect(!values(top: value(1280.2, 1.01), ["a": value(1279.9, 0.96)]).changed(since: last),
                "rounded as sent; an entry no longer carried changes nothing")
        #expect(values(top: value(1280, 1.06), ["a": value(1280, 1.04)]).changed(since: last), "thinking 1.0 → 1.1")
        #expect(values(top: value(1280, 1.04), ["a": value(1281, 1.04)]).changed(since: last), "a tool result moved it")
        #expect(values(top: value(1280, 1.04), ["c": value(10, 0)]).changed(since: last), "a newly listed model")
        #expect(values(top: value(1280, 1.04), [:]).changed(since: ReportStreamValues(ReportSample())), "turned on")
        #expect(values(top: value(1280, 1.04), [:], legacy: true).changed(since: last), "the other extension")
        #expect(!ReportStreamValues(ReportSample()).changed(since: last), "inactive: nothing of the extension")
    }

    // MARK: - Ledger

    /// The `ledger` events through the engine's counting rules, its completion log and the tracker (controller).
    struct LedgerRun {
        var tracker = ReportPerfTracker()
        var log = ReportPerfCompletionLog()
        var watchOn = false
        var builds: [[String: Any]] = []

        mutating func apply(_ event: [String: Any]) throws {
            let at = try #require(event.int64("at"))
            if let on = event["watch"] as? Bool {
                watchOn = on
                tracker.watch(on ? "service" : nil)
            } else if let response = event["response"] as? [String: Any] {
                // The engine logs only what the report ledger counted (11.4), in the current watch's epoch.
                guard event["counted"] as? Bool ?? true, let epoch = tracker.epoch else { return }
                let record = try ReportPerfScoreTests.response(response)
                log.append(epoch: epoch, agentId: try #require(response.string("agent")),
                           modelId: try #require(response.string("model")), source: record.source,
                           kind: .response(startMs: record.startMs ?? 0, endMs: record.endMs, firstMs: record.firstMs,
                                           output: record.usage.output, reasoning: record.usage.reasoning,
                                           input: record.usage.input, cachedInput: record.usage.cachedInput))
                tracker.ingest(log)
            } else if let call = event["call"] as? [String: Any] {
                let completion = ToolCompletion(
                    callMs: try #require(call.int64("callMs")), resultMs: try #require(call.int64("resultMs")),
                    failed: call["failed"] as? Bool, model: call.string("model"),
                    seeded: event["seeding"] as? Bool ?? false)
                guard let epoch = tracker.epoch, ReportPerfScoreRule.countsCall(completion, nowMs: at) else { return }
                log.append(epoch: epoch, agentId: try #require(call.string("agent")),
                           modelId: try #require(call.string("model")), source: try ReportPerfScoreTests.source(call),
                           kind: .call(callMs: completion.callMs, resultMs: completion.resultMs, failed: completion.failed))
                tracker.ingest(log)
            } else if let seq = event["build"] as? Int {
                tracker.ingest(log)
                let taken = tracker.take()
                builds.append(["seq": seq,
                               "perfStats": event["models"] as? Bool == false ? NSNull() : try Self.json(taken)])
            } else if event["relaunch"] != nil {
                self = LedgerRun(builds: builds)
            }
            // `discard` and `newRun` leave the ledger as it is.
        }

        static func json(_ stats: ReportPerfStats) throws -> [String: Any] {
            var out: [String: Any] = [:]
            for (id, rows) in stats {
                out[id] = try SourceKind.allCases.compactMap { source in
                    try rows[source].map { try ReportPerfScoreTests.object($0.reportJSON(source: source)) }
                }
            }
            return out
        }
    }

    static func response(_ value: [String: Any]) throws -> ResponseRecord {
        ResponseRecord(key: "r", source: try source(value), agentId: value.string("agent") ?? "",
                       model: value.string("model"), startMs: value.int64("startMs"),
                       endMs: try #require(value.int64("endMs")),
                       usage: TokenUsage(input: value.int("input"), cachedInput: value.int("cachedInput"),
                                         output: value.int("output"), reasoning: value.int("reasoning")),
                       firstMs: value.int64("firstMs"))
    }

    @Test(arguments: names("ledger"))
    func ledger(_ name: String) throws {
        let spec = try Self.spec("ledger", name)
        var run = LedgerRun()
        for event in try #require(spec["events"] as? [[String: Any]]) { try run.apply(event) }
        let expected = try #require(spec["expect"] as? [String: Any])
        #expect(Self.same(run.builds, try #require(expected["builds"])), "\(name): \(run.builds)")
        let pending = try LedgerRun.json(run.tracker.take())
        #expect(Self.same(pending, try #require(expected["pending"])), "\(name)")
    }

    @Test func aCompletionOfAnotherWatchIsIgnoredAndALogIsReadOnce() {
        var tracker = ReportPerfTracker()
        var log = ReportPerfCompletionLog()
        let started = tracker.watch("a")
        let again = tracker.watch("a")
        #expect(started && !again)
        let epoch = tracker.epoch!
        let call = ReportPerfCompletion.Kind.call(callMs: 0, resultMs: 1000, failed: false)
        log.append(epoch: epoch, agentId: "x", modelId: "gpt-5.5", source: .codex, kind: call)
        log.append(epoch: epoch + 7, agentId: "x", modelId: "gpt-5.5", source: .codex, kind: call)
        let received = tracker.ingest(log)
        let repeated = tracker.ingest(log)
        #expect(received && !repeated, "read once")
        #expect(tracker.ledger.calls == 1)
        let switched = tracker.watch("b")
        #expect(switched, "another service: a break, then a new watch")
        #expect(tracker.ledger.isEmpty && tracker.epoch == epoch + 1)
        var next = ReportPerfCompletionLog()
        next.append(epoch: epoch + 1, agentId: "x", modelId: "gpt-5.5", source: .codex, kind: call)
        let fresh = tracker.ingest(next)
        #expect(fresh, "a new engine's log starts over")
    }

    @Test func theLedgerDropsBeyondItsCaps() {
        var ledger = ReportPerfLedger()
        let call = ReportPerfCompletion(index: 1, epoch: 1, agentId: "x", modelId: "m", source: .codex,
                                        kind: .call(callMs: 0, resultMs: 10, failed: nil))
        for _ in 0..<ReportPerfLedger.maxCalls { ledger.add(call) }
        let dropped = !ledger.add(call)
        #expect(dropped, "the 100 001st call is dropped")
        #expect(ledger.rows["m"]?[.codex]?.toolN == ReportPerfLedger.maxCalls)
        _ = ledger.take()
        let added = ledger.add(call)
        #expect(added, "a build makes room")
    }

    // MARK: - Examples

    /// The spec's active example with the performance score of METRICS 11.10.
    static func exampleInputs() -> (ReportWindowContent, ReportSample) {
        var counters = ReportCounters()
        counters.add(ReportUsage(requests: 3, output: 1500, input: 800_000, cachedInput: 780_000, cacheWriteInput: 12_050,
                                 measuredOutput: 1400, measuredMs: 3300, costMicroUsd: 18_003),
                     to: ReportModelKey(modelId: "claude-opus-5-5", source: .claudeCode))
        counters.add(ReportUsage(requests: 1, output: 340, input: 112_400, cachedInput: 100_120, reasoning: 310,
                                 measuredOutput: 302, measuredMs: 820, costMicroUsd: 3000),
                     to: ReportModelKey(modelId: "gpt-5.5", source: .codex))
        func value(_ id: String, _ form: PerfValueForm, _ score: Double) -> PerfSetValue {
            PerfSetValue(form: form, score: score, agentId: id, lastLineMs: nil)
        }
        let perf = ReportPerfFacts(
            value: value("a1", .live, 1286.2), thinking: 38.64,
            models: ["claude-opus-5-5": ReportPerfModel(value: value("a1", .live, 1286.2), liveSources: [.claudeCode]),
                     "gpt-5.5": ReportPerfModel(value: value("a3", .live, 802.4), thinking: 38.64, liveSources: [.codex]),
                     "claude-sonnet-5-5": ReportPerfModel(value: value("a6", .resting, 940))])
        let facts = ReportLiveFacts(
            agents: [.codex: ReportAgentCounts(generating: 1, active: 2),
                     .claudeCode: ReportAgentCounts(generating: 2, tools: 1, active: 3)],
            generatingModels: [ReportModelKey(modelId: "claude-opus-5-5", source: .claudeCode): 2,
                               ReportModelKey(modelId: "gpt-5.5", source: .codex): 1],
            counters: counters, perf: perf)
        let sample = ReportSample(
            live: Rate(output: 412.6, total: 182_340.2), estimatedShare: 0.215,
            liveCost: LiveCost(microUsdPerSecond: 4213, knownOutput: 412.6),
            sources: [
                .codex: .init(live: Rate(output: 112.2, total: 32_120.1), liveEstimated: Rate(output: 52.7, total: 0),
                              liveCost: LiveCost(microUsdPerSecond: 613, knownOutput: 112.2)),
                .claudeCode: .init(live: Rate(output: 300.4, total: 150_220.1), liveEstimated: Rate(output: 36, total: 0),
                                   liveCost: LiveCost(microUsdPerSecond: 3600, knownOutput: 300.4)),
            ],
            facts: facts,
            traffic: ReportTrafficSample(
                status: .ok, includesTools: true, rate: TrafficRate(rx: 24_310, tx: 1288),
                sourceRates: [.codex: TrafficRate(rx: 4200, tx: 288), .claudeCode: TrafficRate(rx: 20_110, tx: 1000)],
                totals: TrafficReportTotals(sources: [.codex: TrafficSplit(own: TrafficBytes(rx: 21_000, tx: 1440)),
                                                      .claudeCode: TrafficSplit(own: TrafficBytes(rx: 100_550, tx: 5000))])))
        var content = ReportWindow(ReportSample(traffic: ReportTrafficSample(status: .ok))).content(sample)
        content.peak = Rate(output: 655, total: 240_118.7)
        content.peakCost = 6120
        content.peakRate = TrafficRate(rx: 61_200, tx: 3410)
        var opus = PerfRecord()
        (opus.n, opus.outputSum, opus.durationMsSum) = (3, 1400, 3300)
        opus.latencyBins[22] = 1
        opus.latencyBins[24] = 2
        (opus.cacheN, opus.cacheInput, opus.cacheCached) = (3, 800_000, 780_000)
        (opus.toolN, opus.toolLnSum, opus.activeMs) = (4, 24.9999321, 7420)
        var gpt = PerfRecord()
        (gpt.n, gpt.outputSum, gpt.durationMsSum) = (1, 302, 820)
        gpt.latencyBins[6] = 1
        (gpt.reasonN, gpt.reasonOutput, gpt.reasonDurationMsSum) = (1, 302, 820)
        (gpt.cacheN, gpt.cacheInput, gpt.cacheCached, gpt.activeMs) = (1, 112_400, 100_120, 820)
        var sonnet = PerfRecord()
        (sonnet.toolN, sonnet.toolLnSum, sonnet.toolFailed, sonnet.activeMs) = (1, 7.7406644, 1, 2300)
        content.perfStats = ["claude-opus-5-5": [.claudeCode: opus], "gpt-5.5": [.codex: gpt],
                             "claude-sonnet-5-5": [.claudeDesktop: sonnet]]
        return (content, sample)
    }

    /// The example file as compact text, with the floats Python writes as `0.0` and `1.0` in the client's form.
    static func exampleText(_ name: String) throws -> String {
        let data = try Data(contentsOf: Fixtures.root.appendingPathComponent("report-examples/\(name).json"))
        var compact = ""
        var inString = false
        var escaped = false
        for character in String(decoding: data, as: UTF8.self) {
            if inString {
                compact.append(character)
                if escaped { escaped = false } else if character == "\\" { escaped = true } else if character == "\"" { inString = false }
            } else if character == "\"" {
                inString = true
                compact.append(character)
            } else if !character.isWhitespace {
                compact.append(character)
            }
        }
        return compact.replacingOccurrences(of: #"(?<=[:\[,]-?\d{1,20})\.0(?=[,}\]])"#, with: "", options: .regularExpression)
    }

    static func body(extensions: ReportExtensions, websocket: Bool) -> String {
        let (content, sample) = exampleInputs()
        var envelope = Self.envelope
        var options = ReportOptions(sections: Set(ReportSection.allCases), metric: .output, redline: 2000,
                                    extensions: extensions)
        if websocket {
            envelope.realtime = ReportRealtime(observationAgeMs: 18)
            options.messageLimit = ReportStreamSettings.standard.maxMessageBytes
        }
        return String(decoding: ReportBody.data(envelope, content, sample, options), as: UTF8.self)
    }

    @Test func theHTTPExampleIsRebuiltExactly() throws {
        #expect(Self.body(extensions: ReportExtensions(perfScore: true), websocket: false)
                == (try Self.exampleText("report-perfscore")))
    }

    @Test func theWebSocketExampleIsRebuiltExactly() throws {
        #expect(Self.body(extensions: ReportExtensions(perfScore: true), websocket: true)
                == (try Self.exampleText("report-perfscore-websocket")))
    }

    /// A service that still advertises only the retired task score gets the thinking speed in its shape.
    @Test func theLegacyExamplesAreRebuiltExactly() throws {
        #expect(Self.body(extensions: ReportExtensions(taskScore: true), websocket: false)
                == (try Self.exampleText("report-taskscore")))
        #expect(Self.body(extensions: ReportExtensions(taskScore: true), websocket: true)
                == (try Self.exampleText("report-taskscore-websocket")))
        #expect(Self.body(extensions: ReportExtensions(taskScore: true, perfScore: true), websocket: false)
                == (try Self.exampleText("report-perfscore")), "both advertised: only perfScore")
    }

    // MARK: - Body rules

    @Test func inactiveReportsCarryNothingOfTheExtension() throws {
        let (content, sample) = Self.exampleInputs()
        for options in [
            ReportOptions(sections: Set(ReportSection.allCases)),
            ReportOptions(sections: [.sources, .cost], extensions: ReportExtensions(taskScore: true, perfScore: true)),
            ReportOptions(sections: Set(ReportSection.allCases), maxModels: 0,
                          extensions: ReportExtensions(taskScore: true, perfScore: true)),
        ] {
            let text = String(decoding: ReportBody.data(Self.envelope, content, sample, options), as: UTF8.self)
            #expect(!text.contains("perfScore") && !text.contains("perfStats") && !text.contains("taskScore"))
            #expect(!text.contains("claude-sonnet-5-5"), "a model only the extension lists")
        }
    }

    /// The size rule drops the per-model breakdowns first, then `models` with its performance fields; the top-level
    /// object stays.
    @Test func theTopLevelObjectSurvivesTheSizeRule() throws {
        let (content, sample) = Self.exampleInputs()
        var options = ReportOptions(sections: Set(ReportSection.allCases), metric: .output, redline: 2000,
                                    extensions: ReportExtensions(modelHarnessRequests: true, executionModes: true,
                                                                 perfScore: true))
        let full = ReportBody.data(Self.envelope, content, sample, options)
        let fullBody = try #require(try JSONSerialization.jsonObject(with: full) as? [String: Any])
        #expect((fullBody["models"] as? [[String: Any]])?.first?["harnessRequests"] != nil)
        options.bodyLimit = full.count - 1
        let trimmed = try #require(try JSONSerialization.jsonObject(
            with: ReportBody.data(Self.envelope, content, sample, options)) as? [String: Any])
        let models = try #require(trimmed["models"] as? [[String: Any]])
        #expect(models.first?["harnessRequests"] == nil && models.first?["modeUsage"] == nil)
        #expect(models.allSatisfy { $0["perfScore"] != nil && $0["perfStats"] != nil })
        options.bodyLimit = 2000
        let small = try #require(try JSONSerialization.jsonObject(
            with: ReportBody.data(Self.envelope, content, sample, options)) as? [String: Any])
        #expect(small["models"] == nil)
        #expect(Self.same(try #require(small["perfScore"]), ["score": 1286, "form": "live", "thinkingTokS": 38.6]))
        let keys = ReportBody.fitted(Self.envelope, content, sample, options).json
        if case .object(let members) = keys { #expect(members.last?.key == "perfScore") }
    }

    /// An entry that only the extension lists has zero usage: an empty Harness breakdown and no mode rows.
    @Test func anEntryOfTheExtensionHasNoUsageBreakdown() throws {
        let (content, sample) = Self.exampleInputs()
        let options = ReportOptions(sections: [.models], extensions: ReportExtensions(
            modelHarnessRequests: true, executionModes: true, perfScore: true))
        let body = try #require(try JSONSerialization.jsonObject(
            with: ReportBody.data(Self.envelope, content, sample, options)) as? [String: Any])
        let sonnet = try #require((body["models"] as? [[String: Any]])?.first { $0["id"] as? String == "claude-sonnet-5-5" })
        #expect((sonnet["harnessRequests"] as? [Any])?.isEmpty ?? true)
        #expect(sonnet["modeUsage"] == nil)
    }

    /// A score above 10 000 000, which only a broken log could give, is sent as 10 000 000.
    @Test func aScoreIsCappedAsSent() {
        let value = ReportPerfScoreValue(PerfSetValue(form: .live, score: 3e7, agentId: "a", lastLineMs: nil), thinking: 0)
        #expect(value.score == 10_000_000 && value.form == .live)
        let none = ReportPerfScoreValue(.empty, thinking: 12.34)
        #expect(none.score == nil && none.form == .none && none.thinkingTokS == 12.3)
    }

    /// Bodies with the extension pass the reference's schema, its perfScore and taskScore checks and its conformance
    /// rules.
    @Test(.enabled(if: ReferencePython.available)) func referenceAcceptsTheBodies() throws {
        var bodies: [Data] = []
        let (content, sample) = Self.exampleInputs()
        for maxModels in [16, 2, 1] {
            for extensions in [ReportExtensions(perfScore: true), ReportExtensions(taskScore: true),
                               ReportExtensions(modelHarnessRequests: true, executionModes: true, perfScore: true)] {
                let options = ReportOptions(sections: Set(ReportSection.allCases), maxModels: maxModels,
                                            extensions: extensions)
                bodies.append(ReportBody.data(Self.envelope, content, sample, options))
            }
        }
        var small = ReportOptions(sections: Set(ReportSection.allCases), extensions: ReportExtensions(perfScore: true))
        small.bodyLimit = 1500
        bodies.append(ReportBody.data(Self.envelope, content, sample, small))
        let failures = try ReferencePython.strictErrors(bodies)
        #expect(failures.isEmpty, "\(failures)")
    }
}
