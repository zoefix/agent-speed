import Foundation
import Testing
@testable import AgentSpeedCore

/// `spec/fixtures/work-signal-cases.json` (METRICS 6.10), as `spec/tools/work_signals.py` checks it.
@Suite("Work signal fixture cases")
struct WorkSignalCaseTests {
    static let file = "work-signal-cases.json"

    static func cases() throws -> [String: Any] { try Fixtures.json(file) }

    @Test func digests() throws {
        let specs = try #require(Self.cases()["digests"] as? [[String: Any]])
        let inputs = try StoredFixture.digestInputs(file: Self.file)
        #expect(inputs.count == specs.count)
        for (input, spec) in zip(inputs, specs) {
            let digest = input.withUnsafeBytes(ToolDigest.init)
            #expect(digest.hex == spec.string("expect"), "\(String(decoding: input, as: UTF8.self))")
        }
    }

    @Test func signals() throws {
        for spec in try #require(Self.cases()["signals"] as? [[String: Any]]) {
            let name = spec.string("name") ?? ""
            let actions = try #require(spec["actions"] as? [[String: Any]]).map { action in
                ToolAction(
                    call: ToolCall(tool: try #require(action.string("tool")),
                                   digest: try #require(ToolDigest(hex: action.string("digest") ?? ""))),
                    callMs: try #require(action.int64("at")),
                    failed: action["failed"] as? Bool
                )
            }
            let signals = WorkSignals(actions, nowMs: try #require(spec.int64("nowMs")))
            let expected = try #require(spec["expect"] as? [String: Any])
            #expect(signals.looping == (expected["looping"] as? NSNumber)?.intValue, "\(name) looping")
            #expect(signals.repeating == (expected["repeating"] as? NSNumber)?.intValue, "\(name) repeating")
            EngineCaseTests.expectClose(signals.novelty, expected.double("novelty"), "\(name) novelty")
            #expect(signals.failed == expected.int("failed"), "\(name) failed")
            #expect(signals.recovered == expected.int("recovered"), "\(name) recovered")
        }
    }

    struct Row: Equatable {
        var at: Int64
        var tool: String
        var digest: String
        var failed: Bool?
    }

    @Test func files() throws {
        let specs = try #require(Self.cases()["files"] as? [[String: Any]])
        let files = try StoredFixture.fileLines(file: Self.file)
        #expect(files.count == specs.count)
        for (lines, spec) in zip(files, specs) {
            let name = spec.string("name") ?? ""
            let family: TranscriptFamily = switch spec.string("format") {
            case "claude": .claude
            case "codex": .codex
            case "pi": .pi
            case "droid": .droid
            default: .omp
            }
            var log = ActionLog()
            for line in lines {
                line.text.withUnsafeBytes { log.record(ToolEventExtractor.events(family, $0), at: line.atMs, model: nil) }
            }
            let got = log.actions.map { Row(at: $0.callMs, tool: $0.call.tool, digest: $0.call.digest.hex, failed: $0.failed) }
            let expected = try #require(spec["expect"] as? [[String: Any]]).map { action in
                Row(at: action.int64("at") ?? 0, tool: action.string("tool") ?? "", digest: action.string("digest") ?? "",
                    failed: action["failed"] as? Bool)
            }
            #expect(got == expected, "\(name)")
        }
    }
}

/// Fixture values as a transcript line would store them. Objects are compacted the way the reference writes lines
/// (`json.dumps(separators=(",", ":"), ensure_ascii=False)`) from the raw fixture text, because a parsed dictionary
/// would lose the key order their digests depend on.
enum StoredFixture {
    struct Line {
        var atMs: Int64
        var text: [UInt8]
    }

    /// Each `digests[].input`: a string's text, an object's compact JSON.
    static func digestInputs(file: String) throws -> [[UInt8]] {
        var inputs: [[UInt8]] = []
        try scanDocument(file) { key, scanner in
            guard scanner.key(key, is: "digests") else { return false }
            try forEachObject(&scanner) { member, scanner in
                guard scanner.key(member, is: "input") else { return false }
                let isString = scanner.nextIsString
                inputs.append(try scanner.withStoredText { isString ? Array($0) : compact($0) })
                return true
            }
            return true
        }
        return inputs
    }

    /// Each `files[].lines`, every `[at, object]` with the object's compact JSON.
    static func fileLines(file: String) throws -> [[Line]] {
        var files: [[Line]] = []
        try scanDocument(file) { key, scanner in
            guard scanner.key(key, is: "files") else { return false }
            try forEachObject(&scanner) { member, scanner in
                guard scanner.key(member, is: "lines") else { return false }
                var lines: [Line] = []
                guard try scanner.beginArrayIfPresent() else { throw JSONScanError() }
                while try scanner.nextElement() {
                    guard try scanner.beginArrayIfPresent(), try scanner.nextElement(),
                          let at = try scanner.readInteger(), try scanner.nextElement() else { throw JSONScanError() }
                    lines.append(Line(atMs: at, text: try scanner.withStoredText(compact)))
                    guard try !scanner.nextElement() else { throw JSONScanError() }
                }
                files.append(lines)
                return true
            }
            return true
        }
        return files
    }

    /// Calls `member` for each top-level member; it returns false to have the value skipped.
    private static func scanDocument(_ file: String, _ member: (JSONKey, inout JSONScanner) throws -> Bool) throws {
        let data = try Data(contentsOf: Fixtures.root.appendingPathComponent(file))
        try data.withUnsafeBytes { buffer in
            var scanner = JSONScanner(buffer)
            try scanner.beginObject()
            while let key = try scanner.nextMember() {
                if try !member(key, &scanner) { try scanner.skipValue() }
            }
            try scanner.finish()
        }
    }

    /// Calls `member` for each member of each object of the array value; it returns false to have the value skipped.
    private static func forEachObject(_ scanner: inout JSONScanner, _ member: (JSONKey, inout JSONScanner) throws -> Bool) throws {
        guard try scanner.beginArrayIfPresent() else { throw JSONScanError() }
        while try scanner.nextElement() {
            guard try scanner.beginObjectIfPresent() else { continue }
            while let key = try scanner.nextMember() {
                if try !member(key, &scanner) { try scanner.skipValue() }
            }
        }
    }

    /// JSON text without the whitespace outside its strings.
    static func compact(_ text: UnsafeRawBufferPointer) -> [UInt8] {
        var result: [UInt8] = []
        var inString = false
        var escaped = false
        for byte in text {
            if inString {
                result.append(byte)
                if escaped {
                    escaped = false
                } else if byte == UInt8(ascii: "\\") {
                    escaped = true
                } else if byte == UInt8(ascii: "\"") {
                    inString = false
                }
            } else if byte == UInt8(ascii: "\"") {
                inString = true
                result.append(byte)
            } else if ![0x20, 0x0A, 0x0D, 0x09].contains(byte) {
                result.append(byte)
            }
        }
        return result
    }
}
