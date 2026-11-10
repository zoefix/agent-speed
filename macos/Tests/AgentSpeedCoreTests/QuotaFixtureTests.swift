import Foundation
import Testing
@testable import AgentSpeedCore

/// Every case of `spec/fixtures/quota-cases.json` through the Swift readers and formats (QUOTAS 10).
struct QuotaFixtureTests {
    // MARK: Codex (QUOTAS 3)

    /// Each case's lines through the Codex parser after a `session_meta` whose sub-agent history ends at ordinal 100,
    /// so a copied line (ordinal 1) is copied history for the parser's own rule (METRICS 3.5).
    @Test func codexLines() throws {
        let cases = QuotaFixtures.list("codex")
        #expect(cases.count == 9)
        for item in cases {
            let name = item["name"] as? String ?? "?"
            var parser = CodexParser(agentId: "a", fileID: 1)
            var deduper = Deduper()
            let meta: [String: Any] = ["timestamp": "2025-01-01T00:00:00Z", "type": "session_meta",
                                       "payload": ["id": "t", "subagent_history_start_ordinal": 100]]
            parser.consume(try QuotaFixtures.extract(meta), deduper: &deduper) { _ in }
            for line in item["lines"] as? [[String: Any]] ?? [] {
                var object = try #require(line["line"] as? [String: Any])
                if line["copied"] as? Bool == true { object["ordinal"] = 1 }
                parser.consume(try QuotaFixtures.extract(object), deduper: &deduper) { _ in }
            }
            #expect(parser.quota == QuotaSnapshot(fixture: item["expected"], source: .codex), "\(name)")
        }
    }

    @Test func codexRateLimitsReachTheLineWhateverTheUsageRules() throws {
        let line: [String: Any] = ["timestamp": "2026-11-10T19:11:22.318Z", "type": "event_msg", "payload": [
            "type": "token_count", "info": NSNull(),
            "rate_limits": ["primary": ["used_percent": 23, "window_minutes": 10080, "resets_at": 1_794_897_479]],
        ]]
        guard case .line(let decoded) = try QuotaFixtures.extract(line) else {
            Issue.record("not a line")
            return
        }
        #expect(decoded.rateLimits?.windows(timestampMs: 0).map { $0.label() } == ["7d"])
        let other: [String: Any] = ["timestamp": "2026-11-10T19:11:22.318Z", "type": "event_msg",
                                    "payload": ["type": "agent_message", "rate_limits": ["primary": [:]]]]
        guard case .line(let ignored) = try QuotaFixtures.extract(other) else { return }
        #expect(ignored.rateLimits == nil)
    }

    // MARK: Claude Desktop (QUOTAS 4)

    @Test func cacheEntries() throws {
        let entries = QuotaFixtures.list("entries")
        #expect(entries.count == 12)
        for item in entries {
            let file = item["file"] as? String ?? ""
            let parsed = DesktopUsage.entry(try QuotaFixtures.entry(file), mtimeMs: QuotaFixtures.now)
            guard let expected = item["expected"] as? [String: Any] else {
                #expect(parsed == nil, "\(file)")
                continue
            }
            #expect(parsed?.url == expected["url"] as? String, "\(file)")
            #expect(parsed?.observedMs == (expected["observedMs"] as? NSNumber)?.int64Value, "\(file)")
            #expect(parsed?.windows == QuotaFixtures.windows(expected["windows"]), "\(file)")
        }
    }

    @Test func everyContentCodingDecodes() throws {
        let bodies = try ["zstd-ultra.bin", "gzip-max.bin", "br-ultra.bin", "identity-max.bin"].map {
            try #require(SimpleCacheEntry(try QuotaFixtures.entry($0)))
        }
        let codings: [ContentDecoding.Coding] = [.zstd, .gzip, .brotli, .identity]
        for (entry, coding) in zip(bodies, codings) {
            let decoded = try #require(ContentDecoding.decode(Array(entry.body), coding: coding, limit: 1 << 20), "\(coding)")
            #expect((try? JSONParser.parse(Data(decoded)))?.object != nil, "\(coding)")
            // Cut short, every coding but identity fails instead of giving part of the body.
            if coding != .identity {
                #expect(ContentDecoding.decode(Array(entry.body.dropLast(4)), coding: coding, limit: 1 << 20) == nil, "\(coding)")
            }
            #expect(ContentDecoding.decode(Array(entry.body), coding: coding, limit: 16) == nil, "\(coding) limit")
        }
        #expect(ContentDecoding.coding("compress") == nil)
        #expect(ContentDecoding.coding(nil) == .identity)
        #expect(ContentDecoding.coding(" ZSTD ") == .zstd)
    }

