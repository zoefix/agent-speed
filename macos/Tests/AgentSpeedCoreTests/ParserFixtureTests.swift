import Foundation
import Testing
@testable import AgentSpeedCore

@Suite("Parser fixtures")
struct ParserFixtureTests {
    struct FileResult {
        var state: AgentState
        var responses: [ResponseRecord]
    }

    static func family(of relative: String) -> TranscriptFamily {
        if relative.hasPrefix("codex/") { return .codex }
        // A Codex rollout in an OpenClaw agent's Codex home (METRICS 2).
        if relative.hasPrefix("openclaw/"), relative.contains("/codex-home/sessions/") { return .codex }
        if relative.hasPrefix("gemini/") { return .gemini }
        if relative.hasPrefix("omp/") { return .omp }
        if relative.hasPrefix("pi/") { return .pi }
        if relative.hasPrefix("workbuddy/.workbuddy-ai/") { return .workbuddyAi }
        if relative.hasPrefix("workbuddy/.workbuddy/") { return .workbuddy }
        if relative.hasPrefix("droid/") { return .droid }
        return relative.hasPrefix("dsh/") ? .dsh : .claude
    }

    /// The fixture folder `omp` plays `~/.omp`: its automatic roots are the default root and every profile.
    static var ompRoots: [String] {
        TranscriptLocations.ompRoots(environment: ["PI_CONFIG_DIR": "omp"], home: Fixtures.root).map(\.path)
    }

    /// The fixture folder `pi` plays `~/.pi` with the fixtures folder as home; its settings name `~/pi/custom-sessions`.
    static func piHome(fixtures root: URL = Fixtures.root) -> PiHome {
        PiHome(environment: ["PI_CODING_AGENT_DIR": root.appendingPathComponent("pi/agent").path], home: root)
    }

    /// The root an omp or WorkBuddy fixture was found under; nil for other families.
    static func root(of relative: String) -> String? {
        let path = Fixtures.root.appendingPathComponent(relative).path
        switch family(of: relative) {
        case .omp: return ompRoots.first { path.hasPrefix($0 + "/") }
        case .workbuddy, .workbuddyAi: return WorkBuddyTests.fixtureRoots.map(\.1).first { path.hasPrefix($0 + "/") }
        default: return nil
        }
    }

    static func parser(for relative: String, fileID: FileID) -> TranscriptParser {
        TranscriptParser(family: family(of: relative), agentId: Fixtures.root.appendingPathComponent(relative).path,
                         fileID: fileID, root: root(of: relative))
    }

    static func runFixtures() throws -> (expected: [String: Any], results: [String: FileResult]) {
        var deduper = Deduper()
        return try runFixtures(deduper: &deduper)
    }

    /// Every file of `order` with one shared dedupe set, which the caller keeps (the Antigravity databases follow).
    static func runFixtures(deduper: inout Deduper) throws -> (expected: [String: Any], results: [String: FileResult]) {
        let expected = try Fixtures.json("parser-expected.json")
        let order = try #require(expected["order"] as? [String])
        var results: [String: FileResult] = [:]
        for (index, relative) in order.enumerated() {
            let url = Fixtures.root.appendingPathComponent(relative)
            var parser = Self.parser(for: relative, fileID: FileID(index))
            var keys: [String] = []
            var latest: [String: ResponseRecord] = [:]
            let consume = { (line: UnsafeRawBufferPointer) in
                parser.consume(rawLine: line, deduper: &deduper) { upsert in
                    if latest[upsert.record.key] == nil { keys.append(upsert.record.key) }
                    latest[upsert.record.key] = upsert.record
                }
            }
            if Self.family(of: relative) == .droid {
                // The history rule: the whole transcript, then one sidecar read (METRICS 4.16).
                var reader = TranscriptReader(path: url.path)
                let read = reader.read(onLineAt: { line, offset in
                    parser.consume(rawLine: line, offset: offset, deduper: &deduper) { _ in }
                })
                #expect(!read.failed, "\(relative) readable")
                parser.noteReadStart(read.startOffset)
                _ = parser.finishReadCycle { upsert in
                    if latest[upsert.record.key] == nil { keys.append(upsert.record.key) }
                    latest[upsert.record.key] = upsert.record
                }
            } else if TranscriptReader.isFrameStream(url.path) || TranscriptReader.isCompressedStream(url.path)
                || TranscriptReader.isDocument(url.path) {
                var reader = TranscriptReader(path: url.path)
                let read = reader.read(onMalformed: { parser.noteMalformedLine() }, onLine: consume)
                #expect(!read.failed, "\(relative) readable")
            } else {
                let data = try Data(contentsOf: url)
                forEachLine(in: data, consume)
            }
            results[relative] = FileResult(state: parser.state, responses: keys.compactMap { latest[$0] })
        }
        return (expected, results)
    }

