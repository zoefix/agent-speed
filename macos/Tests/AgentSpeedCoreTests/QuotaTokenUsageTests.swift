import Foundation
import Testing
@testable import AgentSpeedCore

struct QuotaTokenUsageTests {
    @Test func rawCountersSurviveDeduplicationAndHistoryWithoutLeakingAnUnknownModel() throws {
        let path = "/fixture/local-rollout.jsonl"
        var deduper = Deduper()
        let (_, updates) = ParserBehaviorTests.run(.codex, [
            #"{"timestamp":"2026-11-06T06:00:00Z","type":"turn_context","payload":{"model":"unlisted-internal-model"}}"#,
            #"{"timestamp":"2026-11-06T06:00:10Z","type":"token_usage_record","payload":{"response_id":"response-1","usage":{"input_tokens":50,"cached_input_tokens":20,"output_tokens":10,"reasoning_output_tokens":3}}}"#,
            #"{"timestamp":"2026-11-06T06:00:10Z","type":"token_usage_record","payload":{"response_id":"response-1","usage":{"input_tokens":50,"cached_input_tokens":20,"output_tokens":10,"reasoning_output_tokens":3}}}"#,
            #"{"timestamp":"2026-11-06T06:00:40Z","type":"token_usage_record","payload":{"response_id":"response-2","usage":{"output_tokens":10}}}"#,
        ], path: path, deduper: &deduper)
        #expect(updates.count == 2)
        let first = try #require(updates.first?.record)
        #expect(first.quotaTokenCounts == QuotaTokenCounts(input: 50, output: 10, cachedInput: 20,
                                                          cacheWriteInput: nil, reasoning: 3))
        #expect(updates.last?.record.quotaTokenCounts == nil, "missing input must not be labelled measured zero")
        let pricing = PriceBook(), models = QuotaModelIds(pricing: pricing)
        var labeler = DayLabeler(timeZone: TimeZone(identifier: "UTC")!)
        let line = QuotaEstimateFeedTests.line(first.endMs + 1, file: path)
        let file = QuotaEstimateFile.make(records: [first], lines: [line], pricing: pricing, models: models,
                                          labeler: &labeler, nowMs: first.endMs + 5)
        let decoded = try #require(QuotaEstimateFile.decode(file.encoded(), file: path))
        let row = try #require(decoded.spend.first)
        #expect(row.tokenContribution == file.spend.first?.tokenContribution)
        #expect(row.model == "unlisted-internal-model", "keep existing private account/model filtering intact")
        let contribution = try #require(row.tokenContribution)
        #expect(contribution.model == nil, "raw private names must not reach the report")
        let ledger = QuotaEstimateLedger()
        ledger.add(account: "account-a", source: row.source, model: row.model, endMs: row.endMs, cost: nil,
                   tokens: row.tokens, tokenContribution: contribution)
        let detail = try #require(ledger.tokenUsage(account: "account-a", name: nil, start: row.endMs - 1, end: row.endMs))
        #expect(detail.models == [.init(id: nil, input: 50, output: 10)])
        #expect(detail.json["cacheWriteInputTokens"] == .null)
        #expect(ledger.tokenUsage(account: "account-a", name: nil, start: row.endMs + 1, end: row.endMs + 2) == nil,
                "the start minute contains usage from before the reset")
        ledger.add(account: "account-a", source: row.source, model: row.model, endMs: row.endMs, cost: nil, tokens: 10)
        #expect(ledger.tokenUsage(account: "account-a", name: nil, start: row.endMs - 1, end: row.endMs) == nil,
                "legacy or unmeasured rows must not yield a partial partition")
    }

    @Test func twoAccountWindowsStaySeparateAndNegotiateTheOptionalMember() throws {
        typealias F = EstimateFixtures
        let fixture = try #require(F.list(QuotaEstimatePipelineTests.pipeline["report"]).first {
            F.string($0["name"]) == "two Codex accounts"
        })
        let input = F.object(fixture["input"])
        var (inputs, salt) = try QuotaEstimatePipelineTests.inputs(input)
        for i in inputs.spend.indices {
            let end = inputs.spend[i].endMs
            let output: Int64 = i == 0 ? 300 : 200
            let counts = QuotaTokenCounts(input: 1000 - output, output: output, cachedInput: 100,
                                         cacheWriteInput: nil, reasoning: nil)
            inputs.spend[i].tokenContribution = .init(counts: counts, model: "gpt-5.5", earliestEndMs: end, latestEndMs: end)
        }
        let now = try #require(F.int(input["now"]))
        let origin = try #require(F.string(input["origin"]))
        let install = try #require(F.string(input["install"]))
        let build = QuotaEstimateBuild(inputs, salt: salt, install: install, nowMs: now)
        let entries = build.entries(nowMs: now, origin: origin, includeTokenUsage: true)
        #expect(entries.count == 2)
        #expect(entries.map(\.tokenUsage?.counts.output) == [300, 200])
        #expect(entries.map(\.usedMicroUsd) == [3_000_000, 2_000_000])
        #expect(entries.allSatisfy { $0.kind == .sevenDay && $0.tokenUsage?.models.count == 1 })
        #expect(entries.allSatisfy { $0.json["tokenUsage"] == nil })
        let (content, sample) = ReportBodyTests.busySample()
        let envelope = ReportBodyTests.envelope
        var options = ReportBodyTests.everything
        options.quotas = entries
        options.extensions = ReportExtensions(quotaEstimate: true)
        let legacy = ReportBody.fitted(envelope, content, sample, options)
        func first(_ report: ReportJSON) -> ReportJSON? {
            guard case .array(let rows) = report["quotas"] else { return nil }
            return rows.first
        }
        #expect(first(legacy.json)?["tokenUsage"] == nil)

        let negotiated = ReportExtensions(try JSONParser.parse(#"{"quotaEstimate":1,"quotaTokenUsage":1}"#))
        #expect(negotiated.quotaTokenUsage)
        #expect(!ReportExtensions(try JSONParser.parse(#"{"quotaTokenUsage":true}"#)).quotaTokenUsage)
        let roundTrip = try JSONDecoder().decode(ReportExtensions.self, from: JSONEncoder().encode(negotiated))
        #expect(roundTrip == negotiated)
        options.extensions = negotiated
        let detailed = ReportBody.fitted(envelope, content, sample, options)
        #expect(first(detailed.json)?["tokenUsage"]?["inputTokens"] == .int(700))
        if ReferencePython.available {
            let errors = try ReferencePython.strictErrors([detailed.data])
            #expect(errors.isEmpty, "\(errors)")
        }
        var noModels = options
        noModels.sections.remove(.models)
        #expect(first(ReportBody.fitted(envelope, content, sample, noModels).json)?["tokenUsage"] == nil)
        options.bodyLimit = legacy.data.count
        let reduced = ReportBody.fitted(envelope, content, sample, options)
        #expect(reduced.data == legacy.data, "drop optional counts before weakening existing quota reports")
    }
}
