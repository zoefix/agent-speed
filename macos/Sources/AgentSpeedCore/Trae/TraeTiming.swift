import Darwin
import Foundation
import os
import zlib

/// Optional per-request corroboration. Every failed timing gate leaves the database counts intact.
/// Only identities, counters and timing markers survive parsing; log messages and conversation content are never cached.
enum TraeTiming {
    struct Proof {
        var session: String
        var turn: String
        var trace: String?
        var history: String
        var config: String
        var usage: TokenUsage
    }
    struct Interval: Hashable { var start: Int64; var end: Int64 }
    struct Turn: Sendable {
        var session: String
        var turn: String
        var status: String
        var trace: String?
        var model: String?
        var cwd: String?
        var start: Int64
        var end: Int64?
    }
    struct Activity: Equatable, Sendable {
        var session: String
        var turn: String
        var model: String?
        var cwd: String?
        var phase: AgentPhase = .idle
        var inFlightStart: Int64?
        var lastLine: Int64
        var referenceResponses: [ResponseRecord] = []
    }
    struct LogFile: Equatable, Sendable { var path: String; var stat: FileStat }
    private static let compressedLimit = 32 * 1024 * 1024
    private static let decodedLimit = 128 * 1024 * 1024
    private static let generation = "call_server_generate_plan_item"
    private static let http = "byted_aha_ffi::aha_net::http"
    private static let stream = "ai_agent::infrastructure::aha_net::stream"
    private static let plan = "ai_agent::domain::plan::simple_service_v2"
    // String.range(.regularExpression) can run through Swift's Unicode regex engine on newer macOS versions.
    // Scanning every large log message that way can monopolize the monitor for tens of seconds. Compile once
    // with ICU and explicit letter/number/underscore boundaries. ICU's \b also treats connector punctuation as
    // word characters, which would miss some unsafe markers previously detected by Swift's regex engine.
    // Feature flags such as enable_retry_after_content_loop remain excluded.
    private static let retryPattern = try! NSRegularExpression(
        pattern: #"(?<![\p{L}\p{N}_])(?:retry|retried)(?![\p{L}\p{N}_])|(?<![\p{L}\p{N}_])reconnect"#,
        options: [.caseInsensitive])

    static func isRetryEvent(_ message: String) -> Bool {
        retryPattern.firstMatch(in: message, range: NSRange(location: 0, length: (message as NSString).length)) != nil
    }
    private static let cache = OSAllocatedUnfairLock(initialState: [String: Cached]())
    private struct Parsed: Sendable { var stat: FileStat; var decodedBytes: Int; var graph: Graph }
    private struct Cached: Sendable { var files: [LogFile]; var parsed: [String: Parsed] }
    enum Failure: Error { case invalid, truncated, oversized, changed }

    static func files(database: String) -> [LogFile] {
        let db = URL(fileURLWithPath: database)
        let directory = db.deletingLastPathComponent()
        guard ["ai-agent", "ai-chat"].contains(directory.lastPathComponent),
              directory.deletingLastPathComponent().lastPathComponent == "ModularData" else { return [] }
        let logs = directory.deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("logs")
        guard safeDirectory(logs.path), let launches = try? FileManager.default.contentsOfDirectory(atPath: logs.path) else { return [] }
        var found: [LogFile] = []
        // Unrecognized huge directory trees are not scanned recursively.
        guard launches.count <= 4096 else { return [] }
        for launch in launches {
            let modular = logs.appendingPathComponent(launch).appendingPathComponent("Modular")
            guard safeDirectory(modular.path), let names = try? FileManager.default.contentsOfDirectory(atPath: modular.path), names.count <= 4096 else { continue }
            for name in names where name.hasPrefix("ai_agent") && name.hasSuffix(".alaudalog") {
                let path = modular.appendingPathComponent(name).path
                if let stat = FileStat.lstat(path), stat.isRegularFile { found.append(LogFile(path: path, stat: stat)) }
            }
        }
        return Array(found.sorted { a, b in a.stat.modifiedNs == b.stat.modifiedNs ? a.path < b.path : a.stat.modifiedNs > b.stat.modifiedNs }.prefix(64))
    }

    private static func safeDirectory(_ path: String) -> Bool {
        var url = URL(fileURLWithPath: path)
        while url.path != "/" {
            guard FileStat.lstat(url.path)?.isDirectory == true else { return false }
            url.deleteLastPathComponent()
        }
        return true
    }

