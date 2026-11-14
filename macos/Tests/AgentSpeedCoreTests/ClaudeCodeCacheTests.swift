import Foundation
import Testing
@testable import AgentSpeedCore

/// Claude Code's usage cache and its join with the bridge (QUOTAS 5.6): `claudeCodeCache` of
/// `spec/fixtures/quota-cases.json` through the Swift reader, join, estimate feed and formats.
struct ClaudeCodeCacheTests {
    nonisolated(unsafe) static let cases = QuotaFixtures.cases["claudeCodeCache"] as? [String: Any] ?? [:]

    static func snapshot(_ value: Any?) -> QuotaSnapshot? {
        QuotaSnapshot(fixture: value, source: .claudeCode)
    }

    static func outcome(_ text: String) -> ClaudeCodeUsageCache.Outcome {
        Data(text.utf8).withUnsafeBytes(ClaudeCodeUsageCache.read)
    }

    @Test func files() {
        let files = Self.cases["files"] as? [[String: Any]] ?? []
        #expect(files.count == 24)
        for item in files {
            let name = item["name"] as? String ?? "?"
            let read = Self.outcome(item["text"] as? String ?? "")
            if item["expected"] as? String == "unparsed" {
                #expect(read == .unparsed, "\(name)")
            } else if let expected = Self.snapshot(item["expected"]) {
                #expect(read == .cache(expected), "\(name)")
            } else {
                #expect(read == ClaudeCodeUsageCache.Outcome.none, "\(name)")
            }
        }
    }

    /// One reader over the fixture's texts in a row, a minute apart: a text that does not parse keeps the value.
    @Test func readsInARow() throws {
        let item = try #require(Self.cases["reads"] as? [String: Any])
        let texts = item["texts"] as? [String] ?? []
        let expected = item["expected"] as? [Any] ?? []
        #expect(texts.count == 5 && expected.count == 5)
        let folder = try TempDirectory()
        var reader = ClaudeCodeUsageCache(file: folder.file(".claude.json"))
        for (index, text) in texts.enumerated() {
            try folder.write(".claude.json", text, modified: Date(timeIntervalSince1970: Double(1_000 + index * 10)))
            reader.refresh(nowMs: QuotaFixtures.now + Int64(index) * 60_000)
            #expect(reader.snapshot == Self.snapshot(expected[index]), "read \(index)")
        }
    }

    @Test func theReaderKeepsTheSignedInMarkersCadence() throws {
        let files = Self.cases["files"] as? [[String: Any]] ?? []
        let observed = try #require(files.first { $0["name"] as? String == "observed" }?["text"] as? String)
        let other = try #require(files.first { $0["name"] as? String == "otherAccount" }?["text"] as? String)
        let broken = String(observed.prefix(observed.count - 2))
        let folder = try TempDirectory()
        let file = folder.file(".claude.json")
        var reader = ClaudeCodeUsageCache(file: file)
        let now = QuotaFixtures.now
        let changed1 = reader.refresh(nowMs: now)
        #expect(!changed1, "no file, no cache")
        try folder.write(".claude.json", observed, modified: Date(timeIntervalSince1970: 1_000))
        let changed2 = reader.refresh(nowMs: now)
        #expect(changed2)
        #expect(reader.snapshot?.windows.map { $0.label() } == ["5h", "7d", "Fable 7d"])
        try folder.write(".claude.json", other, modified: Date(timeIntervalSince1970: 1_010))
        let changed3 = reader.refresh(nowMs: now + 59_000)
        #expect(!changed3, "a change within the minute waits")
        let changed4 = reader.refresh(nowMs: now + 60_000)
        #expect(changed4)
        #expect(reader.snapshot == nil, "another account's cache is skipped")
        // A file that does not parse keeps the value, and the next check after the minute reads it again even though
        // nothing changed meanwhile: here the same size and time with readable bytes again.
        try folder.write(".claude.json", observed, modified: Date(timeIntervalSince1970: 1_020))
        let changed5 = reader.refresh(nowMs: now + 120_000)
        #expect(changed5)
        try folder.write(".claude.json", broken + "  ", modified: Date(timeIntervalSince1970: 1_030))
        let changed6 = reader.refresh(nowMs: now + 180_000)
        #expect(!changed6)
        #expect(reader.snapshot != nil)
        try folder.write(".claude.json", other, modified: Date(timeIntervalSince1970: 1_030))
        let changed7 = reader.refresh(nowMs: now + 200_000)
        #expect(!changed7, "not before the minute")
        let changed8 = reader.refresh(nowMs: now + 240_000)
        #expect(changed8)
        #expect(reader.snapshot == nil)
        // Larger than 64 MiB: not read.
        try folder.write(".claude.json", observed, modified: Date(timeIntervalSince1970: 1_040))
        let changed9 = reader.refresh(nowMs: now + 300_000)
        #expect(changed9)
        let handle = try FileHandle(forWritingTo: file)
        try handle.truncate(atOffset: UInt64(QuotaSignIn.maxFileBytes) + 1)
        try handle.close()
        let changed10 = reader.refresh(nowMs: now + 360_000)
        #expect(changed10)
        #expect(reader.snapshot == nil)
        try FileManager.default.removeItem(at: file)
        let changed11 = reader.refresh(nowMs: now + 420_000)
        #expect(!changed11)
        reader.reset()
        #expect(reader.snapshot == nil)
    }