    @Test func entryFileName() throws {
        let item = try #require(QuotaFixtures.cases["fileName"] as? [String: Any])
        #expect(SimpleCacheEntry.fileName(ofKey: item["key"] as? String ?? "") == item["expected"] as? String)
    }

    @Test func newestDesktopEntryWins() throws {
        let item = try #require(QuotaFixtures.cases["desktopNewest"] as? [String: Any])
        let mtime = try #require((item["mtimeMs"] as? NSNumber)?.int64Value)
        let entries = try (item["files"] as? [String] ?? []).compactMap { name in
            DesktopUsage.entry(try QuotaFixtures.entry(name), mtimeMs: mtime).map { (entry: $0, mtimeMs: mtime, tag: name) }
        }
        let best = try #require(DesktopUsage.newest(entries))
        let expected = try #require(item["expected"] as? [String: Any])
        #expect(best.tag == expected["file"] as? String)
        #expect(best.entry.observedMs == (expected["observedMs"] as? NSNumber)?.int64Value)
        #expect(best.entry.windows == QuotaFixtures.windows(expected["windows"]))
    }

    // MARK: Claude CLI bridge (QUOTAS 5)

    @Test func commands() throws {
        let commands = try #require(QuotaFixtures.cases["commands"] as? [String: Any])
        for pair in commands["posix"] as? [[String]] ?? [] {
            #expect(StatusLineSettings.posixCommand(pair[0]) == pair[1])
        }
    }

    @Test func bridgeOnAndOff() throws {
        let cases = QuotaFixtures.list("bridge")
        #expect(cases.count == 7)
        let command = try #require((QuotaFixtures.cases["commands"] as? [String: Any])?["posix"] as? [[String]])[0][1]
        for item in cases {
            let name = item["name"] as? String ?? "?"
            let settings = item["settings"] as? String
            let sidecar = try Self.ordered(item["sidecar"])
            var previous: OrderedJSON? = sidecar
            if let on = item["on"] as? [String: Any] {
                let result = StatusLineSettings.turnOn(settings, command: command, sidecar: sidecar)
                if on["status"] as? String == "unreadable" {
                    #expect(result == .unreadable, "\(name)")
                    continue
                }
                guard case .on(let text, let kept) = result else {
                    Issue.record("\(name): not on")
                    continue
                }
                #expect(text == on["settings"] as? String, "\(name) on")
                #expect(Self.same(kept, on["previous"]), "\(name) previous")
                previous = kept
                let off = try #require(item["off"] as? [String: Any])
                #expect(StatusLineSettings.turnOff(text, previous: previous) == off["settings"] as? String, "\(name) off")
            } else {
                let off = try #require(item["off"] as? [String: Any])
                #expect(StatusLineSettings.turnOff(settings, previous: previous) == off["settings"] as? String, "\(name) off")
            }
        }
    }

    @Test func bridgeStatus() {
        let cases = QuotaFixtures.list("bridgeStatus")
        #expect(cases.count == 4)
        for item in cases {
            let status = StatusLineSettings.status(item["settings"] as? String, hasQuotaFile: item["quotaFile"] as? Bool == true)
            let expected: StatusLineSettings.Status? = switch item["expected"] as? String {
            case "waiting": .waiting
            case "ok": .ok
            case "replaced": .replaced
            case "unreadable": .unreadable
            default: nil
            }
            #expect(status == expected)
        }
    }

    static func ordered(_ value: Any?) throws -> OrderedJSON? {
        guard let value, !(value is NSNull) else { return nil }
        return OrderedJSON.parse(try JSONSerialization.data(withJSONObject: value))
    }

