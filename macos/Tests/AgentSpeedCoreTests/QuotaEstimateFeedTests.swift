import Foundation
import Testing
@testable import AgentSpeedCore

/// What feeds the quota estimate (QUOTAS 10.1, 10.3, 10.4) and what it gives back: the history's per-minute
/// contributions, sign-in keys, Desktop's organization, the relay's account, the estimator's files, the report member.
struct QuotaEstimateFeedTests {
    static let minute: Int64 = 60_000
    static let now: Int64 = 1_794_000_000_000
    static let salt = Data((0..<32).map { UInt8($0) })

    static func record(_ key: String, _ source: SourceKind = .codex, start: Int64, end: Int64, model: String = "gpt-5.5",
                       provider: String? = nil, file: String = "/r/a.jsonl") -> ResponseRecord {
        ResponseRecord(key: key, source: source, agentId: file, model: model, startMs: start, endMs: end,
                       usage: TokenUsage(input: 1000, output: 200), provider: provider)
    }

    static func line(_ ts: Int64, percent: Double = 12, plan: String? = "pro", file: String = "/r/a.jsonl") -> QuotaCodexLine {
        QuotaCodexLine(file: file, ts: ts, windows: [
            QuotaWindow(kind: .fiveHour, usedPercent: percent, resetsAtMs: now + 3 * 3_600_000),
            QuotaWindow(kind: .sevenDay, usedPercent: 3, resetsAtMs: nil),
        ], plan: plan)
    }

    // MARK: The history's share (QUOTAS 10.3)

    @Test func aFileKeepsItsSpendPerMinuteAndPairsCodexLines() throws {
        var labeler = DayLabeler(timeZone: TimeZone(identifier: "UTC")!)
        let pricing = PriceBook()
        let models = QuotaModelIds(pricing: pricing)
        let t = Self.now - 10 * Self.minute
        let records = [
            Self.record("a", start: t - 20_000, end: t),
            Self.record("b", start: t + 1_000, end: t + 2_000),
            Self.record("c", start: t + 30_000, end: t + 31_000, provider: "azure"),
            Self.record("d", start: t + 40_000, end: t + 41_000),
            Self.record("old", start: Self.now - 9 * 86_400_000, end: Self.now - 9 * 86_400_000 + 1),
            Self.record("e", .claudeCode, start: t + 3_000, end: t + 4_000, model: "claude-opus-5-5"),
            Self.record("f", .claudeCode, start: t + 5_000, end: t + 59_000, model: "claude-opus-5-5"),
            Self.record("g", .claudeCode, start: t, end: t + 6_000, model: "anthropic.claude-opus-5-5-v1:0"),
        ]
        let lines = [Self.line(t + 1_500), Self.line(t + 2_500), Self.line(t + 31_500), Self.line(t + 47_000)]
        let file = QuotaEstimateFile.make(records: records, lines: lines, pricing: pricing, models: models,
                                          labeler: &labeler, nowMs: Self.now)
        // a ends 1.5 s before the first line, b 0.5 s before the second; c is another provider; d's line comes 6 s
        // late; g is a Bedrock model id; e and f share their minute.
        let codex = file.spend.filter { $0.source == "codex" }
        #expect(codex.map(\.codexLine?.ts) == [t + 1_500, t + 2_500])
        let row = try #require(codex.first)
        #expect(row.model == "gpt-5.5" && row.costMicroUsd != nil && row.requests == 1)
        let claude = file.spend.filter { $0.source == "claudeCode" }
        #expect(claude.count == 1)
        #expect(claude.first?.requests == 2 && claude.first?.tokens == 2400)
        #expect(claude.first?.startMs == t + 3_000 && claude.first?.endMs == t + 59_000)
        #expect(file.lines.map(\.tLo) == [t - 20_000, t + 1_000, t + 30_000, nil], "c's line is paired, so it is used")
        #expect(file.lines.first?.windows.count == 1, "a window without a reset is no value")
        // The stored form reads back the same, the path coming from the row.
        let back = try #require(QuotaEstimateFile.decode(file.encoded(), file: "/r/a.jsonl"))
        #expect(back.spend.map(\.endMs) == file.spend.map(\.endMs))
        #expect(back.spend.first?.codexLine == row.codexLine)
        #expect(back.lines == file.lines)
        #expect(QuotaEstimateFile.decode(Data("{".utf8), file: "x") == nil)
    }

