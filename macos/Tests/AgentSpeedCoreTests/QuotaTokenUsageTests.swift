import Foundation
import Testing
@testable import AgentSpeedCore

/// The token counts of each quota entry's own spend (QUOTAS 10.3 *Token counts*, REPORTING-EXTENSIONS 12): what each
/// source reports, the sums the fixtures give, the history's stored form, and the `tokenUsage` member of reports.
struct QuotaTokenUsageTests {
    typealias F = EstimateFixtures

    static var cases: [String: Any] { F.object(F.cases["tokenUsage"]) }

    static func json(_ usage: QuotaTokenUsage?) -> Any {
        usage.map { F.foundation($0.json) } ?? NSNull()
    }

    // MARK: Fixtures

    @Test func eachSourceKeepsTheDetailsItsLogsReport() {
        let sources = F.list(Self.cases["sources"])
        #expect(sources.count == 7)
        for c in sources {
            let u = F.object(c["usage"])
            let usage = TokenUsage(input: Int(F.int(u["input"])!), cachedInput: Int(F.int(u["cachedInput"])!),
                                   cacheWriteInput: Int(F.int(u["cacheWriteInput"])!), output: Int(F.int(u["output"])!),
                                   reasoning: Int(F.int(u["reasoning"])!))
            let got = QuotaTokenCounts(source: F.string(c["source"])!, usage: usage)
            #expect(got == F.counts(c["expected"]), "\(c["why"] ?? "")")
        }
    }

    @Test func sumsMatchTheReference() {
        let ledger = F.ledger(F.list(Self.cases["rows"]))
        let sums = F.list(Self.cases["sums"])
        #expect(sums.count == 9)
        for c in sums {
            let got = ledger.tokenUsage(Set(c["accounts"] as! [String]), F.string(c["window"]), F.int(c["a"])!,
                                        F.int(c["b"])!)
            let diff = F.differences(Self.json(got), c["expected"])
            #expect(diff.isEmpty, "\(c["name"] ?? ""): \(diff)")
        }
    }

    @Test func entriesMatchTheReference() {
        let ledger = F.ledger(F.list(Self.cases["rows"]))
        let input = F.object(Self.cases["input"])
        let estimate = F.object(input["estimate"])
        let base = QuotaEntryBase(
            account: "a", source: F.string(input["source"])!, sources: input["sources"] as? [String] ?? [],
            plan: F.string(input["plan"]), tier: F.string(input["tier"]),
            kind: QuotaWindowKind(rawValue: F.string(input["kind"])!)!, name: F.string(input["name"]),
            minutes: Int(F.int(input["minutes"])!), p: F.double(input["p"])!, t: F.int(input["t"])!,
            resetsMs: F.int(input["R"])!, start: F.int(input["S"])!,
            estimate: QuotaCapEstimate(confidence: QuotaEstimateConfidence(rawValue: F.string(estimate["confidence"])!)!,
                                       cap: F.double(estimate["cap"]), low: F.double(estimate["low"]),
                                       high: F.double(estimate["high"]), windows: Int(F.int(estimate["windows"])!)),
            firstAfterReset: nil)
        let tag = QuotaAccounts.reportTag(origin: F.string(Self.cases["origin"])!, key: F.string(input["account"])!)
        let entries = F.list(Self.cases["entries"])
        #expect(entries.count == 2)
        for c in entries {
            let entry = QuotaReportEntry(base, account: tag, nowMs: F.int(c["now"])!, ledger: ledger, priceVersion: 8)
            let diff = F.differences(F.foundation(entry.json(tokenUsage: true)), c["expected"])
            #expect(diff.isEmpty, "\(c["name"] ?? ""): \(diff)")
            #expect(F.foundation(entry.json) as? [String: Any] != nil)
            #expect((F.foundation(entry.json) as! [String: Any])["tokenUsage"] == nil, "no member without the extension")
        }
    }

    // MARK: The spend's counts