    /// The same JSON value, member order aside.
    static func same(_ value: OrderedJSON?, _ expected: Any?) -> Bool {
        guard let value else { return expected == nil || expected is NSNull }
        guard let expected, let parsed = try? JSONSerialization.jsonObject(with: Data(value.text().utf8)) else { return false }
        return (parsed as AnyObject).isEqual(expected)
    }

    // MARK: Model and formatting (QUOTAS 2, 7)

    @Test func shownWindows() throws {
        let item = try #require(QuotaFixtures.cases["shown"] as? [String: Any])
        let snapshot = try #require(QuotaSnapshot(fixture: item["snapshot"], source: .codex))
        let shown = snapshot.shown(nowMs: QuotaFixtures.now)
        let expected = item["expected"] as? [[String: Any]] ?? []
        #expect(shown.count == expected.count)
        for (window, wanted) in zip(shown, expected) {
            #expect(window.label == wanted["label"] as? String)
            #expect(window.percent == (wanted["percent"] as? NSNumber)?.intValue)
            #expect(window.bar == (wanted["bar"] as? NSNumber)?.doubleValue)
            #expect(window.resetsAtMs == (wanted["resetsAtMs"] as? NSNumber)?.int64Value)
            #expect(window.passed == wanted["passed"] as? Bool)
        }
    }

    @Test func labelsAndPercents() {
        let labels = QuotaFixtures.cases["labels"] as? [[Any]] ?? []
        #expect(labels.count == 10)
        for pair in labels {
            let window = QuotaFixtures.window(pair[0] as? [String: Any] ?? [:])
            let expected = pair[1] as? [String: String] ?? [:]
            for language in QuotaFixtures.languages {
                #expect(window.label(QuotaFixtures.format(language).strings.words) == expected[language], "\(language)")
            }
        }
        let percents = QuotaFixtures.cases["percents"] as? [[NSNumber]] ?? []
        #expect(percents.count == 8)
        for pair in percents {
            #expect(QuotaPercent.rounded(pair[0].doubleValue) == pair[1].intValue, "\(pair[0])")
        }
    }

    @Test func order() throws {
        let item = try #require(QuotaFixtures.cases["order"] as? [String: Any])
        let ordered = QuotaSnapshot.ordered(QuotaFixtures.windows(item["windows"]))
        #expect(ordered.map { $0.label() } == item["expected"] as? [String])
        #expect(QuotaSnapshot.ordered(ordered) == ordered)
    }

    @Test func resetTexts() {
        let cases = QuotaFixtures.list("resetTexts")
        #expect(cases.count == 7)
        for item in cases {
            let resets = (item["resetsAtMs"] as? NSNumber)?.int64Value ?? 0
            for language in QuotaFixtures.languages {
                let format = QuotaFixtures.format(language)
                #expect(format.resetLong(resets, nowMs: QuotaFixtures.now)
                    == (item["long"] as? [String: String])?[language], "\(resets) \(language) long")
                #expect(format.resetShort(resets, nowMs: QuotaFixtures.now)
                    == (item["short"] as? [String: String])?[language], "\(resets) \(language) short")
            }
        }
    }

    @Test func ageTexts() {
        let cases = QuotaFixtures.list("ageTexts")
        #expect(cases.count == 4)
        for item in cases {
            let observed = (item["observedMs"] as? NSNumber)?.int64Value ?? 0
            let expected = item["expected"] as? [String: Any] ?? [:]
            for language in QuotaFixtures.languages {
                #expect(QuotaFixtures.format(language).age(observedMs: observed, nowMs: QuotaFixtures.now)
                    == expected[language] as? String, "\(observed) \(language)")
            }
        }
    }

    @Test func pace() {
        let cases = QuotaFixtures.list("pace")
        #expect(cases.count == 4)
        for item in cases {
            let window = QuotaFixtures.window(item["window"] as? [String: Any] ?? [:])
            let expected = (item["expected"] as? NSNumber)?.doubleValue
            let pace = QuotaFormat.pace(window, nowMs: QuotaFixtures.now)
            if let expected {
                #expect(pace.map { abs($0 - expected) < 1e-12 } == true)
            } else {
                #expect(pace == nil)
            }
        }
        #expect(QuotaFixtures.format("zh-Hant").paceText(0.6) == "時間進度 60%")
    }
}