    @Test func theHistoryStoreKeepsEachFilesShare() throws {
        let database = try HistoryDatabase.inMemory()
        let entry = HistoryDatabase.Entry(id: nil, family: "codex", size: 1, modified: 1, version: 11, days: [:])
        let share = QuotaEstimateFile(spend: [QuotaEstimateSpend(source: "codex", model: "gpt-5.5", startMs: 1, endMs: 2,
                                                                 costMicroUsd: 3, file: "/a", codexLine: .init(file: "/a", ts: 4))],
                                      lines: [Self.line(4, file: "/a")])
        try database.save([("/a", entry, []), ("/b", entry, [])], estimates: ["/a": share, "/b": QuotaEstimateFile()])
        let stored = try database.estimateFiles()
        #expect(stored["/a"]?.file.spend == share.spend)
        #expect(stored["/a"]?.version == QuotaEstimateFile.version)
        #expect(stored["/b"]?.file.isEmpty == true, "an empty share is still a current version")
        try database.save([("/a", entry, [])])
        #expect(try database.estimateFiles()["/a"] == nil, "a file saved again without a share loses it")
    }

    @Test func theCutSplitsHistoryAndLive() {
        #expect(QuotaEstimateHistory.cut(analysisStartMs: 10 * Self.minute + 30_000) == 8 * Self.minute)
    }

    // MARK: Keys (QUOTAS 10.1)

    @Test func signInFilesGiveTheirAccountIdsOnly() throws {
        let folder = try TempDirectory()
        let codex = try folder.write("auth.json", #"{"auth_mode":"chatgpt","tokens":{"id_token":"x","account_id":"acct-1","access_token":"y"}}"#)
        var codexSignIn = QuotaSignIn(.codex, file: codex)
        let codexIn = codexSignIn.refresh(nowMs: Self.now)
        #expect(codexIn)
        #expect(codexSignIn.identity == .codex(accountId: "acct-1"))
        let claude = try folder.write(".claude.json", #"{"oauthAccount":{"accountUuid":"u-1","emailAddress":"x","organizationUuid":"o-1","organizationType":"claude_max","organizationRateLimitTier":"default_claude_max_20x"},"projects":{}}"#)
        var claudeSignIn = QuotaSignIn(.claudeCode, file: claude)
        let claudeIn = claudeSignIn.refresh(nowMs: Self.now)
        #expect(claudeIn)
        #expect(claudeSignIn.identity == .claude(organization: "o-1", account: "u-1", organizationType: "claude_max",
                                                 tier: "default_claude_max_20x"))
        #expect((claudeSignIn.identityMs ?? .max) <= Self.now)
        let keys = QuotaAccountKeys(salt: Self.salt)
        let identity = try #require(claudeSignIn.identity)
        let profile = try #require(keys.claude(identity, at: 5))
        #expect(profile.entry.key == QuotaAccounts.key(salt: Self.salt, vendor: "claude", id: "o-1:u-1"))
        #expect(profile.orgKey == keys.desktop(organization: "O-1"), "organizations compare without case")
        #expect(profile.entry.plan == "claude_max")
        let anonymous = try folder.write("auth2.json", #"{"tokens":{"id_token":"x"}}"#)
        var noId = QuotaSignIn(.codex, file: anonymous)
        let anonymousIn = noId.refresh(nowMs: Self.now)
        #expect(anonymousIn)
        #expect(noId.identity == nil)
    }

    @Test func desktopsOrganizationComesFromItsUsageURL() {
        #expect(DesktopUsage.organization(ofURL: "https://claude.ai/api/organizations/1A2B3C4D-0000-4000-8000-00000000000F/usage?x=1")
            == "1a2b3c4d-0000-4000-8000-00000000000f")
        #expect(DesktopUsage.organization(ofURL: "https://claude.ai/api/organizations/x/usage") == nil)
    }

