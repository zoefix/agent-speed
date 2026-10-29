import Foundation
import Darwin
import os
import Testing
@testable import AgentSpeedCore

@Suite("Trae native stores")
struct TraeTests {
    static let dayStart: Int64 = 1_792_454_400_000
    static let now: Int64 = 1_792_497_700_000

    private static func physicalURL(_ path: String) throws -> URL {
        let pointer = try #require(realpath(path, nil))
        defer { free(pointer) }
        return URL(fileURLWithPath: String(cString: pointer))
    }

    private static func install(_ name: String, at root: URL) throws {
        let fixture = Fixtures.root.appendingPathComponent("trae/" + name)
        let database = root.appendingPathComponent("ModularData/ai-agent/database.db")
        let log = root.appendingPathComponent("logs/launch/Modular/ai_agent-0.alaudalog")
        try FileManager.default.createDirectory(at: database.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: log.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(contentsOf: fixture.appendingPathComponent("ModularData/ai-agent/database.db")).write(to: database, options: .atomic)
        try Data(contentsOf: fixture.appendingPathComponent("logs/20261020T120000/Modular/ai_agent-0-2026-10-20.alaudalog")).write(to: log, options: .atomic)
    }

    @Test func sourceIdentifiersAndLocationsPreserveExistingSettings() {
        #expect(SourceSet(.trae).rawValue == 1 << 17)
        #expect(SourceSet(.pi).rawValue == 1 << 13)
        #expect(SourceSet(.droid).rawValue == 1 << 16)
        #expect(SourceSet(.grokBuild).rawValue == 1 << 12)
        #expect(TranscriptFamily(.trae) == nil)
        #expect(!TaskScoring.scores(.trae), "Trae has no verified task-score input")
        #expect(LiveMonitor.defaultTraeRoots.map(\.lastPathComponent) == ["Trae", "Trae CN", "TRAE SOLO", "TRAE SOLO CN"])
        var moved = MonitorConfiguration(enabledSources: [.trae])
        let old = moved
        moved.traeRoot = URL(fileURLWithPath: "/test/trae")
        #expect(moved.requiresRescan(comparedTo: old))
        #expect(moved.resolvedTraeRoots == ["/test/trae"])
    }

    @Test func nativeFixtureAgreementIncludesEncryptedCommittedWAL() throws {
        for file in try #require(try Fixtures.json("trae-expected.json")["files"] as? [[String: Any]]) {
            let path = Fixtures.root.appendingPathComponent(try #require(file.string("path")))
            let before = try Data(contentsOf: path)
            let rows = try TraeDatabase(path: path.path).read()
            try HarnessFixtureTests.compare(rows.map(\.record), try #require(file["responses"] as? [[String: Any]]))
            #expect(rows.allSatisfy { !$0.record.isMeasured && $0.record.startMs == nil })
            #expect(try Data(contentsOf: path) == before)
            #expect(!FileManager.default.fileExists(atPath: path.path + "-shm"))
        }
    }

    @Test func liveHistoryAndRestartUseFinalCallCountsWithoutInventingSpeed() throws {
        let root = Fixtures.root.appendingPathComponent("trae/wal")
        let configuration = MonitorConfiguration(enabledSources: [.trae], traeRoot: root)
        let session = TranscriptSession(configuration: configuration, clock: { Self.now })
        let summary = session.seed(todayStartMs: Self.dayStart)
        let result = session.advance(nowMs: Self.now, todayStartMs: Self.dayStart, emitsEvents: false).output
        #expect(summary.filesDiscovered == 1 && summary.filesRead == 1 && summary.partialHistory)
        #expect(result.today.requests == 3 && result.today.input == 2200 && result.today.output == 420)
        #expect(result.averageToday == nil)
        #expect(session.responses.allSatisfy { !$0.isMeasured })
        #expect(result.agents.allSatisfy { $0.phase == .idle })
        #expect(session.sessionCounts(todayStartMs: Self.dayStart)[.trae] == 2)
        #expect(!session.pollRecentFiles(nowMs: Self.now))
        let temp = try TempDirectory()
        let store = temp.file("history.sqlite").path
        let first = try HistoryScan.run(configuration: configuration, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        let second = try HistoryScan.run(configuration: configuration, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        #expect(first.days == second.days && second.sessionsAnalyzed == 0)
        let day = try #require(first.days.first { $0.source == .trae })
        #expect(day.totals == result.today && day.measuredMs == 0)
    }

    @Test func authenticatedPagesRejectTamperingAndSourceNeverCreatesSidecars() throws {
        let temp = try TempDirectory()
        let path = temp.file("database.db")
        var bytes = try Data(contentsOf: Fixtures.root.appendingPathComponent("trae/encrypted/database.db"))
        bytes[100] ^= 1
        try bytes.write(to: path)
        #expect(throws: TraeSnapshot.Error.self) { try TraeSnapshot.read(path: path.path) }
        let source = TraeSource(roots: [temp.path], fileID: -3)
        var deduper = Deduper()
        #expect(source.seed(todayStartMs: Self.dayStart, deduper: &deduper).isEmpty)
        #expect(source.status == .unsupported)
        #expect(try FileManager.default.contentsOfDirectory(atPath: temp.path) == ["database.db"])
    }

    @Test func copiedStoresAndUnchangedRefreshDoNotCountTwice() throws {
        let plain = Fixtures.root.appendingPathComponent("trae/plain").path
        let encrypted = Fixtures.root.appendingPathComponent("trae/encrypted").path
        let source = TraeSource(roots: [plain, encrypted], fileID: -3)
        var deduper = Deduper()
        let update = source.seed(todayStartMs: Self.dayStart, deduper: &deduper)
        #expect(update.upserts.count == 3 && source.activeSessionCount(todayStartMs: Self.dayStart) == 2)
        #expect(source.refreshIfChanged(deduper: &deduper) == nil)
    }

    @Test func movingToACopiedDatabasePreservesHistoryWithoutDoubleCounting() throws {
        let temp = try TempDirectory()
        let store = temp.file("history.sqlite").path
        let first = MonitorConfiguration(enabledSources: [.trae], traeRoot: Fixtures.root.appendingPathComponent("trae/plain"))
        let copy = MonitorConfiguration(enabledSources: [.trae], traeRoot: Fixtures.root.appendingPathComponent("trae/encrypted"))
        let original = try HistoryScan.run(configuration: first, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        let moved = try HistoryScan.run(configuration: copy, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        #expect(original.days == moved.days)
        #expect(moved.days.reduce(0) { $0 + $1.totals.requests } == 3)
    }

    @Test func rollbackJournalAndHostilePageSizesFailSafely() throws {
        let temp = try TempDirectory()
        let path = temp.file("database.db")
        try FileManager.default.copyItem(at: Fixtures.root.appendingPathComponent("trae/encrypted/database.db"), to: path)
        try Data([1]).write(to: temp.file("database.db-journal"))
        #expect(throws: TraeSnapshot.Error.self) { try TraeSnapshot.read(path: path.path) }
        try Data(repeating: 0, count: 4096).write(to: temp.file("database.db-journal"))
        #expect(try TraeDatabase(path: path.path).read().count == 3, "zeroed persistent journal is not hot")
        try FileManager.default.removeItem(at: temp.file("database.db-journal"))
        try Data([1, 2, 3]).write(to: temp.file("database.db-wal"))
        #expect(throws: TraeSnapshot.Error.self) { try TraeSnapshot.read(path: path.path) }
        try FileManager.default.removeItem(at: temp.file("database.db-wal"))
        try Data(repeating: 0, count: 4095).write(to: path)
        #expect(throws: TraeSnapshot.Error.self) { try TraeSnapshot.read(path: path.path) }
    }

    @Test func optionalNativeTimingRequiresEveryCorroboratingGate() throws {
        for example in try #require(try Fixtures.json("trae-timing-expected.json")["cases"] as? [[String: Any]]) {
            let path = Fixtures.root.appendingPathComponent(try #require(example.string("database")))
            let records = try TraeDatabase(path: path.path).read().map(\.record)
            let expected = try #require(example["responses"] as? [[String: Any]])
            try HarnessFixtureTests.compare(records, expected)
            #expect(records.filter(\.isMeasured).count == expected.filter { $0["startMs"] is NSNumber }.count,
                    Comment(rawValue: example.string("name") ?? ""))
        }
    }

    @Test func lateLogsUpgradeLiveAndPersistedHistoryWithoutCountingAgain() async throws {
        let temp = try TempDirectory()
        let root = try Self.physicalURL(temp.path)
        let database = root.appendingPathComponent("ModularData/ai-agent/database.db")
        let log = root.appendingPathComponent("logs/launch/Modular/ai_agent-0.alaudalog")
        try FileManager.default.createDirectory(at: database.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: log.deletingLastPathComponent(), withIntermediateDirectories: true)
        let fixture = Fixtures.root.appendingPathComponent("trae/timing-valid")
        try FileManager.default.copyItem(at: fixture.appendingPathComponent("ModularData/ai-agent/database.db"), to: database)
        let source = TraeSource(roots: [root.path], fileID: -3)
        var deduper = Deduper()
        let first = source.seed(todayStartMs: Self.dayStart, deduper: &deduper)
        #expect(first.upserts.count == 3 && first.upserts.allSatisfy { !$0.0.record.isMeasured })
        let configuration = MonitorConfiguration(enabledSources: [.trae], traeRoot: root)
        let store = root.appendingPathComponent("history.sqlite").path
        let before = try HistoryScan.run(configuration: configuration, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        try FileManager.default.copyItem(at: fixture.appendingPathComponent("logs/20261020T120000/Modular/ai_agent-0-2026-10-20.alaudalog"), to: log)
        try await Task.sleep(for: .milliseconds(1100))
        let updated = try #require(source.refreshIfChanged(deduper: &deduper))
        #expect(updated.upserts.count == 1 && updated.upserts[0].0.record.isMeasured && !updated.upserts[0].0.isCompletion)
        #expect(source.refreshIfChanged(deduper: &deduper) == nil)
        let after = try HistoryScan.run(configuration: configuration, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        #expect(before.days.map(\.totals) == after.days.map(\.totals))
        #expect(after.days.reduce(0) { $0 + $1.measuredMs } == 8000)
        let restart = try HistoryScan.run(configuration: configuration, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        #expect(restart.days == after.days && restart.sessionsAnalyzed == 0)
        try FileManager.default.removeItem(at: log)
        #expect(TraeTiming.graphs(database: database.path).isEmpty, "rotated files must leave the timing cache")
    }

    @Test func malformedContainersAndSymlinkLogsKeepTimingUnknown() throws {
        let fixture = Fixtures.root.appendingPathComponent("trae/timing-valid")
        let original = try Data(contentsOf: fixture.appendingPathComponent("logs/20261020T120000/Modular/ai_agent-0-2026-10-20.alaudalog"))
        #expect(throws: TraeTiming.Failure.self) { try TraeTiming.inflateLog(original, limit: 8) }
        #expect(throws: TraeTiming.Failure.self) { try TraeTiming.inflateLog(original + Data([1, 2])) }
        #expect(throws: TraeTiming.Failure.self) { try TraeTiming.inflateLog(Data(original.prefix(24))) }
        var unsupportedTable = original
        unsupportedTable[24] = 1
        #expect(throws: TraeTiming.Failure.self) { try TraeTiming.inflateLog(unsupportedTable) }
        let temp = try TempDirectory()
        let root = try Self.physicalURL(temp.path)
        let database = root.appendingPathComponent("ModularData/ai-agent/database.db")
        let log = root.appendingPathComponent("logs/launch/Modular/ai_agent-0.alaudalog")
        try FileManager.default.createDirectory(at: database.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: log.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.copyItem(at: fixture.appendingPathComponent("ModularData/ai-agent/database.db"), to: database)
        try FileManager.default.createSymbolicLink(at: log, withDestinationURL: fixture.appendingPathComponent("logs/20261020T120000/Modular/ai_agent-0-2026-10-20.alaudalog"))
        #expect(TraeTiming.files(database: database.path).isEmpty)
        #expect(try TraeDatabase(path: database.path).read().allSatisfy { !$0.record.isMeasured })
        try FileManager.default.removeItem(at: log)
        try Data("unsupported".utf8).write(to: log)
        #expect(try TraeDatabase(path: database.path).read().allSatisfy { !$0.record.isMeasured })
    }

    @Test(arguments: [false, true])
    func olderLogBudgetsPreserveRecentTiming(warmCache: Bool) throws {
        let temp = try TempDirectory()
        let root = try Self.physicalURL(temp.path)
        try Self.install("timing-valid", at: root)
        let path = root.appendingPathComponent("ModularData/ai-agent/database.db").path
        let log = root.appendingPathComponent("logs/launch/Modular/ai_agent-0.alaudalog")
        let unrelated = log.deletingLastPathComponent().appendingPathComponent("ai_agent-unrelated.alaudalog")
        let database = try TraeDatabase(path: path)
        if warmCache { _ = try database.read() }
        for size: UInt64 in [32 * 1024 * 1024 + 1, 32 * 1024 * 1024] {
            try Data().write(to: unrelated)
            let handle = try FileHandle(forWritingTo: unrelated)
            try handle.truncate(atOffset: size)
            try handle.close()
            // Individually oversized files are skipped even when newest. A bounded older file
            // crossing the aggregate budget stops history scanning without losing the valid graph.
            try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSinceNow: size > 32 * 1024 * 1024 ? 60 : -60)], ofItemAtPath: unrelated.path)
            for _ in 0..<2 {
                let records = try database.read().map(\.record)
                #expect(records.count == 3 && records.reduce(0) { $0 + $1.usage.output } == 340)
                let measured = try #require(records.first { $0.isMeasured })
                #expect(records.filter(\.isMeasured).count == 1)
                #expect(measured.usage.output == 240 && measured.endMs - (measured.startMs ?? 0) == 8000)
            }
        }
        try FileManager.default.removeItem(at: log)
        let records = try database.read().map(\.record)
        #expect(records.count == 3 && records.reduce(0) { $0 + $1.usage.output } == 340)
        #expect(records.allSatisfy { !$0.isMeasured })
    }

    @Test func earlierParserHistoryCacheReanalyzesUnchangedStores() throws {
        let temp = try TempDirectory()
        let root = Fixtures.root.appendingPathComponent("trae/timing-valid")
        let configuration = MonitorConfiguration(enabledSources: [.trae], traeRoot: root)
        let store = temp.file("history.sqlite").path
        let first = try HistoryScan.run(configuration: configuration, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        let key = "trae:" + TraeDatabase.databasePath(forRoot: root.path)
        let database = try HistoryDatabase(path: store)
        let stamp = try #require(try database.meta(key))
        #expect(stamp.hasSuffix("|trae-v2"))
        try database.setMeta(key, stamp.replacingOccurrences(of: "|trae-v2", with: ""))
        let upgraded = try HistoryScan.run(configuration: configuration, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        #expect(upgraded.sessionsAnalyzed > 0 && upgraded.days == first.days)
        let repeated = try HistoryScan.run(configuration: configuration, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        #expect(repeated.sessionsAnalyzed == 0 && repeated.days == upgraded.days)
    }

    @Test func nativeLifecycleFixturesRequireFreshUnambiguousRequestEvidence() throws {
        for example in try #require(try Fixtures.json("trae-activity-expected.json")["cases"] as? [[String: Any]]) {
            let path = Fixtures.root.appendingPathComponent(try #require(example.string("database")))
            let now = try #require(example.int64("nowMs"))
            let source = TraeSource(roots: [path.path], fileID: -3, clock: { now })
            var deduper = Deduper()
            let agents = source.seed(todayStartMs: Self.dayStart, deduper: &deduper).agents
            let expected = try #require(example["expected"] as? [[String: Any]])
            #expect(agents.count == expected.count, Comment(rawValue: example.string("name") ?? ""))
            for wanted in expected {
                let sessionId = try #require(wanted.string("sessionId"))
                let state = try #require(agents[path.path + "#" + sessionId])
                #expect(state.phase.rawValue == wanted.string("phase"), Comment(rawValue: example.string("name") ?? ""))
                #expect(state.inFlightStartMs == wanted.int64("inFlightStartMs"))
                #expect(state.model == wanted.string("model"))
                let lastLine = try #require(state.lastLineMs)
                #expect(lastLine <= now)
                if state.phase != .idle { #expect(now - lastLine <= SpeedEngine.staleMs) }
            }
        }
    }

    @Test func activeTraeUsesMarkedEstimatesAndNeverFabricatesUsage() throws {
        for (name, phase) in [("generating", AgentPhase.generating), ("tools", .tools), ("next-fetch", .generating), ("completed", .idle)] {
            let root = Fixtures.root.appendingPathComponent("trae/activity-" + name)
            let now: Int64 = name == "next-fetch" ? 1_792_497_625_000 : name == "tools" ? 1_792_497_624_000 : 1_792_497_622_000
            let session = TranscriptSession(configuration: MonitorConfiguration(enabledSources: [.trae], traeRoot: root), clock: { now })
            _ = session.seed(todayStartMs: Self.dayStart)
            let output = session.advance(nowMs: now, todayStartMs: Self.dayStart, emitsEvents: false).output
            #expect(output.today.requests == 1 && output.today.output == 240)
            #expect(output.agents.count == 1 && output.agents[0].phase == phase)
            #expect(output.liveEstimatedShare == (phase == .generating ? 1 : 0))
            #expect((output.live.output > 0) == (phase == .generating))
            #expect(session.responses.count == 1 && session.responses[0].isMeasured)
            #expect(!session.pollRecentFiles(nowMs: now))
        }
        let now: Int64 = 1_792_497_622_000
        let source = TraeSource(roots: [Fixtures.root.appendingPathComponent("trae/activity-generating").path], fileID: -3, clock: { now })
        var deduper = Deduper(), engine = SpeedEngine()
        let update = source.seed(todayStartMs: Self.dayStart, deduper: &deduper)
        for (id, state) in update.agents { engine.setAgent(EngineAgent(id: id, state: state)) }
        // Without inserting the completed reference, generation still has no speed estimate and adds no usage.
        let withoutReference = engine.advance(nowMs: now, todayStartMs: Self.dayStart, emitsEvents: false)
        #expect(withoutReference.agents.first?.phase == .generating)
        #expect(withoutReference.live == .zero && withoutReference.liveEstimatedShare == 0 && withoutReference.today.requests == 0)
        let temp = try TempDirectory()
        let root = try Self.physicalURL(temp.path)
        try Self.install("activity-generating", at: root)
        let path = root.appendingPathComponent("ModularData/ai-agent/database.db").path
        // Only a synthetic fixture is rewritten: remove all optional exact proofs, retaining ordinary final usage.
        try TraeSnapshot.read(path: path).write(to: URL(fileURLWithPath: path))
        let writer = try SQLiteConnection(path: path, mode: .readWriteCreate)
        try writer.execute("DROP TABLE server_history_info")
        let noProofs = TranscriptSession(configuration: MonitorConfiguration(enabledSources: [.trae], traeRoot: root), clock: { now })
        _ = noProofs.seed(todayStartMs: Self.dayStart)
        let unmeasured = noProofs.advance(nowMs: now, todayStartMs: Self.dayStart, emitsEvents: false).output
        #expect(unmeasured.agents.first?.phase == .generating)
        #expect(unmeasured.today.requests == 1 && unmeasured.today.output == 240)
        #expect(unmeasured.live == .zero && unmeasured.liveEstimatedShare == 0 && noProofs.responses.allSatisfy { !$0.isMeasured })
    }

    @Test func liveLifecycleUpdatesStopDuringToolsAndKeepCompletionCountsStable() async throws {
        let temp = try TempDirectory()
        let root = try Self.physicalURL(temp.path)
        try Self.install("activity-generating", at: root)
        let clock = OSAllocatedUnfairLock(initialState: Int64(1_792_497_622_000))
        let session = TranscriptSession(configuration: MonitorConfiguration(enabledSources: [.trae], traeRoot: root), clock: { clock.withLock { $0 } })
        _ = session.seed(todayStartMs: Self.dayStart)
        for (name, now, phase) in [("tools", Int64(1_792_497_624_000), AgentPhase.tools),
                                   ("next-fetch", 1_792_497_625_000, .generating), ("completed", 1_792_497_626_000, .idle)] {
            try Self.install("activity-" + name, at: root)
            clock.withLock { $0 = now }
            try await Task.sleep(for: .milliseconds(1100))
            #expect(session.pollRecentFiles(nowMs: now))
            let (output, events) = session.advance(nowMs: now, todayStartMs: Self.dayStart, emitsEvents: true)
            #expect(output.agents.first?.phase == phase)
            #expect(output.liveEstimatedShare == (phase == .generating ? 1 : 0))
            #expect(output.today.requests == 1 && output.today.output == 240 && session.responses.count == 1)
            #expect(!events.contains { if case .responseCompleted = $0 { true } else { false } })
        }
    }

    @Test func activeTurnCompletedCallsSeedReferencesWithoutCountingUsage() throws {
        for example in try #require(try Fixtures.json("trae-live-reference-expected.json")["cases"] as? [[String: Any]]) {
            let name = try #require(example.string("name"))
            let root = Fixtures.root.appendingPathComponent(try #require(example.string("root")))
            let now = try #require(example.int64("nowMs"))
            let session = TranscriptSession(configuration: MonitorConfiguration(enabledSources: [.trae], traeRoot: root), clock: { now })
            _ = session.seed(todayStartMs: Self.dayStart)
            let (output, events) = session.advance(nowMs: now, todayStartMs: Self.dayStart, emitsEvents: false)
            let agent = try #require(output.agents.first)
            #expect(agent.phase.rawValue == example.string("phase"), Comment(rawValue: name))
            #expect(Int64(agent.agent.referenceResponses.count) == example.int64("expectedReferenceCount"), Comment(rawValue: name))
            #expect(abs(output.live.output - (example["expectedLiveOutput"] as! NSNumber).doubleValue) < 0.0001, Comment(rawValue: name))
            #expect(Int64(output.today.requests) == example.int64("expectedRequests"), Comment(rawValue: name))
            #expect(Int64(output.today.output) == example.int64("expectedOutput"), Comment(rawValue: name))
            #expect(output.liveEstimatedShare == (output.live.output > 0 ? 1 : 0), Comment(rawValue: name))
            #expect(session.responses.count == output.today.requests, Comment(rawValue: name))
            #expect(events.isEmpty)
            if name != "completed" {
                #expect(output.averageToday == nil && output.lastResponse == nil)
                #expect(output.history.allSatisfy { $0.measured == .zero })
                #expect(output.report.counters == .zero)
            }
        }
    }

    @Test func liveReferencesExpireAndNeverOverrideCountedRecords() throws {
        let now: Int64 = 1_792_497_615_000
        let root = Fixtures.root.appendingPathComponent("trae/live-reference-next-request")
        let source = TraeSource(roots: [root.path], fileID: -3, clock: { now })
        var deduper = Deduper()
        let update = source.seed(todayStartMs: Self.dayStart, deduper: &deduper)
        let entry = try #require(update.agents.first)
        let original = EngineAgent(id: entry.key, state: entry.value)
        #expect(original.referenceResponses.count == 1)
        for lastLine in [now - SpeedEngine.staleMs - 1, now + 1] {
            var agent = original, engine = SpeedEngine()
            agent.lastLineMs = lastLine
            engine.setAgent(agent)
            let output = engine.advance(nowMs: now, todayStartMs: Self.dayStart, emitsEvents: false)
            #expect(output.live == .zero && output.today.requests == 0)
        }
        var engine = SpeedEngine()
        engine.setAgent(original)
        var counted = try #require(original.referenceResponses.first)
        counted.startMs = nil
        engine.upsert(counted, project: "test", isCompletion: false, emitsEvents: false)
        let output = engine.advance(nowMs: now, todayStartMs: Self.dayStart, emitsEvents: false)
        #expect(output.live == .zero, "counted usage owns its key even when its timing is unavailable")
        #expect(output.today.requests == 1 && output.today.output == 240)
        #expect(output.averageToday == nil && output.report.counters == .zero)
    }

    @Test func activeReferenceLifecycleClearsAtCompletionWithoutDuplicateCounts() async throws {
        let temp = try TempDirectory()
        let root = try Self.physicalURL(temp.path)
        let clock = OSAllocatedUnfairLock(initialState: Int64(1_792_497_605_000))
        try Self.install("live-reference-before-first-completion", at: root)
        let session = TranscriptSession(configuration: MonitorConfiguration(enabledSources: [.trae], traeRoot: root), clock: { clock.withLock { $0 } })
        _ = session.seed(todayStartMs: Self.dayStart)
        for (name, now, refs, outputTokens, requests, live) in [
            ("tools", Int64(1_792_497_610_000), 1, 0, 0, 0.0),
            ("next-request", 1_792_497_615_000, 1, 0, 0, 30.0),
            ("completed", 1_792_497_622_000, 0, 840, 2, 0.0)
        ] {
            try Self.install("live-reference-" + name, at: root)
            clock.withLock { $0 = now }
            try await Task.sleep(for: .milliseconds(1100))
            #expect(session.pollRecentFiles(nowMs: now))
            let (output, _) = session.advance(nowMs: now, todayStartMs: Self.dayStart, emitsEvents: true)
            #expect(output.agents.first?.agent.referenceResponses.count == refs)
            #expect(output.today.output == outputTokens && output.today.requests == requests)
            #expect(abs(output.live.output - live) < 0.0001)
            #expect(!session.pollRecentFiles(nowMs: now))
            let repeated = session.advance(nowMs: now, todayStartMs: Self.dayStart, emitsEvents: false).output
            #expect(repeated.today == output.today && session.responses.count == requests)
        }
        let restart = TranscriptSession(configuration: MonitorConfiguration(enabledSources: [.trae], traeRoot: root), clock: { clock.withLock { $0 } })
        _ = restart.seed(todayStartMs: Self.dayStart)
        let afterRestart = restart.advance(nowMs: clock.withLock { $0 }, todayStartMs: Self.dayStart, emitsEvents: false).output
        #expect(afterRestart.today.requests == 2 && afterRestart.today.output == 840)
        #expect(afterRestart.agents.first?.agent.referenceResponses.isEmpty == true)
    }

    @Test func asynchronousReadsCoalesceAndRejectStaleCompletions() async throws {
        let temp = try TempDirectory()
        let root = try Self.physicalURL(temp.path)
        try Self.install("timing-valid", at: root)
        let path = root.appendingPathComponent("ModularData/ai-agent/database.db")
        let source = TraeSource(roots: [root.path], fileID: -3, clock: { Self.now })
        var deduper = Deduper()
        #expect(source.seed(todayStartMs: Self.dayStart, deduper: &deduper).upserts.count == 3)
        var jobs: [TraeSource.ReadJob] = []
        source.backgroundReads = { jobs.append($0) }
        #expect(source.seed(todayStartMs: Self.dayStart, deduper: &deduper).isEmpty)
        #expect(jobs.count == 1)
        let first = jobs[0].perform()
        try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSince1970: 1_792_497_800)], ofItemAtPath: path.path)
        for _ in 0..<4 {
            #expect(source.refreshIfChanged(deduper: &deduper) == nil)
            #expect(source.refresh(deduper: &deduper).isEmpty)
        }
        #expect(jobs.count == 1, "a slow read never creates a backlog")
        let replacement = TraeSource(roots: [root.path], fileID: -3)
        #expect(replacement.complete(first, deduper: &deduper) == nil, "a restarted source rejects old jobs")
        #expect(source.complete(first, deduper: &deduper)?.upserts.isEmpty == true)
        #expect(source.complete(first, deduper: &deduper) == nil, "completion applies only once")
        try await Task.sleep(for: .milliseconds(1100))
        #expect(source.refreshIfChanged(deduper: &deduper)?.isEmpty == true)
        #expect(jobs.count == 2, "changes during a read are picked up once it finishes")
        let second = jobs[1].perform()
        try FileManager.default.removeItem(at: path)
        let removed = try #require(source.complete(second, deduper: &deduper))
        #expect(removed.upserts.isEmpty && !removed.removedAgents.isEmpty)
        #expect(source.activeSessionCount(todayStartMs: Self.dayStart) == 0)
    }

    @Test func lateExactUsageAndRotatedLogsPreserveStableResponseIdentities() throws {
        let temp = try TempDirectory()
        let root = try Self.physicalURL(temp.path)
        try Self.install("timing-valid", at: root)
        let source = TraeSource(roots: [root.path], fileID: -3)
        var deduper = Deduper()
        let original = source.seed(todayStartMs: Self.dayStart, deduper: &deduper)
        #expect(original.upserts.count == 3)
        let configuration = MonitorConfiguration(enabledSources: [.trae], traeRoot: root)
        let store = temp.file("history.sqlite").path
        let before = try HistoryScan.run(configuration: configuration, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        try Self.install("timing-multi-call", at: root)
        let expanded = source.seed(todayStartMs: Self.dayStart, deduper: &deduper)
        #expect(expanded.upserts.count == 1 && expanded.upserts[0].0.record.key == "trae:history:s-main:history-earlier")
        #expect(expanded.upserts[0].0.isCompletion)
        let withTiming = try HistoryScan.run(configuration: configuration, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        #expect(withTiming.days.reduce(0) { $0 + $1.totals.requests } == before.days.reduce(0) { $0 + $1.totals.requests } + 1)
        try FileManager.default.removeItem(at: root.appendingPathComponent("logs/launch/Modular/ai_agent-0.alaudalog"))
        let rotated = source.seed(todayStartMs: Self.dayStart, deduper: &deduper)
        #expect(rotated.upserts.count == 2 && rotated.upserts.allSatisfy { !$0.0.isCompletion && !$0.0.record.isMeasured })
        let withoutTiming = try HistoryScan.run(configuration: configuration, timeZone: TimeZone(secondsFromGMT: 0)!, storePath: store)
        #expect(withoutTiming.days.map(\.totals) == withTiming.days.map(\.totals))
        #expect(withoutTiming.days.reduce(0) { $0 + $1.measuredMs } == 0)
        let copied = TraeSource(roots: [root.path, Fixtures.root.appendingPathComponent("trae/timing-multi-call").path], fileID: -3)
        var copyDeduper = Deduper()
        #expect(copied.seed(todayStartMs: Self.dayStart, deduper: &copyDeduper).upserts.count == 4)
        try Self.install("timing-multi-call", at: root)
        let path = root.appendingPathComponent("ModularData/ai-agent/database.db").path
        try TraeSnapshot.read(path: path).write(to: URL(fileURLWithPath: path))
        let writer = try SQLiteConnection(path: path, mode: .readWriteCreate)
        try writer.execute("""
            INSERT INTO chat_turn(id,session_id,turn_id,turn_status,context,created_at,updated_at,deleted_at,response_message_id)
            SELECT id+100,session_id,turn_id||'-duplicate',turn_status,context,created_at,updated_at,deleted_at,response_message_id
            FROM chat_turn WHERE turn_id='t-one'
            """)
        let ambiguous = try TraeDatabase(path: path).read().map(\.record)
        #expect(ambiguous.count == 4 && !ambiguous.contains { $0.key.hasPrefix("trae:history:") })
        #expect(ambiguous.first { $0.key == "trae:s-main:t-one" }?.isMeasured == true,
                "a reused final anchor blocks expansion without changing the legacy final response")
    }
}