    @Test func everyFileMatchesExpectations() throws {
        let (expected, results) = try Self.runFixtures()
        let files = try #require(expected["files"] as? [String: [String: Any]])
        #expect(files.count == results.count)
        for (relative, spec) in files {
            let result = try #require(results[relative], "missing \(relative)")
            let state = result.state
            #expect(state.source.rawValue == spec.string("source"), "\(relative) source")
            #expect(state.project == spec.string("project"), "\(relative) project")
            #expect(state.model == spec.string("model"), "\(relative) model")
            #expect(state.isSubagent == (spec["isSubagent"] as? Bool), "\(relative) isSubagent")
            #expect(state.phase.rawValue == spec.string("phase"), "\(relative) phase")
            #expect(state.inFlightStartMs == spec.int64("inFlightStartMs"), "\(relative) inFlightStartMs")
            #expect(state.lastLineMs == spec.int64("lastLineMs"), "\(relative) lastLineMs")
            #expect(state.malformedLines == spec.int("malformedLines"), "\(relative) malformedLines")

            let expectedResponses = try #require(spec["responses"] as? [[String: Any]])
            #expect(result.responses.count == expectedResponses.count, "\(relative) response count")
            for (actual, wanted) in zip(result.responses, expectedResponses) {
                #expect(actual.key == wanted.string("key"), "\(relative) key")
                #expect(actual.startMs == wanted.int64("startMs"), "\(relative) \(actual.key) startMs")
                #expect(actual.endMs == wanted.int64("endMs"), "\(relative) \(actual.key) endMs")
                #expect(actual.firstMs == wanted.int64("firstMs"), "\(relative) \(actual.key) firstMs")
                #expect(actual.usage.input == wanted.int("input"), "\(relative) \(actual.key) input")
                #expect(actual.usage.cachedInput == wanted.int("cachedInput"), "\(relative) \(actual.key) cachedInput")
                #expect(actual.usage.cacheWriteInput == wanted.int("cacheWriteInput"), "\(relative) \(actual.key) cacheWriteInput")
                #expect(actual.usage.output == wanted.int("output"), "\(relative) \(actual.key) output")
                #expect(actual.usage.reasoning == wanted.int("reasoning"), "\(relative) \(actual.key) reasoning")
                #expect(actual.model == wanted.string("model"), "\(relative) \(actual.key) model")
                #expect(actual.source == state.source, "\(relative) \(actual.key) source")
                #expect(actual.isFinal == (wanted["final"] as? Bool), "\(relative) \(actual.key) final")
                expectPricingFields(actual, wanted, "\(relative) \(actual.key)")
            }
            expectModeFields(fixtureCurrent(state, agentId: Fixtures.root.appendingPathComponent(relative).path),
                             result.responses, spec, relative)
            // The actions of 6.10, for the files the fixture lists them for.
            if let wanted = spec["actions"] as? [[String: Any]] {
                let actions = state.actions.actions.map(Self.json)
                #expect(NSArray(array: actions).isEqual(to: wanted), "\(relative) actions: \(actions)")
            }
        }
    }

    /// An action as `parser-expected.json` lists it.
    static func json(_ action: ToolAction) -> [String: Any] {
        ["tool": action.tool, "digest": action.call.digest.hex, "callMs": action.callMs,
         "resultMs": action.resultMs ?? NSNull(), "failed": action.failed ?? NSNull(), "model": action.model ?? NSNull()]
    }

    @Test func parentLinksMatchExpectations() throws {
        let (expected, results) = try Self.runFixtures()
        let files = try #require(expected["files"] as? [String: [String: Any]])
        let path = { (relative: String) in Fixtures.root.appendingPathComponent(relative).path }
        let members = results.map { relative, result in
            AgentParents.Member(
                id: path(relative),
                family: Self.family(of: relative),
                thread: result.state.thread
            )
        }
        let parents = AgentParents.resolve(members)
        for (relative, spec) in files {
            #expect(spec.keys.contains("parentFile"), "\(relative) parentFile")
            let parent = parents[path(relative)]
            let wanted = spec.string("parentFile").map(path)
            #expect(parent == wanted, "\(relative) parentFile")
        }
    }
}