    @Test func aResponseCarriesItsCountsAndWhetherItsModelIsPublic() throws {
        let models = QuotaModelIds(pricing: PriceBook())
        let usage = TokenUsage(input: 1300, cachedInput: 1000, cacheWriteInput: 200, output: 80, reasoning: 30)
        let known = ResponseRecord(key: "a", source: .claudeCode, agentId: "/a", model: "claude-opus-5-5",
                                   startMs: 1_000, endMs: 2_000, usage: usage)
        let spend = try #require(QuotaEstimateSpend(known, cost: 5, models: models))
        #expect(spend.counts == QuotaTokenCounts(input: 1300, output: 80, cachedInput: 1000, cacheWriteInput: 200,
                                                 reasoning: nil))
        #expect(spend.publicModel && spend.model == "claude-opus-5-5")
        var unknown = known
        unknown.model = "Acme-Internal-7"
        let raw = try #require(QuotaEstimateSpend(unknown, cost: nil, models: models))
        #expect(!raw.publicModel && raw.model == "acme-internal-7", "kept locally to match, never sent")
    }

    @Test func perMinuteSumsKeepUnmeasuredAndMissingDetails() {
        let a = QuotaEstimateSpend(source: "codex", model: "gpt-5.5", startMs: 0, endMs: 1_000, costMicroUsd: 1,
                                   counts: QuotaTokenCounts(input: 10, output: 2, cachedInput: 4, cacheWriteInput: nil,
                                                            reasoning: 1))
        var b = a
        b.counts = QuotaTokenCounts(input: 5, output: 1, cachedInput: 1, cacheWriteInput: nil, reasoning: 0)
        b.publicModel = false
        let summed = QuotaEstimateFile.perMinute([a, b])
        #expect(summed.count == 1)
        #expect(summed[0].counts == QuotaTokenCounts(input: 15, output: 3, cachedInput: 5, cacheWriteInput: nil, reasoning: 1))
        #expect(!summed[0].publicModel)
        var old = a
        old.counts = nil
        #expect(QuotaEstimateFile.perMinute([a, old])[0].counts == nil)
    }

    @Test func theStoredFormKeepsTheCountsAndOldRowsStayUnmeasured() throws {
        #expect(QuotaEstimateFile.version == 2)
        let counts = QuotaTokenCounts(input: 900, output: 90, cachedInput: 800, cacheWriteInput: nil, reasoning: 9)
        let file = QuotaEstimateFile(spend: [
            QuotaEstimateSpend(source: "codex", model: "gpt-5.5", startMs: 1, endMs: 2, costMicroUsd: 3, tokens: 990,
                               counts: counts, file: "/a"),
            QuotaEstimateSpend(source: "gemini", model: "acme", startMs: nil, endMs: 4, costMicroUsd: nil, tokens: 5,
                               counts: nil, publicModel: false, file: "/a"),
        ])
        let back = try #require(QuotaEstimateFile.decode(file.encoded(), file: "/a"))
        #expect(back.spend.map(\.counts) == [counts, nil])
        #expect(back.spend.map(\.publicModel) == [true, false])
        let v1 = Data(#"{"spend":[["codex","gpt-5.5",1,2,3,990,1,null]],"lines":[]}"#.utf8)
        let old = try #require(QuotaEstimateFile.decode(v1, file: "/a"))
        #expect(old.spend.count == 1 && old.spend[0].counts == nil, "a version 1 row is unmeasured, never 0")
        let broken = Data(#"{"spend":[["codex","gpt-5.5",1,2,3,990,1,null,[1,2],true]],"lines":[]}"#.utf8)
        #expect(QuotaEstimateFile.decode(broken, file: "/a") == nil)
        let negative = Data(#"{"spend":[["codex","gpt-5.5",1,2,3,990,1,null,[1,-2,null,null,null],true]],"lines":[]}"#.utf8)
        #expect(QuotaEstimateFile.decode(negative, file: "/a") == nil)
    }

    @Test func absurdStoredCountsEndUnsafeInsteadOfTrapping() {
        let ledger = QuotaEstimateLedger()
        let huge = QuotaTokenCounts(input: .max - 1, output: 1, cachedInput: nil, cacheWriteInput: nil, reasoning: nil)
        for minute in 0..<3 {
            ledger.add(account: "a", source: "codex", model: "gpt-5.5", endMs: Int64(minute) * 60_000, cost: 1,
                       counts: huge)
        }
        #expect(ledger.tokenUsage(["a"], nil, 0, 180_000) == nil)
    }

    // MARK: Reports

    @Test func theExtensionNeedsTheIntegerOne() throws {
        let parsed = ReportExtensions(try JSONParser.parse(#"{"quotaEstimate": 1, "quotaTokenUsage": 1}"#))
        #expect(parsed.quotaEstimate && parsed.quotaTokenUsage)
        for value in ["true", #""1""#, "2", "1.5", "null"] {
            #expect(!ReportExtensions(try JSONParser.parse(#"{"quotaTokenUsage": \#(value)}"#)).quotaTokenUsage, "\(value)")
        }
        let stored = try JSONDecoder().decode(ReportExtensions.self, from: JSONEncoder().encode(parsed))
        #expect(stored.quotaTokenUsage)
    }

    static func pipelineEntries() throws -> [QuotaReportEntry] {
        let c = try #require(F.list(QuotaEstimatePipelineTests.pipeline["tokens"]).first {
            F.string($0["name"]) == "Codex and Claude accounts"
        })
        let input = F.object(c["input"])
        let build = try QuotaEstimatePipelineTests.build(input)
        return build.entries(nowMs: F.int(input["now"])!, origin: F.string(input["origin"])!)
    }

    @Test func reportsCarryTheCountsOnlyWhenAdvertisedAndDropThemFirst() throws {
        let entries = try Self.pipelineEntries()
        #expect(entries.count == 5)
        let (content, sample) = ReportBodyTests.busySample()
        var options = ReportBodyTests.everything
        options.extensions.perfScore = true
        options.extensions.quotaEstimate = true
        options.quotas = entries
        let plain = ReportBody.fitted(ReportBodyTests.envelope, content, sample, options).json
        guard case .array(let without) = plain["quotas"] else { Issue.record("no quotas"); return }
        #expect(without.allSatisfy { $0["tokenUsage"] == nil })

        options.extensions.quotaTokenUsage = true
        let http = ReportBody.fitted(ReportBodyTests.envelope, content, sample, options)
        guard case .array(let carried) = http.json["quotas"] else { Issue.record("no quotas"); return }
        for entry in carried {
            guard case .object(let members) = entry else { Issue.record("not an object"); return }
            #expect(members.last?.key == "tokenUsage")
        }
        #expect(carried[0]["tokenUsage"]?["cacheWriteInputTokens"] == .null, "Codex logs no cache writes")
        #expect(carried[2]["tokenUsage"]?["reasoningTokens"] == .null, "Claude logs no thinking tokens")
        #expect(!String(decoding: http.data, as: UTF8.self).contains("claude-mystery-1"), "raw names never leave")

        // The WebSocket body (with `realtime` last) carries the same entries.
        var envelope = ReportBodyTests.envelope
        envelope.realtime = ReportRealtime(observationAgeMs: 120)
        let ws = ReportBody.fitted(envelope, content, sample, options).json
        #expect(ws["quotas"] == http.json["quotas"])

        // Too large with the counts: they go first, the quotas stay.
        var smaller = options
        smaller.extensions.quotaTokenUsage = false
        var tight = options
        tight.bodyLimit = ReportBody.fitted(ReportBodyTests.envelope, content, sample, smaller).data.count
        let fitted = ReportBody.fitted(ReportBodyTests.envelope, content, sample, tight).json
        guard case .array(let kept) = fitted["quotas"] else { Issue.record("quotas dropped"); return }
        #expect(kept.count == 5 && kept.allSatisfy { $0["tokenUsage"] == nil })
        #expect(fitted["models"] != nil)

        if ReferencePython.available {
            let failures = try ReferencePython.strictErrors([http.data, ReportBody.data(envelope, content, sample, options)])
            #expect(failures.isEmpty, "\(failures)")
        }
    }

    @Test func unmeasuredCountsAreNullNeverZero() throws {
        let c = try #require(F.list(QuotaEstimatePipelineTests.pipeline["tokens"]).first {
            F.string($0["name"]) == "a response recorded without counts"
        })
        let input = F.object(c["input"])
        let entries = try QuotaEstimatePipelineTests.build(input).entries(nowMs: F.int(input["now"])!,
                                                                          origin: F.string(input["origin"])!)
        #expect(entries.map { $0.tokenUsage == nil } == [true, true, false])
        #expect(entries[0].json(tokenUsage: true)["tokenUsage"] == .null)
        #expect(entries[0].usedMicroUsd > 0, "the spend still counts the response")
    }
}
