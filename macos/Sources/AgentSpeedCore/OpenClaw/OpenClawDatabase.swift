import Foundation
import SQLite3

/// One row of `session_windows` (METRICS 4.14): a transcript window and the runtime that ran it.
struct OpenClawWindow: Equatable, Sendable {
    static let embeddedRuntimes: Set<String> = ["", "openclaw", "pi", "auto", "default"]

    var sessionId: String
    var createdMs: Int64
    /// `transcript_updated_at`; nil when the column is missing or NULL.
    var transcriptUpdatedMs: Int64?
    /// `spawned_by`, the session key of the spawning parent; nil unless a non-empty string.
    var spawnedBy: String?
    /// `agent_harness_id` trimmed and lower-cased: the runtime of the window's latest run ("" when missing).
    var runtime: String
    /// `acp_owned = 1`.
    var acpOwned: Bool

    var isSubagent: Bool { spawnedBy != nil }

    /// The window's turns run in another program: an ACP session or a runtime other than OpenClaw's own.
    var isDelegated: Bool { acpOwned || !Self.embeddedRuntimes.contains(runtime) }

    /// Every assistant message of the window is a mirror: ACP, the Codex app server or Claude Code.
    var mirrorsTurns: Bool { acpOwned || runtime == "codex" || runtime == "claude-cli" || runtime.hasPrefix("acp") }

    /// The time startup compares with `todayStartMs − 15 min`.
    var activityMs: Int64 { transcriptUpdatedMs ?? createdMs }
}

/// One transcript row, decoded: its entry (a malformed row counts as one malformed line) and its tool calls and
/// results (METRICS 6.10). Rows longer than the line limit are skipped and have no entry.
struct OpenClawRow: Sendable {
    var seq: Int64
    var entry: LineExtraction<OpenClawLine>?
    var tools: [ToolEvent] = []
}

/// How a read takes the rows' tool events: hashed (live lines, for the work signals), without hashing (seeding and
/// the history analysis), or not at all.
enum OpenClawToolReading: Sendable {
    case none
    case unhashed
    case hashed
}

/// Read-only access to one OpenClaw agent database (METRICS 4.14), opened exactly like OpenCode's (4.5): `mode=ro`,
/// busy timeout 2 s, `immutable=1` when the header says WAL and no `-wal` exists; reading never creates a file. Only
/// `session_windows`, `session_nodes`, `transcript_events` and `transcript_rewrite_watermarks` are ever selected, and of
/// `trajectory_runtime_events` the `trace.metadata` rows, chosen in SQL.
final class OpenClawDatabase {
    /// `event_utf8_bytes` above this is not decoded (METRICS 4.14).
    static let maxEventBytes = 4 << 20
    /// A trajectory row larger than OpenClaw's own limit is not read (METRICS 4.14, Trajectory rows).
    static let maxTraceBytes = 262_144

    enum OpenError: Error, Equatable {
        case missing
        case unreadable
        /// No `session_windows` or `transcript_events` (agent schema 1 of 2026.5 – 2026.7).
        case unsupported
    }

    let path: String
    let isImmutable: Bool
    private let connection: SQLiteConnection
    private let windowsStatement: SQLiteStatement
    private let rowsStatement: SQLiteStatement
    private let firstRowsStatement: SQLiteStatement
    private let maxSeqStatement: SQLiteStatement
    private let nodesStatement: SQLiteStatement?
    private let watermarksStatement: SQLiteStatement?
    private let tracesStatement: SQLiteStatement?
    private let maxTrajectorySeqStatement: SQLiteStatement?
    private var decoder: ZstdFrameDecoder?