    static func graphs(database: String) -> [Graph] {
        let selected = files(database: database)
        let previous = cache.withLock { $0[database] }
        if let previous, previous.files == selected { return selected.compactMap { previous.parsed[$0.path]?.graph } }
        var parsed: [String: Parsed] = [:], compressed = 0, decoded = 0
        for file in selected {
            // Files are newest first. An unsupported file cannot erase newer evidence, and the
            // aggregate budget truncates old history while retaining the recent graphs already read.
            guard file.stat.size > 0, file.stat.size <= compressedLimit else { continue }
            guard compressed + Int(file.stat.size) <= compressedLimit else { break }
            compressed += Int(file.stat.size)
            if let hit = previous?.parsed[file.path], hit.stat == file.stat {
                guard decoded + hit.decodedBytes <= decodedLimit else { break }
                decoded += hit.decodedBytes
                parsed[file.path] = hit
                continue
            }
            do {
                let data = try read(file)
                let bytes = try inflateLog(data)
                guard decoded + bytes.count <= decodedLimit else { break }
                decoded += bytes.count
                parsed[file.path] = Parsed(stat: file.stat, decodedBytes: bytes.count, graph: try Graph(bytes))
            } catch { continue }
        }
        // read() verifies each immutable byte snapshot before parsing. Later appends do not invalidate an already
        // completed request; retaining the original signature makes the next poll parse any newly appended events.
        // Replacing this root's dictionary evicts removed/rotated files. At most eight roots and 64 files/root survive.
        let snapshot = Cached(files: selected, parsed: parsed)
        cache.withLock { values in
            if values.count >= 8 { values.removeAll(keepingCapacity: true) }
            values[database] = snapshot
        }
        return selected.compactMap { parsed[$0.path]?.graph }
    }

    static func interval(proof: Proof, end: Int64, graphs: [Graph], final: Bool = true) -> Interval? {
        let intervals = Set(graphs.compactMap { $0.interval(proof: proof, chatEnd: end, final: final) })
        return intervals.count == 1 ? intervals.first : nil
    }

    static func activity(turn: Turn, nowMs: Int64, graphs: [Graph]) -> Activity {
        let last = max(turn.start, turn.end ?? 0)
        let idle = Activity(session: turn.session, turn: turn.turn, model: turn.model, cwd: turn.cwd,
                            lastLine: last <= nowMs ? last : 0)
        guard turn.status == "in_progress", (turn.end ?? 0) == 0, turn.start <= nowMs else { return idle }
        let candidates = graphs.compactMap { $0.activity(turn: turn, nowMs: nowMs) }
        guard let first = candidates.first, candidates.allSatisfy({ $0 == first }) else { return idle }
        return first
    }

    private static func read(_ file: LogFile) throws -> Data {
        guard safeDirectory((file.path as NSString).deletingLastPathComponent) else { throw Failure.invalid }
        let fd = Darwin.open(file.path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK)
        guard fd >= 0 else { throw Failure.invalid }
        defer { Darwin.close(fd) }
        guard FileStat.fstat(fd) == file.stat else { throw Failure.changed }
        var data = Data(count: Int(file.stat.size))
        let read = data.withUnsafeMutableBytes { bytes -> Int in
            var offset = 0
            while offset < bytes.count {
                let n = Darwin.pread(fd, bytes.baseAddress! + offset, bytes.count - offset, off_t(offset))
                if n < 0 && errno == EINTR { continue }
                guard n > 0 else { break }
                offset += n
            }
            return offset
        }
        guard read == data.count, FileStat.fstat(fd) == file.stat, FileStat.lstat(file.path) == file.stat else { throw Failure.changed }
        return data
    }