    /// A file that exists but cannot be read keeps the value, like one that does not parse.
    @Test func anUnreadableFileKeepsTheValue() throws {
        let files = Self.cases["files"] as? [[String: Any]] ?? []
        let observed = try #require(files.first { $0["name"] as? String == "observed" }?["text"] as? String)
        let folder = try TempDirectory()
        let file = try folder.write(".claude.json", observed, modified: Date(timeIntervalSince1970: 1_000))
        var reader = ClaudeCodeUsageCache(file: file)
        _ = reader.refresh(nowMs: QuotaFixtures.now)
        let value = reader.snapshot
        #expect(value != nil)
        try folder.write(".claude.json", observed + " ", modified: Date(timeIntervalSince1970: 1_010))
        try FileManager.default.setAttributes([.posixPermissions: 0], ofItemAtPath: file.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path) }
        let unreadable = reader.refresh(nowMs: QuotaFixtures.now + 60_000)
        #expect(!unreadable)
        #expect(reader.snapshot == value)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
        try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSince1970: 1_010)], ofItemAtPath: file.path)
        let again = reader.refresh(nowMs: QuotaFixtures.now + 120_000)
        #expect(!again, "read again although unchanged, with the same value")
        #expect(reader.snapshot == value)
    }

    @Test func joins() {
        let joins = Self.cases["joins"] as? [[String: Any]] ?? []
        #expect(joins.count == 12)
        for item in joins {
            let name = item["name"] as? String ?? "?"
            let joined = ClaudeCodeQuota.join(bridge: Self.snapshot(item["bridge"]), cache: Self.snapshot(item["cache"]))
            #expect(joined == Self.snapshot(item["expected"]), "\(name)")
        }
    }

    @Test func theEstimateFeedTakesEachWindowForward() {
        let steps = Self.cases["feed"] as? [[String: Any]] ?? []
        #expect(steps.count == 6)
        var feed = QuotaWindowFeed()
        for (index, step) in steps.enumerated() {
            guard let snapshot = Self.snapshot(step["snapshot"]) else {
                Issue.record("step \(index)")
                continue
            }
            let groups = feed.take(snapshot)
            let expected = (step["expected"] as? [[String: Any]] ?? []).compactMap(Self.snapshot)
            #expect(groups.map(\.observedMs) == expected.map(\.observedMs), "step \(index)")
            #expect(groups.map(\.windows) == expected.map(\.windows), "step \(index)")
        }
    }

    /// Each window's own time and age text, and the `Fable 7d` line at 100 %.
    @Test func eachWindowHasItsOwnAge() throws {
        for key in ["ages", "cacheOnlyAges"] {
            let item = try #require(Self.cases[key] as? [String: Any])
            let now = (item["nowMs"] as? NSNumber)?.int64Value ?? 0
            let snapshot = try #require(Self.snapshot(item["snapshot"]))
            let expected = item["expected"] as? [[String: Any]] ?? []
            #expect(snapshot.windows.count == expected.count, "\(key)")
            for (window, wanted) in zip(snapshot.windows, expected) {
                #expect(window.label() == wanted["label"] as? String, "\(key)")
                let time = snapshot.time(of: window)
                #expect(time == (wanted["observedMs"] as? NSNumber)?.int64Value, "\(key)")
                let ages = wanted["age"] as? [String: Any] ?? [:]
                for language in QuotaFixtures.languages {
                    #expect(QuotaFixtures.format(language).age(observedMs: time, nowMs: now) == ages[language] as? String,
                            "\(key) \(window.label()) \(language)")
                }
            }
            guard let shown = item["shown"] as? [[String: Any]] else { continue }
            let drawn = snapshot.shown(nowMs: now)
            #expect(drawn.map(\.label) == shown.map { $0["label"] as? String ?? "" })
            #expect(drawn.map(\.percent) == shown.map { ($0["percent"] as? NSNumber)?.intValue ?? -1 })
            #expect(drawn.last?.label == "Fable 7d" && drawn.last?.percentText == "100%")
        }
    }

    /// Only a window whose time moves forward reaches the estimate: a Fable value from a newer cache read arrives
    /// although the snapshot's newest time stays the bridge's, and an older cache value never follows a newer one.
    @Test func theEstimatorTakesClaudeCodeWindowByWindow() throws {
        let now = QuotaFixtures.now
        let estimator = QuotaEstimator(data: nil, pricing: PriceBook(), clock: { now })
        estimator.setEnabled([.claudeCode])
        let reset = now + 3_600_000, week = now + 3 * 86_400_000
        func joined(cacheMs: Int64, fable: Double, bridgeMs: Int64 = now - 60_000) -> QuotaSnapshot? {
            ClaudeCodeQuota.join(
                bridge: QuotaSnapshot(source: .claudeCode, observedMs: bridgeMs, windows: [
                    QuotaWindow(kind: .fiveHour, usedPercent: 50, resetsAtMs: reset),
                    QuotaWindow(kind: .sevenDay, usedPercent: 80, resetsAtMs: week)]),
                cache: QuotaSnapshot(source: .claudeCode, observedMs: cacheMs, windows: [
                    QuotaWindow(kind: .fiveHour, usedPercent: 40, resetsAtMs: reset),
                    QuotaWindow(kind: .modelWeek, name: "Fable", usedPercent: fable, resetsAtMs: week)]))
        }
        for snapshot in [joined(cacheMs: now - 600_000, fable: 97), joined(cacheMs: now - 600_000, fable: 97),
                         joined(cacheMs: now - 300_000, fable: 99), joined(cacheMs: now - 900_000, fable: 98)] {
            estimator.snapshot(try #require(snapshot))
        }
        estimator.flush()
        let fed = estimator.inputs(nowMs: now).snapshots
        #expect(fed.map(\.observedMs) == [now - 600_000, now - 60_000, now - 300_000])
        #expect(fed.map { $0.windows.map { $0.label() } } == [["Fable 7d"], ["5h", "7d"], ["Fable 7d"]])
        #expect(fed.last?.windows.first?.usedPercent == 99)
        #expect(fed.allSatisfy { $0.windows.allSatisfy { $0.observedMs == nil } })
        // Hosts keep a feed of their own.
        estimator.snapshot(try #require(joined(cacheMs: now - 600_000, fable: 97)), host: "h1")
        estimator.flush()
        #expect(estimator.inputs(nowMs: now).snapshots.filter { $0.host == "h1" }.count == 2)
    }
}