    init(path: String) throws(OpenError) {
        guard let stat = FileStat.lstat(path), stat.isRegularFile else { throw .missing }
        self.path = path
        var immutable = FileStat.lstat(path + "-wal") == nil && OpencodeDatabase.headerSaysWAL(path)
        var opened: SQLiteConnection
        do {
            opened = try Self.connect(path, mode: immutable ? .immutable : .readOnly)
        } catch where !immutable && FileStat.lstat(path + "-wal") == nil {
            opened = try Self.connect(path, mode: .immutable)
            immutable = true
        }
        isImmutable = immutable
        connection = opened
        do {
            let tables = try Self.tableNames(opened)
            guard tables.contains("session_windows"), tables.contains("transcript_events") else { throw OpenError.unsupported }
            let windowColumns = try Self.columns(of: "session_windows", in: opened)
            let eventColumns = try Self.columns(of: "transcript_events", in: opened)
            func column(_ name: String, else fallback: String) -> String { windowColumns.contains(name) ? name : fallback }
            windowsStatement = try opened.prepare("""
                SELECT session_id, created_at, \(column("transcript_updated_at", else: "NULL")), \
                \(column("spawned_by", else: "NULL")), \(column("agent_harness_id", else: "NULL")), \
                \(column("acp_owned", else: "0")) FROM session_windows ORDER BY created_at, session_id
                """)
            let payload = eventColumns.isSuperset(of: ["event_zstd", "event_utf8_bytes"])
                ? "event_json, event_zstd, event_utf8_bytes" : "event_json, NULL, NULL"
            rowsStatement = try opened.prepare(
                "SELECT seq, \(payload) FROM transcript_events WHERE session_id = ?1 AND seq > ?2 ORDER BY seq")
            firstRowsStatement = try opened.prepare(
                "SELECT seq, \(payload) FROM transcript_events WHERE session_id = ?1 ORDER BY seq LIMIT 2")
            maxSeqStatement = try opened.prepare("SELECT max(seq) FROM transcript_events WHERE session_id = ?1")
            nodesStatement = tables.contains("session_nodes")
                ? try opened.prepare("SELECT session_key, current_session_id FROM session_nodes") : nil
            watermarksStatement = tables.contains("transcript_rewrite_watermarks")
                ? try opened.prepare("SELECT session_id, generation FROM transcript_rewrite_watermarks") : nil
            let trajectory = tables.contains("trajectory_runtime_events")
                ? try Self.columns(of: "trajectory_runtime_events", in: opened) : []
            tracesStatement = Self.traceSQL(columns: trajectory).flatMap { try? opened.prepare($0) }
            maxTrajectorySeqStatement = tracesStatement == nil ? nil
                : try? opened.prepare("SELECT max(seq) FROM trajectory_runtime_events WHERE session_id = ?1")
        } catch let error as OpenError {
            throw error
        } catch let error as SQLiteError {
            throw error.code & 0xFF == SQLITE_ERROR ? .unsupported : .unreadable
        } catch {
            throw .unreadable
        }
    }

    /// Opens and checks that the file can be queried at all (a `mode=ro` open of a closed WAL database fails here).
    private static func connect(_ path: String, mode: SQLiteConnection.Mode) throws(OpenError) -> SQLiteConnection {
        do {
            let connection = try SQLiteConnection(path: path, mode: mode)
            try connection.execute("SELECT 1 FROM sqlite_master LIMIT 1")
            return connection
        } catch {
            throw .unreadable
        }
    }

    /// The one query on `trajectory_runtime_events` (METRICS 4.14): `?1` the window, `?2` the highest `seq` read so far.
    /// A compressed row cannot be matched in SQL, but the row right after a plain `session.started` is a trace row by
    /// construction. A database from 2026.8 has no codec columns and no compressed rows.
    static func traceSQL(columns: Set<String>) -> String? {
        guard columns.isSuperset(of: ["session_id", "seq", "event_json", "created_at"]) else { return nil }
        let plain = "length(e.event_json) <= \(maxTraceBytes) AND instr(e.event_json, '\"type\":\"trace.metadata\"') > 0"
        guard columns.isSuperset(of: ["event_payload", "event_codec", "event_utf8_bytes"]) else {
            return """
                SELECT e.seq, e.created_at, e.event_json, NULL, NULL, NULL FROM trajectory_runtime_events e
                WHERE e.session_id = ?1 AND e.seq > ?2 AND \(plain) ORDER BY e.seq
                """
        }
        return """
            SELECT e.seq, e.created_at, e.event_json, e.event_payload, e.event_codec, e.event_utf8_bytes
            FROM trajectory_runtime_events e
            WHERE e.session_id = ?1 AND e.seq > ?2 AND (
              (e.event_codec IS NULL AND \(plain))
              OR (e.event_codec = 'br' AND e.event_utf8_bytes <= \(maxTraceBytes) AND length(e.event_payload) <= \(maxTraceBytes)
                AND EXISTS (SELECT 1 FROM trajectory_runtime_events p
                            WHERE p.session_id = e.session_id AND p.seq = e.seq - 1 AND p.event_codec IS NULL
                              AND instr(p.event_json, '"type":"session.started"') > 0)))
            ORDER BY e.seq
            """
    }