    static func inflateLog(_ data: Data, limit: Int = decodedLimit) throws -> Data {
        func little(_ offset: Int, _ count: Int) -> UInt64 { (0..<count).reduce(0) { $0 | UInt64(data[offset + $1]) << (8 * $1) } }
        guard data.count >= 24, data.prefix(4) == Data("AalG".utf8), data[8..<12] == Data("Atab".utf8) else { throw Failure.invalid }
        let rawOffset = little(16, 8)
        guard rawOffset == 152, rawOffset <= UInt64(data.count - 20),
              data[24..<152].allSatisfy({ $0 == 0 }) else { throw Failure.invalid }
        let offset = Int(rawOffset), version = Int(little(offset + 16, 4))
        guard data[offset..<offset + 4] == Data("AALG".utf8), version <= 1024,
              offset + 20 + version < data.count, limit > 0 else { throw Failure.invalid }
        var state = z_stream()
        guard inflateInit2_(&state, -MAX_WBITS, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else { throw Failure.invalid }
        defer { inflateEnd(&state) }
        var output = Data()
        try data.withUnsafeBytes { raw in
            state.next_in = UnsafeMutablePointer(mutating: raw.baseAddress!.advanced(by: offset + 20 + version).assumingMemoryBound(to: UInt8.self))
            state.avail_in = uInt(data.count - offset - 20 - version)
            var block = [UInt8](repeating: 0, count: 65536)
            while true {
                let status = block.withUnsafeMutableBytes { buffer -> Int32 in
                    state.next_out = buffer.baseAddress!.assumingMemoryBound(to: UInt8.self)
                    state.avail_out = uInt(buffer.count)
                    return inflate(&state, Z_NO_FLUSH)
                }
                let count = block.count - Int(state.avail_out)
                guard output.count + count <= limit else { throw Failure.oversized }
                output.append(contentsOf: block.prefix(count))
                if status == Z_STREAM_END {
                    guard state.avail_in == 0 else { throw Failure.invalid }
                    break
                }
                guard status == Z_OK || status == Z_BUF_ERROR else { throw Failure.invalid }
                // Active logs may not yet carry a DEFLATE trailer; only a final incomplete CBOR item is ignored.
                if state.avail_in == 0 && count == 0 { break }
                guard status != Z_BUF_ERROR || count > 0 else { throw Failure.invalid }
            }
        }
        return output
    }

    private struct Span: Sendable {
        var id: UInt64
        var parent: UInt64?
        var name: String?
        var start: Int64?
        var end: Int64?
        var tags: [String: String] = [:]
    }
    private struct Event: Sendable {
        enum Kind: Sendable { case other, fetch(String?, String?), done, eof, timing(String?, Bool?) }
        var span: UInt64
        var at: Int64?
        var trace: String?
        var bad: Bool
        var kind: Kind
    }
    struct Graph: Sendable {
        private var spans: [UInt64: Span] = [:]
        private var owned: [UInt64: [Event]] = [:]
        init(_ bytes: Data) throws {
            var decoder = CBOR(bytes)
            var events: [Event] = []
            while !decoder.finished {
                let value: CBOR.Value
                do { value = try decoder.next() } catch Failure.truncated { break }
                guard let map = value.map else { throw Failure.invalid }
                if let id = map["sid"]?.uint {
                    var span = spans[id] ?? Span(id: id)
                    if let parent = map["psid"]?.uint { if let previous = span.parent, previous != parent { throw Failure.invalid }; span.parent = parent }
                    if let name = map["name"]?.string { if let previous = span.name, previous != name { throw Failure.invalid }; span.name = name }
                    if let stamp = map["start"] { guard let time = Self.time(stamp), span.start == nil || span.start == time else { throw Failure.invalid }; span.start = time }
                    if let stamp = map["end"] { guard let time = Self.time(stamp), span.end == nil || span.end == time else { throw Failure.invalid }; span.end = time }
                    for tag in map["tags"]?.array ?? [] {
                        guard let pair = tag.array, pair.count == 2, let name = pair[0].string else { throw Failure.invalid }
                        guard ["history_v2_id", "event_name", "turn_id", "session_id"].contains(name) else { continue }
                        guard let text = pair[1].string, text.utf8.count <= 4096,
                              span.tags[name] == nil || span.tags[name] == text else { throw Failure.invalid }
                        span.tags[name] = text
                    }
                    spans[id] = span
                }
                if let fields = map["fields"]?.map, let id = map["span"]?.uint {
                    let message = fields["message"]?.string ?? ""
                    let target = map["marker"]?.map?["target"]?.string
                    var event = Event(span: id, at: Self.time(map["time"]), trace: fields["trace_id"]?.string,
                                      bad: map["level"]?.string == "ERROR", kind: .other)
                    if target == http, message.hasPrefix("[aha_net] send: calling Fetch id=") {
                        let parts = message.components(separatedBy: ", ")
                        let method = parts.first { $0.hasPrefix("method=") }.map { String($0.dropFirst(7)) }
                        let path = parts.first { $0.hasPrefix("url=") }.flatMap { URL(string: String($0.dropFirst(4)))?.path }
                        event.kind = .fetch(method, path)
                    } else if target == stream, message == "DONE" { event.kind = .done
                    } else if target == stream, message == "EOF" { event.kind = .eof
                    } else if target == plan, message.hasPrefix("TimingCost: TimingCostOriginEvent { data: ") {
                        if let timing = Self.timing(message) { event.kind = .timing(timing.0, timing.1) }
                        else { event.bad = true }
                    } else if TraeTiming.isRetryEvent(message) { event.bad = true }
                    // No arbitrary message text, paths, request headers or body are retained.
                    events.append(event)
                }
            }
            for event in events {
                if let owner = ancestor(event.span, named: generation) { owned[owner.id, default: []].append(event) }
            }
        }

        private func ancestor(_ first: UInt64?, named name: String) -> Span? {
            var id = first, visited: Set<UInt64> = []
            while let current = id, let span = spans[current], visited.count < 64, visited.insert(current).inserted {
                if span.name == name { return span }
                id = span.parent
            }
            return nil
        }
        private func children(_ owner: Span) -> [Span] {
            spans.values.filter { $0.id != owner.id && ancestor($0.parent, named: generation)?.id == owner.id }
        }
        private struct Details {
            var sends: [(Int64?, String?, String?)] = []
            var done: [Int64?] = [], eof: [Int64?] = []
            var timings: [(Int64?, String?, Bool?)] = []
            var traces: [(Int64?, String)] = []
            var unsafeTimes: [Int64?] = []
            func isUnsafe(from start: Int64, through end: Int64) -> Bool {
                unsafeTimes.contains { $0.map { start <= $0 && $0 <= end } ?? true }
            }
            func matchesTrace(_ expected: String?, from start: Int64, through end: Int64) -> Bool {
                for (timestamp, trace) in traces {
                    guard let timestamp else { return false }
                    if start <= timestamp, timestamp <= end, let expected, trace != expected { return false }
                }
                return true
            }
        }
        private func details(_ id: UInt64) -> Details {
            var result = Details()
            for event in owned[id] ?? [] {
                if event.bad { result.unsafeTimes.append(event.at) }
                if let trace = event.trace, !trace.isEmpty { result.traces.append((event.at, trace)) }
                switch event.kind {
                case let .fetch(method, path): result.sends.append((event.at, method, path))
                case .done: result.done.append(event.at)
                case .eof: result.eof.append(event.at)
                case let .timing(model, retry): result.timings.append((event.at, model, retry))
                case .other: break
                }
            }
            return result
        }
        private func root(session: String, turn: String) -> Span? {
            let creates = spans.values.filter { $0.name == "create_chat_turn_with_id" && $0.tags["session_id"] == session && $0.tags["turn_id"] == turn }
            guard creates.count == 1 else { return nil }
            return ancestor(creates[0].parent, named: "do_chat")
        }

        func interval(proof: Proof, chatEnd: Int64, final: Bool = true) -> Interval? {
            let origins = spans.values.filter { $0.name == "add_origin_history" && $0.tags["history_v2_id"] == proof.history }
            guard origins.count == 1, let origin = origins.first,
                  let call = ancestor(origin.parent, named: generation), let root = root(session: proof.session, turn: proof.turn),
                  ancestor(call.parent, named: "do_chat")?.id == root.id else { return nil }
            let evidence = details(call.id)
            guard evidence.sends.count == 1, evidence.done.count == 1, evidence.eof.count == 1, evidence.timings.count == 1,
                  evidence.sends[0].1 == "POST", Self.supportedPath(evidence.sends[0].2),
                  evidence.timings[0].1 == proof.config, evidence.timings[0].2 == false else { return nil }
            let markers = children(call)
            let usages = markers.filter { $0.name == "end_slardar_event_with_metrics" && $0.tags["event_name"] == "IcubeAiAgentTaskPlanToolTokenUsage" }
            let updates = markers.filter { $0.name == "update_turn_context" }
            guard let start = evidence.sends[0].0, let done = evidence.done[0], let eof = evidence.eof[0],
                  let first = evidence.timings[0].0, let begin = call.start, let close = call.end,
                  let added = origin.start, !evidence.isUnsafe(from: start, through: done),
                  evidence.matchesTrace(proof.trace, from: start, through: eof),
                  begin <= start, start < done, done <= eof, eof <= close, close <= chatEnd,
                  done - start <= 900_000, eof - done <= 1000, !final || chatEnd - done <= 60_000,
                  start <= first, first <= done, start <= added, added <= close, abs(added - done) <= 1000,
                  updates.contains(where: { marker in
                      guard let begin = marker.start, let end = marker.end else { return false }
                      return start <= begin && begin <= end && end <= min(close, done + 1000)
                  }), usages.contains(where: { marker in
                      guard let begin = marker.start, let end = marker.end else { return false }
                      return start <= begin && begin <= end && end <= min(close, done + 5000)
                  }) else { return nil }
            for other in spans.values where other.id != call.id && other.name == generation && ancestor(other.parent, named: "do_chat")?.id == root.id {
                let detail = details(other.id)
                if detail.sends.isEmpty { continue }
                guard detail.sends.count == 1, let otherStart = detail.sends[0].0, !final || otherStart <= start else { return nil }
                if otherStart >= done { continue }
                guard detail.done.count == 1, let otherEnd = detail.done[0], otherStart < otherEnd,
                      !(otherStart < done && otherEnd > start) else { return nil }
            }
            return Interval(start: start, end: done)
        }

        private static func supportedPath(_ path: String?) -> Bool {
            path == "/api/agent/v3/create_agent_task" || path == "/api/agent/v3/commit_toolcall_result"
        }

        func activity(turn: Turn, nowMs: Int64) -> Activity? {
            guard let root = root(session: turn.session, turn: turn.turn) else { return nil }
            var result = Activity(session: turn.session, turn: turn.turn, model: turn.model, cwd: turn.cwd, lastLine: turn.start)
            let finishes = spans.values.filter { $0.name == "chat_turn_finish" && $0.end != nil && ancestor($0.parent, named: "do_chat")?.id == root.id }
            if !finishes.isEmpty {
                result.lastLine = min(nowMs, finishes.compactMap(\.end).max() ?? turn.start)
                return result
            }
            let calls = spans.values.filter { $0.name == generation && ancestor($0.parent, named: "do_chat")?.id == root.id }
            var requests: [(Span, Details, Int64)] = []
            for call in calls {
                let evidence = details(call.id)
                if evidence.sends.isEmpty { continue }
                guard evidence.sends.count == 1, let at = evidence.sends[0].0,
                      evidence.sends[0].1 == "POST", Self.supportedPath(evidence.sends[0].2),
                      at >= turn.start, at <= nowMs else { return result }
                requests.append((call, evidence, at))
            }
            guard let newest = requests.map({ $0.2 }).max() else { return result }
            let latest = requests.filter { $0.2 == newest }
            guard latest.count == 1, let (call, evidence, start) = latest.first,
                  let begun = call.start, begun <= start,
                  call.end.map({ start <= $0 && $0 <= nowMs }) ?? true,
                  evidence.matchesTrace(turn.trace, from: start, through: evidence.eof.count == 1 ? (evidence.eof[0] ?? nowMs) : nowMs),
                  evidence.timings.count <= 1 else { return result }
            if let timing = evidence.timings.first {
                guard timing.2 == false, let timestamp = timing.0, start <= timestamp, timestamp <= nowMs else { return result }
                if let model = timing.1, !model.isEmpty { result.model = model }
            }
            // Every older request must be known to have stopped before this one began.
            for (previous, detail, at) in requests where previous.id != call.id {
                guard at < start else { return result }
                if detail.done.count == 1, let done = detail.done[0], at < done, done <= start { continue }
                guard let close = previous.end, at <= close, close <= start else { return result }
            }
            let eventTimes = ([start] + evidence.done.compactMap { $0 } + evidence.eof.compactMap { $0 } + evidence.timings.compactMap { $0.0 })
            guard eventTimes.allSatisfy({ $0 <= nowMs }), let last = eventTimes.max(),
                  nowMs - last <= SpeedEngine.staleMs else { return result }
            result.lastLine = last
            if evidence.done.count == 1, evidence.eof.count == 1,
               let done = evidence.done[0], let eof = evidence.eof[0], start < done, done <= eof,
               eof - done <= 1000, !evidence.isUnsafe(from: start, through: done) {
                result.phase = .tools
            } else if evidence.done.isEmpty, evidence.eof.isEmpty, call.end == nil,
                      !evidence.isUnsafe(from: start, through: nowMs) {
                result.phase = .generating
                result.inFlightStart = start
            }
            return result
        }

        private static func timing(_ message: String) -> (String?, Bool?)? {
            let prefix = "TimingCost: TimingCostOriginEvent { data: "
            guard message.hasSuffix(" }"), let literal = try? JSONSerialization.jsonObject(with: Data(message.dropFirst(prefix.count).dropLast(2).utf8), options: [.fragmentsAllowed]) as? String,
                  let fields = try? JSONParser.parse(Data(literal.utf8)) else { return nil }
            return (fields["config_name"]?.string, fields["is_retry"]?.bool)
        }
        private static func time(_ value: CBOR.Value?) -> Int64? {
            guard let values = value?.array, values.count == 9 else { return nil }
            let numbers = values.compactMap(\.int)
            guard numbers.count == 9, (1970...9999).contains(numbers[0]), (1...366).contains(numbers[1]),
                  (0...23).contains(numbers[2]), (0...59).contains(numbers[3]), (0...59).contains(numbers[4]),
                  (0..<1_000_000_000).contains(numbers[5]), (-23...23).contains(numbers[6]),
                  (-59...59).contains(numbers[7]), (-59...59).contains(numbers[8]) else { return nil }
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = TimeZone(secondsFromGMT: 0)!
            guard let year = calendar.date(from: DateComponents(year: numbers[0], month: 1, day: 1)),
                  numbers[1] <= (calendar.range(of: .day, in: .year, for: year)?.count ?? 0) else { return nil }
            let seconds = (numbers[1] - 1) * 86400 + numbers[2] * 3600 + numbers[3] * 60 + numbers[4] - numbers[6] * 3600 - numbers[7] * 60 - numbers[8]
            return Int64(year.timeIntervalSince1970) * 1000 + Int64(seconds) * 1000 + Int64(numbers[5] / 1_000_000)
        }
    }

    /// Bounded definite-length CBOR. Unknown fields are ephemeral and discarded after projecting each map.
    private struct CBOR {
        indirect enum Value {
            case uint(UInt64), negative(UInt64), text(String), array([Value]), map([String: Value]), other
            var uint: UInt64? { if case let .uint(v) = self { v } else { nil } }
            var int: Int? { switch self { case let .uint(v): Int(exactly: v); case let .negative(v): v < UInt64(Int.max) ? -1 - Int(v) : nil; default: nil } }
            var string: String? { if case let .text(v) = self { v } else { nil } }
            var array: [Value]? { if case let .array(v) = self { v } else { nil } }
            var map: [String: Value]? { if case let .map(v) = self { v } else { nil } }
        }
        var data: Data
        var offset = 0
        var nodes = 0
        init(_ data: Data) { self.data = data }
        var finished: Bool { offset == data.count }
        mutating func bytes(_ count: Int) throws -> Data {
            guard count >= 0, count <= data.count - offset else { throw Failure.truncated }
            defer { offset += count }
            return data.subdata(in: offset..<offset + count)
        }
        mutating func next(_ depth: Int = 0) throws -> Value {
            nodes += 1
            guard depth < 64, nodes <= 8_000_000 else { throw Failure.oversized }
            let header = try bytes(1)[0], major = header >> 5, extra = header & 31
            let number: UInt64
            if extra < 24 { number = UInt64(extra) }
            else if (24...27).contains(extra) { number = try bytes(1 << (extra - 24)).reduce(0) { ($0 << 8) | UInt64($1) } }
            else { throw Failure.invalid }
            if major == 0 { return .uint(number) }
            if major == 1 { return .negative(number) }
            if major == 6 { return try next(depth + 1) }
            if major == 7 { return .other }
            guard number <= UInt64(data.count), number <= 1_048_576 else { throw Failure.oversized }
            let count = Int(number)
            if major == 2 || major == 3 {
                let raw = try bytes(count)
                guard let text = String(data: raw, encoding: .utf8) else {
                    if major == 2 { return .other }; throw Failure.invalid
                }
                return .text(text)
            }
            if major == 4 { var values: [Value] = []; for _ in 0..<count { values.append(try next(depth + 1)) }; return .array(values) }
            if major == 5 {
                var values: [String: Value] = [:]
                for _ in 0..<count {
                    guard let key = try next(depth + 1).string, values[key] == nil else { throw Failure.invalid }
                    values[key] = try next(depth + 1)
                }
                return .map(values)
            }
            throw Failure.invalid
        }
    }
}
