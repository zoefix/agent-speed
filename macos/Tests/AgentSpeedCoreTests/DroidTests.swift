import Darwin
import Foundation
import Testing
@testable import AgentSpeedCore

/// Factory Droid (METRICS 4.16): the shared fixtures, discovery, roots, sidecars, the allocation and the scripted read
/// cycles of `parser-expected.json` `droid`.
@Suite("Factory Droid")
struct DroidTests {
    static var droidFixtures: [String: Any] {
        get throws { try #require(try Fixtures.json("parser-expected.json")["droid"] as? [String: Any]) }
    }

    static var sessionsRoot: URL { Fixtures.root.appendingPathComponent("droid/.factory/sessions", isDirectory: true) }

    // MARK: - Fixtures

    @Test func fixturesMatchTheReference() throws {
        let (expected, results) = try ParserFixtureTests.runFixtures()
        let files = try #require(expected["files"] as? [String: [String: Any]])
        let droid = files.filter { $0.key.hasPrefix("droid/") }
        #expect(droid.count == 10)
        for (relative, spec) in droid {
            let summary = OmpTests.summary(try #require(results[relative]))
            for (field, wanted) in spec where !["parentFile", "effort", "speed", "responseModes"].contains(field) {
                let actual = summary[field] ?? NSNull()
                #expect((actual as AnyObject).isEqual(wanted), "\(relative) \(field): \(actual)")
            }
        }
    }

    @Test func tracesMatchPhaseAfterEveryCheckpoint() throws {
        let traces = try #require(try Self.droidFixtures["traces"] as? [[String: Any]])
        #expect(traces.count == 18)
        for trace in traces {
            let file = try #require(trace.string("file"))
            let afterLine = trace.int("afterLine")
            var deduper = try OmpTests.deduper(before: file)
            var parser = ParserFixtureTests.parser(for: file, fileID: 999)
            try GeminiTests.feed(file, into: &parser, deduper: &deduper, lines: afterLine)
            #expect(parser.state.phase.rawValue == trace.string("phase"), "\(file) line \(afterLine) phase")
            #expect(parser.state.inFlightStartMs == trace.int64("inFlightStartMs"), "\(file) line \(afterLine) inFlightStartMs")
        }
    }

    @Test func discoveryFindsSessionsInTheRootAndOneFolderBelow() throws {
        let wanted = try #require(try Self.droidFixtures["discover"] as? [String])
        var index = DirectoryIndex(root: Self.sessionsRoot.path, names: .droidSession)
        let prefix = Fixtures.root.path + "/"
        #expect(index.scan().map { String($0.dropFirst(prefix.count)) }.sorted() == wanted)
        let names = TranscriptNames.droidSession
        #expect(names.accepts(relativeComponents: ["a.jsonl"]) && names.accepts(relativeComponents: ["-w-p", "a.jsonl"]))
        #expect(!names.accepts(relativeComponents: ["-w-p", "nested", "a.jsonl"]))
        #expect(!names.accepts(relativeComponents: [".trash", "a.jsonl"]) && !names.accepts(relativeComponents: ["-w-p", ".a.jsonl"]))
        #expect(!names.accepts(relativeComponents: ["-w-p", "a.settings.json"]) && !names.accepts(relativeComponents: ["a.jsonl.tmp"]))
    }

    @Test func sidecarChangesStandForTheirSession() {
        let family = TranscriptFamily.droid
        #expect(family.transcriptPath(forChanged: "/r/-w-p/s1.settings.json") == "/r/-w-p/s1.jsonl")
        #expect(family.transcriptPath(forChanged: "/r/-w-p/s1.settings.json.bak") == "/r/-w-p/s1.settings.json.bak")
        #expect(family.transcriptPath(forChanged: "/r/-w-p/.s1.settings.json") == "/r/-w-p/.s1.settings.json")
        #expect(family.transcriptPath(forChanged: "/r/-w-p/s1.jsonl") == "/r/-w-p/s1.jsonl")
        #expect(DroidPaths.sidecarPath(ofTranscript: "/r/s1.jsonl") == "/r/s1.settings.json")
        #expect(DroidPaths.sibling("s0", of: "/r/-w-p/s1.jsonl") == "/r/-w-p/s0.jsonl")
    }

    @Test func statFoldsInTheSidecarsModificationTime() throws {
        let directory = try TempDirectory()
        let transcript = try directory.write("s/-w-p/a.jsonl", "{}\n", modified: Date(timeIntervalSince1970: 1_000))
        #expect(DroidPaths.stat(transcript.path)?.modifiedNs == 1_000_000_000_000)
        try directory.write("s/-w-p/a.settings.json", "{}", modified: Date(timeIntervalSince1970: 2_000))
        let stat = try #require(DroidPaths.stat(transcript.path))
        #expect(stat.modifiedNs == 2_000_000_000_000 && stat.size == 3, "the newer time, the transcript's own size")
        try FileManager.default.createSymbolicLink(atPath: directory.file("s/-w-p/b.jsonl").path, withDestinationPath: transcript.path)
        #expect(DroidPaths.stat(directory.file("s/-w-p/b.jsonl").path) == nil)
    }

    // MARK: - Roots

    @Test func rootsFollowTheSetting() throws {
        let cases = try #require(try Self.droidFixtures["roots"] as? [[String: Any]])
        #expect(cases.count == 6)
        for spec in cases {
            let directory = try TempDirectory()
            for folder in spec["make"] as? [String] ?? [] {
                try FileManager.default.createDirectory(at: directory.file(folder), withIntermediateDirectories: true)
            }
            let root = spec.string("setting").map { DroidPaths.sessionsFolder(directory.file($0)) }
                ?? DroidPaths.defaultRoot(home: directory.url)
            let relative = String(root.path.dropFirst(directory.path.count + 1))
            #expect(relative == spec.string("expected"), "\(spec)")
            if let setting = spec.string("setting") {
                let configuration = MonitorConfiguration(droidRoot: directory.file(setting))
                #expect(configuration.resolvedRoots(for: .droid) == [root.path])
            }
        }
    }

    @Test func theAutomaticRootIsTheFactorySessionsFolder() {
        #expect(DroidPaths.defaultRoot(home: URL(fileURLWithPath: "/Users/dev")).path == "/Users/dev/.factory/sessions")
        #expect(MonitorConfiguration().resolvedRoots(for: .droid) == [LiveMonitor.defaultDroidRoot.path])
        var configuration = MonitorConfiguration(droidRoot: URL(fileURLWithPath: "/Volumes/x/.factory/", isDirectory: true))
        #expect(configuration.resolvedRoots(for: .droid) == ["/Volumes/x/.factory"], "no sessions folder there: itself")
        #expect(configuration.requiresRescan(comparedTo: MonitorConfiguration()))
        configuration.droidRoot = nil
        #expect(!configuration.requiresRescan(comparedTo: MonitorConfiguration()))
    }

    // MARK: - Sidecars

    @Test func sidecarsReadLikeTheReference() throws {
        let cases = try #require(try Self.droidFixtures["sidecars"] as? [[String: Any]])
        #expect(cases.count == 15)
        let directory = try TempDirectory()
        for spec in cases {
            let name = try #require(spec.string("name"))
            let text = try #require(spec.string("text"))
            var data = spec.string("encoding") == "latin-1" ? try #require(text.data(using: .isoLatin1)) : Data(text.utf8)
            if let size = spec["size"] as? Int { data.append(Data(repeating: 0x20, count: size - data.count)) }
            let path = directory.file("x.settings.json")
            try data.write(to: path)
            let content = DroidSidecar.read(path.path)
            #expect((content != nil) == (spec["readable"] as? Bool), "\(name) readable")
            #expect(content?.model == spec.string("model"), "\(name) model")
            let counters = spec["counters"] as? [String: Any]
            #expect((content?.counters == nil) == (counters == nil), "\(name) counters")
            if let counters, let got = content?.counters {
                #expect(Self.counters(counters) == got, "\(name) counters \(got)")
            }
        }
    }

    @Test func aSymlinkedSidecarIsUnreadable() throws {
        let directory = try TempDirectory()
        let target = try directory.write("real.json", #"{"model":"m","tokenUsage":{"inputTokens":5}}"#)
        let link = directory.file("s.settings.json")
        try FileManager.default.createSymbolicLink(atPath: link.path, withDestinationPath: target.path)
        #expect(DroidSidecar.read(target.path)?.counters?.input == 5)
        #expect(DroidSidecar.read(link.path) == nil)
        #expect(DroidSidecar.read(directory.file("missing.settings.json").path) == nil)
    }

    @Test func repeatedMembersResolveLikePython() {
        func read(_ text: String) -> DroidSidecar.Content? {
            Array(text.utf8).withUnsafeBytes { DroidSidecar.parse($0) }
        }
        #expect(read(#"{"tokenUsage":{"inputTokens":5},"tokenUsage":7}"#)?.counters == nil)
        #expect(read(#"{"tokenUsage":{"inputTokens":5,"inputTokens":"x"}}"#)?.counters == .zero)
        #expect(read(#"{"model":"a","model":3}"#)?.model == nil)
        #expect(read(#"{"effectiveFactoryRouterModel":{"modelId":"r"},"effectiveFactoryRouterModel":[]}"#)?.model == nil)
        #expect(read(#"{"model":" \u00a0auto\t","effectiveFactoryRouterModel":{"modelId":" glm-5.3 "}}"#)?.model == "glm-5.3")
        #expect(read("\u{FEFF}\u{FEFF}{}") == nil, "only one byte order mark is removed")
        #expect(read(#"{"tokenUsage":{"thinkingTokens":true}} x"#) == nil)
    }

    /// A response in the shape of `reference.py`'s emitted rows.
    static func response(_ record: ResponseRecord) -> [String: Any] {
        func value(_ value: Any?) -> Any { value ?? NSNull() }
        return [
            "key": record.key, "startMs": value(record.startMs), "endMs": record.endMs, "model": value(record.model),
            "provider": value(record.provider), "fast": record.fast, "final": record.isFinal, "input": record.usage.input,
            "cachedInput": record.usage.cachedInput, "cacheWriteInput": record.usage.cacheWriteInput,
            "cacheWrite1hInput": record.usage.cacheWrite1hInput, "output": record.usage.output,
            "reasoning": record.usage.reasoning,
        ]
    }

    static func counters(_ object: [String: Any]) -> DroidCounters {
        DroidCounters(input: object.int64("inputTokens") ?? 0, cacheCreation: object.int64("cacheCreationTokens") ?? 0,
                      cacheRead: object.int64("cacheReadTokens") ?? 0, output: object.int64("outputTokens") ?? 0,
                      thinking: object.int64("thinkingTokens") ?? 0)
    }

    // MARK: - Allocation

    @Test func allocationsSplitByLargestRemainder() throws {
        let cases = try #require(try Self.droidFixtures["allocations"] as? [[String: Any]])
        #expect(cases.count == 7)
        for spec in cases {
            let weights = try #require(spec["weights"] as? [NSNumber]).map(\.int64Value)
            let shares = try #require(spec["shares"] as? [NSNumber]).map(\.int64Value)
            let total = try #require(spec.int64("total"))
            #expect(DroidAllocation.shares(of: total, weights: weights) == shares, "\(spec)")
        }
        #expect(DroidAllocation.shares(of: 1_000_000_000_000, weights: Array(repeating: DroidAllocation.maxWeight, count: 3))
            == [333_333_333_334, 333_333_333_333, 333_333_333_333])
        #expect(DroidAllocation.shares(of: 9, weights: []) == [])
    }

    @Test func eachFieldUsesItsWeight() {
        let growth = DroidCounters(input: 10, cacheCreation: 4, cacheRead: 100, output: 9, thinking: 3)
        let shares = DroidAllocation.allocate(growth, inputWeights: [1, 4], outputWeights: [2, 1])
        #expect(shares[0] == DroidCounters(input: 2, cacheCreation: 1, cacheRead: 20, output: 6, thinking: 2))
        #expect(shares[1] == DroidCounters(input: 8, cacheCreation: 3, cacheRead: 80, output: 3, thinking: 1))
        let tokens = shares[1].tokens
        #expect(tokens.input == 91 && tokens.cachedInput == 80 && tokens.cacheWriteInput == 3 && tokens.output == 3
            && tokens.reasoning == 1 && tokens.cacheWrite1hInput == 0)
    }

    // MARK: - Read cycles

    /// One followed session of the scripted read cycles, read the way the live session reads it: new lines with their
    /// offsets, then the sidecar, then the allocation.
    final class Tail {
        let path: String
        let fileID: FileID
        var reader: TranscriptReader
        var parser: TranscriptParser
        var latest: [String: ResponseRecord] = [:]
        var order: [String] = []

        init(path: String, fileID: FileID, atEnd: Bool) throws {
            self.path = path
            self.fileID = fileID
            let size = try #require(FileStat.lstat(path)).size
            reader = TranscriptReader(path: path, start: atEnd ? .end(size) : .beginning)
            parser = TranscriptParser(family: .droid, agentId: path, fileID: fileID)
            if atEnd { parser.takeBaseline(DroidSidecar.snapshot(DroidPaths.sidecarPath(ofTranscript: path))) }
        }

        func read(deduper: inout Deduper) {
            let result = reader.read(onReset: { self.parser = TranscriptParser(family: .droid, agentId: self.path, fileID: self.fileID) },
                                     onLineAt: { line, offset in
                                         self.parser.consume(rawLine: line, offset: offset, deduper: &deduper) { _ in }
                                     })
            parser.noteReadStart(result.startOffset)
            _ = parser.finishReadCycle { upsert in
                if latest[upsert.record.key] == nil { order.append(upsert.record.key) }
                latest[upsert.record.key] = upsert.record
            }
        }

        var responses: [ResponseRecord] { order.compactMap { latest[$0] } }
    }

    /// The raw text of every `lines` element of `droid.live.steps`, compacted the way `reference.py` writes them
    /// (`json.dumps(line, ensure_ascii=False, separators=(",", ":"))`): the fixture keeps Python's member order and
    /// escapes, so removing the whitespace outside strings gives the same bytes and so the same weights.
    static func liveLines() throws -> [[String]] {
        let data = try Data(contentsOf: Fixtures.root.appendingPathComponent("parser-expected.json"))
        return try data.withUnsafeBytes { buffer -> [[String]] in
            var scanner = JSONScanner(buffer)
            var steps: [[String]] = []
            func enter(_ name: StaticString) throws {
                while let key = try scanner.nextMember() {
                    if scanner.key(key, is: name) {
                        try scanner.beginObject()
                        return
                    }
                    try scanner.skipValue()
                }
                throw JSONScanError()
            }
            try scanner.beginObject()
            try enter("droid")
            try enter("live")
            while let key = try scanner.nextMember() {
                guard scanner.key(key, is: "steps"), try scanner.beginArrayIfPresent() else {
                    try scanner.skipValue()
                    continue
                }
                while try scanner.nextElement() {
                    try scanner.beginObject()
                    var lines: [String] = []
                    while let member = try scanner.nextMember() {
                        guard scanner.key(member, is: "lines"), try scanner.beginArrayIfPresent() else {
                            try scanner.skipValue()
                            continue
                        }
                        while try scanner.nextElement() {
                            lines.append(try scanner.withStoredText { String(decoding: minified($0), as: UTF8.self) })
                        }
                    }
                    steps.append(lines)
                }
            }
            return steps
        }
    }

    static func minified(_ raw: UnsafeRawBufferPointer) -> [UInt8] {
        var output: [UInt8] = []
        var inString = false
        var escaped = false
        for byte in raw {
            if inString {
                output.append(byte)
                if escaped {
                    escaped = false
                } else if byte == UInt8(ascii: "\\") {
                    escaped = true
                } else if byte == UInt8(ascii: "\"") {
                    inString = false
                }
            } else if ![0x20, 0x09, 0x0A, 0x0D].contains(byte) {
                if byte == UInt8(ascii: "\"") { inString = true }
                output.append(byte)
            }
        }
        return output
    }

    @Test func scriptedReadCyclesMatchTheReference() throws {
        let live = try #require(try Self.droidFixtures["live"] as? [String: Any])
        let steps = try #require(live["steps"] as? [[String: Any]])
        let lines = try Self.liveLines()
        #expect(lines.count == steps.count && steps.filter { $0["read"] != nil }.count == 17)
        let home = try TempDirectory()
        var deduper = Deduper()
        var tails: [String: Tail] = [:]
        for (number, (step, stepLines)) in zip(steps, lines).enumerated() {
            let label = "step \(number + 1)"
            if let relative = step.string("append") ?? step.string("rewrite") {
                let path = home.file(relative).path
                try FileManager.default.createDirectory(atPath: (path as NSString).deletingLastPathComponent,
                                                        withIntermediateDirectories: true)
                let bytes = Data(stepLines.map { $0 + "\n" }.joined().utf8)
                if step["append"] != nil {
                    if !FileManager.default.fileExists(atPath: path) { try Data().write(to: URL(fileURLWithPath: path)) }
                    let handle = try FileHandle(forWritingTo: URL(fileURLWithPath: path))
                    try handle.seekToEnd()
                    try handle.write(contentsOf: bytes)
                    try handle.close()
                } else {
                    try bytes.write(to: URL(fileURLWithPath: path + ".new"))
                    #expect(Darwin.rename(path + ".new", path) == 0)
                }
            } else if let relative = step.string("sidecar") {
                let path = DroidPaths.sidecarPath(ofTranscript: home.file(relative).path)
                if let text = step.string("text") {
                    try Data(text.utf8).write(to: URL(fileURLWithPath: path))
                } else {
                    let doc = try #require(step["doc"])
                    try JSONSerialization.data(withJSONObject: doc, options: [.prettyPrinted]).write(to: URL(fileURLWithPath: path))
                }
                let mtime = try #require(step.int64("mtime"))
                try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSince1970: Double(mtime) / 1000)],
                                                      ofItemAtPath: path)
            } else if let relative = step.string("open") {
                tails[relative] = try Tail(path: home.file(relative).path, fileID: FileID(tails.count),
                                           atEnd: step["atEnd"] as? Bool == true)
            } else if let relative = step.string("read") {
                let tail = try #require(tails[relative])
                tail.read(deduper: &deduper)
                let wanted = try #require(step["responses"] as? [[String: Any]])
                let got = tail.responses
                #expect(got.map(\.key) == wanted.compactMap { $0.string("key") }, "\(label) keys")
                for (record, want) in zip(got, wanted) {
                    let actual = Self.response(record) as NSDictionary
                    #expect(actual.isEqual(to: want), "\(label) \(record.key): \(actual) != \(want)")
                }
                let pending = try #require(step["pending"] as? [String: Any])
                #expect(tail.parser.droidPending == Self.counters(pending), "\(label) pending")
                if let phase = step.string("phase") { #expect(tail.parser.state.phase.rawValue == phase, "\(label) phase") }
            }
        }
    }

    // MARK: - History with unreadable parent sidecars

    /// Writes a `droid.history` `write` step: the file's text and its modification time (ms).
    static func write(_ step: [String: Any], in home: TempDirectory) throws {
        let relative = try #require(step.string("write"))
        let mtime = try #require(step.int64("mtime"))
        try home.write(relative, try #require(step.string("text")), modified: Date(timeIntervalSince1970: Double(mtime) / 1000))
    }

    /// One analysis the way `reference.py` collects it: every session in startup order, read whole with one sidecar
    /// read, skipping those whose own counters are unknown; `memory` carries the unreadable parent sidecars.
    static func analyse(_ home: TempDirectory, memory: inout [String: DroidPaths.Signature])
        -> (responses: [String: [ResponseRecord]], skipped: [String]) {
        var index = DirectoryIndex(root: home.file(".factory/sessions").path, names: .droidSession)
        let files = TranscriptDiscovery.files(at: index.scan(), family: .droid).sorted(by: TranscriptDiscovery.startupOrder)
        var deduper = Deduper()
        var responses: [String: [ResponseRecord]] = [:]
        var skipped: [String] = []
        for (number, file) in files.enumerated() {
            let relative = String(file.path.dropFirst(home.path.count + 1))
            var parser = TranscriptParser(family: .droid, agentId: file.path, fileID: FileID(number))
            parser.droidUnreadableParent = memory[file.path]
            var reader = TranscriptReader(path: file.path)
            let read = reader.read(onLineAt: { line, offset in
                parser.consume(rawLine: line, offset: offset, deduper: &deduper) { _ in }
            })
            parser.noteReadStart(read.startOffset)
            var records: [ResponseRecord] = []
            _ = parser.finishReadCycle { records.append($0.record) }
            memory[file.path] = parser.droidUnreadableParent
            if parser.droidOwnCountersUnknown {
                skipped.append(relative)
                continue
            }
            parser.finishHistory { records.append($0.record) }
            if !records.isEmpty { responses[relative] = records }
        }
        return (responses, skipped.sorted())
    }

    @Test func historySkipsSessionsWhoseOwnCountersAreUnknown() throws {
        let history = try #require(try Self.droidFixtures["history"] as? [String: Any])
        let steps = try #require(history["steps"] as? [[String: Any]])
        #expect(steps.filter { $0["analyze"] != nil }.count == 3)
        let home = try TempDirectory()
        var memory: [String: DroidPaths.Signature] = [:]
        for (number, step) in steps.enumerated() {
            if step["write"] != nil {
                try Self.write(step, in: home)
                continue
            }
            let (responses, skipped) = Self.analyse(home, memory: &memory)
            let wanted = try #require(step["responses"] as? [String: [[String: Any]]])
            #expect(skipped == step["skipped"] as? [String], "analysis at step \(number + 1) skipped")
            #expect(Set(responses.keys) == Set(wanted.keys), "analysis at step \(number + 1) sessions")
            for (relative, records) in responses {
                let want = wanted[relative] ?? []
                #expect(records.count == want.count, "\(relative)")
                for (record, expected) in zip(records, want) {
                    #expect((Self.response(record) as NSDictionary).isEqual(to: expected), "\(relative) \(record.key)")
                }
            }
        }
    }

    /// The same steps through the daily history itself: a skipped session is not stored, so the next analysis reads it
    /// again, and the remembered parent signature carries over between analyses of one analyser.
    @Test func theHistoryAnalysesASkippedSessionAgain() throws {
        let history = try #require(try Self.droidFixtures["history"] as? [String: Any])
        let steps = try #require(history["steps"] as? [[String: Any]])
        let home = try TempDirectory()
        let analyzer = try HistoryAnalyzer(database: HistoryDatabase.inMemory())
        let configuration = MonitorConfiguration(enabledSources: [.droid], droidRoot: home.file(".factory"))
        var analysed: [Int] = []
        for step in steps {
            if step["write"] != nil {
                try Self.write(step, in: home)
                continue
            }
            let report = try analyzer.run(configuration: configuration, options: HistoryAnalyzer.Options(timeZone: HistoryTests.utc))
            analysed.append(report.filesAnalyzed)
            let wanted = (step["responses"] as? [String: [[String: Any]]] ?? [:]).values.flatMap { $0 }
            let days = analyzer.days(sources: [.droid])
            #expect(days.reduce(0) { $0 + $1.totals.requests } == wanted.count)
            #expect(days.reduce(0) { $0 + $1.totals.output } == wanted.reduce(0) { $0 + (($1["output"] as? Int) ?? 0) })
            #expect(days.reduce(0) { $0 + $1.totals.input } == wanted.reduce(0) { $0 + (($1["input"] as? Int) ?? 0) })
        }
        #expect(analysed == [2, 3, 0], "the parents first; then the changed parent and both successors; then nothing")
    }

    // MARK: - Lines

    @Test func linesClassifyLikeTheReference() {
        func line(_ text: String) -> LineExtraction<DroidLine> {
            Array(text.utf8).withUnsafeBytes { DroidLineExtractor.extract($0) }
        }
        let ts = #""timestamp":"2026-10-20T13:00:00.000Z""#
        #expect(line(#"{"type":"session_start","cwd":"C:\\w\\halo\\","parent":"s-1","callingSessionId":"../x"}"#)
            == .line(.sessionStart(cwd: #"C:\w\halo\"#, parent: "s-1", callingSessionId: nil)))
        #expect(line(#"{"type":"session_start","parent":"-s","callingSessionId":"\#(String(repeating: "a", count: 129))"}"#)
            == .line(.sessionStart(cwd: nil, parent: nil, callingSessionId: nil)))
        #expect(line(#"{"type":"agent_turn_outcome","turnId":"t"}"#) == .line(.turnOutcome))
        #expect(line(#"{"type":"message","message":{"role":"user"}}"#) == .line(.ignored))
        #expect(line(#"{"type":"message",\#(ts),"message":{"role":"user","visibility":"user_only"}}"#)
            == .line(.timed(timestampMs: 1_792_501_200_000, record: .notice)))
        #expect(line(#"{"type":"message",\#(ts),"message":{"role":"user","visibility":7}}"#)
            == .line(.timed(timestampMs: 1_792_501_200_000, record: .input)))
        #expect(line(#"{"type":"message",\#(ts),"message":"text"}"#) == .line(.timed(timestampMs: 1_792_501_200_000, record: .other)))
        #expect(line(#"{"type":"message","id":"","message":{"role":"assistant","modelId":"  ","content":{"type":"tool_use"}},\#(ts)}"#)
            == .line(.timed(timestampMs: 1_792_501_200_000, record: .assistant(DroidAssistant(
                id: nil, modelId: nil, effort: nil, asksForTools: false)))))
        let call = line(#"{"type":"message","id":"m1","message":{"role":"assistant","modelId":" custom:X-0 ","reasoningEffort":"none","content":[{"type":"text"},{"type":"tool_use","id":"t"}]},\#(ts)}"#)
        guard case .line(.timed(_, .assistant(let assistant))) = call else {
            Issue.record("not an assistant line: \(call)")
            return
        }
        #expect(assistant.id == "m1" && assistant.modelId == "custom:X-0" && assistant.asksForTools)
        #expect(assistant.effort?.value == "off")
        #expect(line(#"{"type":"compaction_state",\#(ts)}"#) == .line(.timed(timestampMs: 1_792_501_200_000, record: .compaction)))
        #expect(line(#"{"type":"todo_state",\#(ts),"timestamp":"no"}"#) == .line(.ignored), "the last member wins")
        #expect(line("  \r") == .blank && line("[1]") == .malformed && line(#"{"type":"message","x":NaN}"#) == .malformed)
    }

    @Test func customModelsNameTheCustomProvider() {
        #expect(DroidParser.provider(ofModel: "custom:GPT-5.5-[OpenAI-BYOK]-0") == "custom")
        #expect(DroidParser.provider(ofModel: "CUSTOM:x") == "custom")
        #expect(DroidParser.provider(ofModel: "custom") == nil && DroidParser.provider(ofModel: "customer:x") == nil)
        #expect(DroidParser.provider(ofModel: "claude-opus-5-5") == nil)
    }

    // MARK: - Source

    @Test func sourceOrderAndProcessRow() {
        #expect(SourceKind.allCases.firstIndex(of: .droid) == 16)
        #expect(SourceKind.allCases.firstIndex(of: .droid) == SourceKind.allCases.firstIndex(of: .hermes)! + 1)
        #expect(SourceSet(.droid).rawValue == 1 << 16)
        #expect(TranscriptFamily(.droid) == .droid && TranscriptFamily.droid.sources == [.droid])
        let mac = AgentProcessRules(platform: TrafficPlatform(kind: .macos, home: "/Users/dev"))
        func source(_ executable: String, _ arguments: [String] = []) -> SourceKind? {
            mac.source(executable: executable, arguments: { arguments }, ancestorExecutables: [])
        }
        #expect(source("/Users/dev/.local/bin/droid") == .droid)
        #expect(source("/opt/homebrew/Caskroom/droid/0.250.0/droid", ["droid", "exec", "--input-format", "stream-jsonrpc"]) == .droid)
        #expect(source("/opt/homebrew/lib/node_modules/droid/node_modules/@factory/cli-darwin-arm64/bin/droid") == .droid)
        #expect(source("/opt/homebrew/bin/node", ["node", "/opt/homebrew/lib/node_modules/droid/bin/droid.js"]) == nil)
        #expect(source("/usr/local/bin/droids") == nil && source("/usr/local/bin/Droid") == nil)
        let status = AgentStatus(id: "/Users/dev/.factory/sessions/-w-p/s1.jsonl", source: .droid, project: "p", model: nil,
                                 phase: .idle, isSubagent: false, live: .zero, recentSpeed: nil, lastActivity: Date())
        #expect(TrafficAgent(status).transcript == status.id)
    }
}