    private static func tableNames(_ connection: SQLiteConnection) throws -> Set<String> {
        let statement = try connection.prepare("SELECT name FROM sqlite_master WHERE type = 'table'")
        var names: Set<String> = []
        try statement.forEachRow { row in
            if let name = row.string(0) { names.insert(name) }
        }
        return names
    }

    private static func columns(of table: String, in connection: SQLiteConnection) throws -> Set<String> {
        let statement = try connection.prepare("SELECT name FROM pragma_table_info('\(table)')")
        var names: Set<String> = []
        try statement.forEachRow { row in
            if let name = row.string(0) { names.insert(name) }
        }
        return names
    }

    // MARK: - Reading

    /// Runs `body` in one short read transaction, so every query sees the same snapshot.
    func read<T>(_ body: () throws -> T) throws -> T {
        try connection.execute("BEGIN")
        defer { try? connection.execute("COMMIT") }
        return try body()
    }

    /// Every window in `created_at` order, ties by `session_id`.
    func windows() throws -> [OpenClawWindow] {
        var windows: [OpenClawWindow] = []
        try windowsStatement.forEachRow { row in
            guard let id = row.string(0) else { return }
            let spawned = row.string(3)
            let runtime = row.string(4)?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() ?? ""
            windows.append(OpenClawWindow(
                sessionId: id,
                createdMs: row.int64(1),
                transcriptUpdatedMs: row.optionalInt64(2),
                spawnedBy: spawned.flatMap { $0.isEmpty ? nil : $0 },
                runtime: runtime,
                acpOwned: Self.isOne(row, 5)
            ))
        }
        return windows
    }

    /// `session_key` → `current_session_id`.
    func currentSessions() throws -> [String: String] {
        guard let nodesStatement else { return [:] }
        var current: [String: String] = [:]
        try nodesStatement.forEachRow { row in
            if let key = row.string(0), let id = row.string(1) { current[key] = id }
        }
        return current
    }

    /// `session_id` → rewrite `generation`.
    func generations() throws -> [String: String] {
        guard let watermarksStatement else { return [:] }
        var generations: [String: String] = [:]
        try watermarksStatement.forEachRow { row in
            if let id = row.string(0) { generations[id] = row.string(1) ?? "" }
        }
        return generations
    }

    func maxSeq(session id: String) throws -> Int64? {
        maxSeqStatement.bind(1, id)
        var value: Int64?
        try maxSeqStatement.forEachRow { row in value = row.optionalInt64(0) }
        return value
    }

    /// The window's selected trajectory rows above `seq`, in order: each row's `seq` with its trace, nil when the row does
    /// not decode or is no `trace.metadata` event. Nothing else of a row leaves this call.
    func traces(session id: String, after seq: Int64, _ body: (Int64, OpenClawTrace?) -> Void) throws {
        guard let tracesStatement else { return }
        tracesStatement.bind(1, id)
        tracesStatement.bind(2, seq)
        try tracesStatement.forEachRow { row in
            body(row.int64(0), Self.trace(row))
        }
    }

    /// The highest trajectory `seq` of a window, of any row; 0 without any.
    func maxTrajectorySeq(session id: String) throws -> Int64 {
        guard let maxTrajectorySeqStatement else { return 0 }
        maxTrajectorySeqStatement.bind(1, id)
        var value: Int64?
        try maxTrajectorySeqStatement.forEachRow { row in value = row.optionalInt64(0) }
        return value ?? 0
    }