    @Test func codexParsersCollectTheirTokenCountsWithThePlan() throws {
        var parser = CodexParser(agentId: "/r/a.jsonl", fileID: 1)
        parser.collectQuotaLines()
        var deduper = Deduper()
        let line: [String: Any] = ["timestamp": "2026-11-10T19:11:22.318Z", "type": "event_msg", "payload": [
            "type": "token_count", "info": NSNull(),
            "rate_limits": ["limit_id": "codex", "plan_type": "promax",
                            "primary": ["used_percent": 23, "window_minutes": 10080, "resets_at": 1_794_897_479]],
        ]]
        parser.consume(try QuotaFixtures.extract(line), deduper: &deduper) { _ in }
        let lines = parser.takeQuotaLines()
        #expect(lines.count == 1)
        #expect(lines.first?.plan == "promax" && lines.first?.file == "/r/a.jsonl")
        #expect(lines.first?.windows.first?.resetsAtMs == 1_794_897_479_000)
        #expect(parser.takeQuotaLines().isEmpty)
    }

    // MARK: Remote hosts (REMOTE 11.1)

    @Test func relayLinesCarryTheAccountAndTheConfigTheSalt() throws {
        let decoder = RelayLineDecoder(hostId: "h")
        func report(_ extra: [String: Any]) -> RemoteQuotaReport? {
            var q: [String: Any] = ["source": "codex", "observedMs": RelayLines.baseMs, "windows": [], "running": true,
                                    "signedIn": true, "status": "ok"]
            q.merge(extra) { $1 }
            guard case .message(.quota(let report)) = decoder.decode(Data(RelayLines.json(["t": "quota", "q": q]).utf8))
            else { return nil }
            return report
        }
        let keyed = try #require(report(["account": "0123456789abcdef", "plan": "promax", "tier": NSNull()]))
        #expect(keyed.account == "0123456789abcdef" && keyed.plan == "promax" && keyed.tier == nil)
        let older = try #require(report([:]))
        #expect(older.account == nil && older.plan == nil)
        #expect(report(["account": "not-a-key", "plan": "Pro Max"])?.account == nil)
        #expect(report(["account": "not-a-key", "plan": "Pro Max"])?.plan == nil)
        let hex = QuotaEstimateSalt.hex(Self.salt)
        let config = RemoteQuotaConfig(sources: [.codex], network: true, grokLive: false, claudeBridge: false, salt: hex)
        let line = try JSONParser.parse(try #require(RelayLineEncoder.quota(config)))
        #expect(line["config"]?["salt"]?.string == hex)
        let none = try JSONParser.parse(try #require(RelayLineEncoder.quota(RemoteQuotaConfig(
            sources: [.codex], network: true, grokLive: false, claudeBridge: false, salt: "short"))))
        #expect(none["config"]?["salt"] == nil)
    }

    @Test func equalHostKeysShareUnlessTheirWindowsDisagree() {
        let reset = Self.now + 3 * 86_400_000
        func inputs(hostReset: Int64) -> QuotaEstimateInputs {
            var inputs = QuotaEstimateInputs()
            let key = QuotaAccounts.key(salt: Self.salt, vendor: "codex", id: "acct")
            inputs.codexKeys = [QuotaKeyEntry(at: Self.now - 3_600_000, key: key)]
            inputs.codexLines = [QuotaCodexLine(file: "/a", ts: Self.now - 60_000, windows: [
                QuotaWindow(kind: .sevenDay, usedPercent: 10, resetsAtMs: reset)], plan: "pro")]
            inputs.snapshots = [QuotaEstimateSnapshot(source: "codex", host: "h", observedMs: Self.now - 30_000, windows: [
                QuotaWindow(kind: .sevenDay, usedPercent: 11, resetsAtMs: hostReset)], account: key, plan: "pro")]
            inputs.spend = [QuotaEstimateSpend(source: "codex", host: "h", model: "gpt-5.5", startMs: nil,
                                               endMs: Self.now - 40_000, costMicroUsd: 1_000_000)]
            return inputs
        }
        let shared = QuotaEstimateBuild(inputs(hostReset: reset + 20_000), salt: Self.salt, nowMs: Self.now)
        let local = shared.account(of: QuotaEstimateSource(source: "codex"))
        #expect(local != nil && shared.account(of: QuotaEstimateSource(source: "codex", host: "h")) == local)
        #expect(shared.spendAccounts == [local])
        let apart = QuotaEstimateBuild(inputs(hostReset: reset + 19 * 3_600_000), salt: Self.salt, nowMs: Self.now)
        #expect(apart.account(of: QuotaEstimateSource(source: "codex", host: "h")) != local)
    }

    // MARK: The estimator (QUOTAS 10.4)

    @Test func theEstimatorKeepsItsFilesPrivateAndReplacesABrokenOne() throws {
        let folder = try TempDirectory()
        try folder.write("quota/estimate.json", "{not json")
        let clock = Self.now
        let estimator = QuotaEstimator(data: folder.url, pricing: PriceBook(), clock: { clock })
        estimator.setEnabled([.claudeCode])
        let identity = QuotaSignInIdentity.claude(organization: "o", account: "u", organizationType: "claude_max", tier: nil)
        estimator.claudeSignIn(try #require(estimator.keys.claude(identity, at: Self.now - 3_600_000)))
        estimator.snapshot(QuotaSnapshot(source: .claudeCode, observedMs: Self.now - 60_000, windows: [
            QuotaWindow(kind: .fiveHour, usedPercent: 4, resetsAtMs: Self.now + 3_600_000)]))
        estimator.flush()
        for name in ["quota/estimate-salt", "quota/estimate.json"] {
            let attributes = try FileManager.default.attributesOfItem(atPath: folder.file(name).path)
            #expect((attributes[.posixPermissions] as? NSNumber)?.intValue == 0o600, "\(name)")
        }
        #expect(try Data(contentsOf: folder.file("quota/estimate-salt")).count == 32)
        let store = try #require(QuotaEstimateStore.load(data: folder.url))
        #expect(store.observations.map(\.p) == [4])
        #expect(store.keys.contains { $0.source == "claudeCode" && $0.orgKey != nil })
        let text = try String(contentsOf: folder.file("quota/estimate.json"), encoding: .utf8)
        #expect(!text.contains("\"o\"") && !text.contains("\"u\""), "ids are never stored")
        // A second estimator reads the same salt back.
        #expect(QuotaEstimator(data: folder.url, pricing: nil).saltHex == estimator.saltHex)
    }

    @Test func historyHoldsTheSpendBeforeItsCutAndTheLiveEngineAfter() throws {
        let clock = Self.now
        let estimator = QuotaEstimator(data: nil, pricing: PriceBook(), clock: { clock })
        let cut = Self.now - 10 * Self.minute
        let early = QuotaEstimateSpend(source: "claudeCode", model: "claude-opus-5-5", startMs: nil, endMs: cut - Self.minute,
                                       costMicroUsd: 5, file: "/c")
        let late = QuotaEstimateSpend(source: "claudeCode", model: "claude-opus-5-5", startMs: nil, endMs: cut + Self.minute,
                                      costMicroUsd: 7, file: "/c")
        estimator.historyUpdated(QuotaEstimateHistory(files: ["/c": QuotaEstimateFile(spend: [early, late])], cutMs: cut))
        var before = Self.record("x", .claudeCode, start: cut - 2 * Self.minute, end: cut - Self.minute,
                                 model: "claude-opus-5-5", file: "/c")
        before.costMicroUsd = 11
        var after = Self.record("y", .claudeCode, start: cut, end: cut + 2 * Self.minute, model: "claude-opus-5-5",
                                file: "/c")
        after.costMicroUsd = 13
        estimator.spend(before)
        estimator.spend(after)
        estimator.flush()
        let inputs = estimator.inputs(nowMs: Self.now)
        #expect(inputs.spend.map(\.costMicroUsd) == [5, 13])
    }

    // MARK: Reports (REPORTING-EXTENSIONS 11.2, 11.3)

    @Test func reportsCarryQuotasAfterTheScoreAndDropThemFirst() throws {
        let input = EstimateFixtures.object(EstimateFixtures.list(EstimateFixtures.object(
            EstimateFixtures.cases["pipeline"])["report"])[0]["input"])
        let build = try QuotaEstimatePipelineTests.build(input)
        let entries = build.entries(nowMs: EstimateFixtures.int(input["now"])!)
        #expect(entries.count == 6)
        let (content, sample) = ReportBodyTests.busySample()
        var options = ReportBodyTests.everything
        options.extensions.perfScore = true
        options.extensions.quotaEstimate = true
        options.quotas = entries
        var envelope = ReportBodyTests.envelope
        envelope.realtime = ReportRealtime(observationAgeMs: 120)
        let body = ReportBody.fitted(envelope, content, sample, options).json
        guard case .object(let members) = body else { Issue.record("not an object"); return }
        let keys = members.map(\.key)
        let quotas = try #require(keys.firstIndex(of: "quotas"))
        #expect(keys[quotas - 1] == "perfScore" && keys[quotas + 1] == "realtime" && keys.last == "realtime")
        var without = options
        without.quotas = nil
        var small = options
        small.bodyLimit = ReportBody.fitted(envelope, content, sample, without).data.count
        let fitted = ReportBody.fitted(envelope, content, sample, small)
        #expect(fitted.json["quotas"] == nil && fitted.json["models"] != nil, "quotas go before anything else")
        if ReferencePython.available {
            let failures = try ReferencePython.strictErrors([ReportBody.data(envelope, content, sample, options)])
            #expect(failures.isEmpty, "\(failures)")
        }
    }

    @Test func theExtensionIsReadAndKept() throws {
        let parsed = ReportExtensions(try JSONParser.parse(#"{"quotaEstimate": 1, "perfScore": 1}"#))
        #expect(parsed.quotaEstimate && parsed.perfScore)
        #expect(!ReportExtensions(try JSONParser.parse(#"{"quotaEstimate": true}"#)).quotaEstimate)
        let stored = try JSONDecoder().decode(ReportExtensions.self, from: JSONEncoder().encode(parsed))
        #expect(stored.quotaEstimate)
    }

    // MARK: Cost

    /// A busy week of one Claude account and Codex builds within the estimator's 5 s interval even in a debug build
    /// beside the other suites (about 1 s alone): a guard against anything quadratic.
    @Test func aBusyWeekBuildsQuickly() {
        var inputs = QuotaEstimateInputs()
        let start = Self.now - 7 * 86_400_000
        let codexReset = Self.now + 86_400_000
        for i in 0..<20_000 {
            let end = start + Int64(i) * 30_000
            let file = "/r/\(i / 500).jsonl"
            inputs.spend.append(QuotaEstimateSpend(source: i % 2 == 0 ? "codex" : "claudeCode", model: "gpt-5.5",
                                                   startMs: end - 20_000, endMs: end, costMicroUsd: 50_000, file: file))
            if i % 2 == 0 {
                inputs.codexLines.append(QuotaCodexLine(file: file, ts: end + 1_000, windows: [
                    QuotaWindow(kind: .fiveHour, usedPercent: Double(i % 100), resetsAtMs: end - end % 18_000_000 + 18_000_000),
                    QuotaWindow(kind: .sevenDay, usedPercent: Double(i / 200), resetsAtMs: codexReset),
                ], plan: "pro"))
            } else if i % 10 == 1 {
                inputs.snapshots.append(QuotaEstimateSnapshot(source: "claudeCode", observedMs: end + 2_000, windows: [
                    QuotaWindow(kind: .sevenDay, usedPercent: Double(i / 250), resetsAtMs: codexReset + 3_600_000)]))
            }
        }
        inputs.claudeProfiles = [QuotaClaudeProfile(at: start, organization: "o", account: "u", organizationType: "claude_max",
                                                    tier: nil, salt: Self.salt)]
        let began = DispatchTime.now().uptimeNanoseconds
        let build = QuotaEstimateBuild(inputs, salt: Self.salt, nowMs: Self.now)
        _ = build.entries(nowMs: Self.now)
        let seconds = Double(DispatchTime.now().uptimeNanoseconds - began) / 1e9
        #expect(!build.current.isEmpty)
        #expect(seconds < Double(QuotaEstimator.buildIntervalMs) / 1000, "\(seconds) s")
    }
}