    private static func trace(_ row: SQLiteStatement) -> OpenClawTrace? {
        guard let created = row.optionalInt64(1) else { return nil }
        let modes: ExecutionModes?
        if row.isNull(4) {
            modes = row.type(2) == SQLITE_TEXT ? row.withText(2, OpenClawTraceExtractor.modes) : nil
        } else if row.string(4) == "br", row.type(3) == SQLITE_BLOB, let size = row.optionalInt64(5), (1...Int64(maxTraceBytes)).contains(size) {
            modes = row.withBlob(3) { stream in
                Brotli.decode(stream, size: Int(size))?.withUnsafeBytes(OpenClawTraceExtractor.modes)
            }
        } else {
            modes = nil
        }
        return modes.map { OpenClawTrace(createdMs: created, modes: $0) }
    }

    /// The rows of a window above `seq`, in order.
    func rows(session id: String, after seq: Int64, maxLineBytes: Int, tools: OpenClawToolReading,
              _ body: (OpenClawRow) -> Void) throws {
        rowsStatement.bind(1, id)
        rowsStatement.bind(2, seq)
        try rowsStatement.forEachRow { row in
            body(decode(row, maxLineBytes: maxLineBytes, tools: tools))
        }
    }

    /// The window's header row: the first of its first two rows whose entry has type `session` (METRICS 4.7).
    func header(session id: String, maxLineBytes: Int) throws -> OpenClawRow? {
        firstRowsStatement.bind(1, id)
        var header: OpenClawRow?
        try firstRowsStatement.forEachRow { row in
            guard header == nil else { return }
            let decoded = decode(row, maxLineBytes: maxLineBytes, tools: .none)
            if case .line(.header)? = decoded.entry { header = decoded }
        }
        return header
    }

    /// `event_json` when it is not NULL, else the zstd frame of `event_zstd` decoded within `event_utf8_bytes`.
    private func decode(_ row: SQLiteStatement, maxLineBytes: Int, tools: OpenClawToolReading) -> OpenClawRow {
        let seq = row.int64(0)
        if row.type(1) == SQLITE_TEXT {
            return row.withText(1) { bytes in
                guard bytes.count <= maxLineBytes else { return OpenClawRow(seq: seq, entry: nil) }
                return Self.entry(seq: seq, bytes, tools: tools)
            }
        }
        guard row.type(2) == SQLITE_BLOB, let size = row.optionalInt64(3),
              size > 0, size <= Self.maxEventBytes else {
            return OpenClawRow(seq: seq, entry: .malformed)
        }
        if decoder == nil { decoder = ZstdFrameDecoder() }
        guard let decoder else { return OpenClawRow(seq: seq, entry: .malformed) }
        var decoded: OpenClawRow?
        let ok = row.withBlob(2) { frame in
            decoder.decode(frame, limit: Int(size)) { decoded = Self.entry(seq: seq, $0, tools: tools) }
        }
        return ok ? decoded ?? OpenClawRow(seq: seq, entry: .malformed) : OpenClawRow(seq: seq, entry: .malformed)
    }

    private static func entry(seq: Int64, _ bytes: UnsafeRawBufferPointer, tools: OpenClawToolReading) -> OpenClawRow {
        var extraction = OpenClawLineExtractor.extract(bytes)
        // A row is one whole entry: an empty or blank one is not JSON.
        if case .blank = extraction { extraction = .malformed }
        let events: [ToolEvent] = switch tools {
        case .none: []
        case .unhashed: ToolEventExtractor.openclawEvents(bytes, hashes: false)
        case .hashed: ToolEventExtractor.openclawEvents(bytes)
        }
        return OpenClawRow(seq: seq, entry: extraction, tools: events)
    }

    private static func isOne(_ row: SQLiteStatement, _ column: Int32) -> Bool {
        switch row.type(column) {
        case SQLITE_INTEGER: row.int64(column) == 1
        case SQLITE_FLOAT: row.double(column) == 1
        default: false
        }
    }
}
