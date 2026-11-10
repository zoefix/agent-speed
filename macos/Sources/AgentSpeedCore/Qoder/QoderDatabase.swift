import Foundation
import SQLite3

/// Qoder IDE's `local.db`, opened read-only (METRICS 4.18): never written, never given a `-wal` or `-shm`.
final class QoderDatabase {
    enum OpenError: Error, Equatable {
        /// The tables or columns of METRICS 4.18 are absent.
        case unsupported
    }

    struct Session: Equatable, Sendable {
        var id: String
        /// The working folder `project_uri` names, if any.
        var projectPath: String?
        /// `parent_session_id` when it is not blank: the session is a sub-agent.
        var parentId: String?
    }

    enum Role: Equatable, Sendable {
        case user
        case tool
        case assistant
        case other
    }

    struct Row: Sendable {
        var rowId: Int64
        /// Nil when `session_id` is not text.
        var sessionId: String?
        /// `id` trimmed, nil when blank or not text.
        var messageId: String?
        /// Nil when `role` is not a string (a malformed row).
        var role: Role?
        /// `gmt_create` in Unix milliseconds; nil when it is not a finite number.
        var timeMs: Int64?
        /// `token_info` when it is a JSON object.
        var tokens: QoderTokens?
        /// `model_info.model_key`.
        var model: String?
        var requestId: String?
    }

    static let maxJSONBytes = 1 << 20

    let path: String
    private let connection: SQLiteConnection
    private let hasRequestIds: Bool
    private let hasParents: Bool
    private let hasRecords: Bool

    init(path: String) throws {
        guard QoderPaths.isRegularFile(path) else { throw OpencodeDatabase.OpenError.missing }
        self.path = path
        let mode: SQLiteConnection.Mode = FileStat.stat(path + "-wal") == nil && OpencodeDatabase.headerSaysWAL(path)
            ? .immutable : .readOnly
        connection = try SQLiteConnection(path: path, mode: mode)
        let message = try Self.columns(of: "chat_message", in: connection)
        let session = try Self.columns(of: "chat_session", in: connection)
        guard ["id", "session_id", "role", "token_info", "model_info", "gmt_create"].allSatisfy(message.contains),
              ["session_id", "project_uri"].allSatisfy(session.contains) else { throw OpenError.unsupported }
        hasRequestIds = message.contains("request_id")
        hasParents = session.contains("parent_session_id")
        let record = try Self.columns(of: "chat_record", in: connection)
        hasRecords = record.contains("request_id") && record.contains("extra")
        _ = try connection.prepare(rowColumns + " LIMIT 0")
    }

    private static func columns(of table: String, in connection: SQLiteConnection) throws -> Set<String> {
        let query = try connection.prepare("SELECT name FROM pragma_table_info(?1)")
        query.bind(1, table)
        var names: Set<String> = []
        try query.forEachRow { row in
            if let name = row.string(0) { names.insert(name) }
        }
        return names
    }

    /// Runs `body` in one read transaction, so sessions, rows and turn records come from one snapshot.
    func snapshot<T>(_ body: () throws -> T) throws -> T {
        try connection.execute("BEGIN")
        defer { try? connection.execute("COMMIT") }
        return try body()
    }

    // MARK: - Sessions

    private var sessionColumns: String {
        "SELECT session_id, project_uri, " + (hasParents ? "parent_session_id" : "NULL") + " FROM chat_session"
    }

    func sessions() throws -> [Session] {
        var sessions: [Session] = []
        try connection.prepare(sessionColumns).forEachRow { row in
            if let session = Self.session(row) { sessions.append(session) }
        }
        return sessions
    }

    /// The sessions of these ids that `chat_session` lists.
    func sessions(ids: some Sequence<String>) throws -> [Session] {
        let query = try connection.prepare(sessionColumns + " WHERE session_id = ?1")
        var sessions: [Session] = []
        for id in ids {
            query.bind(1, id)
            try query.forEachRow { row in
                if let session = Self.session(row), session.id == id { sessions.append(session) }
            }
        }
        return sessions
    }

    private static func session(_ row: SQLiteStatement) -> Session? {
        guard row.type(0) == SQLITE_TEXT, let id = row.string(0) else { return nil }
        let uri = row.type(1) == SQLITE_TEXT ? row.string(1) : nil
        let parent = row.type(2) == SQLITE_TEXT ? row.string(2) : nil
        return Session(id: id, projectPath: QoderPaths.projectPath(ofURI: uri),
                       parentId: DevinMessage.trimmed(parent) == nil ? nil : parent)
    }

    // MARK: - Rows

    private static func bounded(_ column: String) -> String {
        "CASE WHEN typeof(\(column)) = 'text' AND length(CAST(\(column) AS BLOB)) <= \(maxJSONBytes) THEN \(column) END"
    }

    private var rowColumns: String {
        "SELECT rowid, id, session_id, role, gmt_create, \(Self.bounded("token_info")), \(Self.bounded("model_info")), "
            + (hasRequestIds ? "request_id" : "NULL") + " FROM chat_message"
    }

    func maxRowId() throws -> Int64 {
        let query = try connection.prepare("SELECT max(rowid) FROM chat_message")
        defer { query.reset() }
        return try query.step() ? query.optionalInt64(0) ?? 0 : 0
    }

    /// Rows above `rowId`, in `rowid` order.
    func rows(after rowId: Int64) throws -> [Row] {
        let query = try connection.prepare(rowColumns + " WHERE rowid > ?1 ORDER BY rowid")
        query.bind(1, rowId)
        var rows: [Row] = []
        try query.forEachRow { rows.append(Self.row($0)) }
        return rows
    }

    /// The startup read (METRICS 4.18 *Reading*): from the highest `rowid` down while rows are not older than
    /// `thresholdMs` (rows without a time are passed over), returned in `rowid` order.
    func recentRows(since thresholdMs: Int64) throws -> [Row] {
        let query = try connection.prepare(rowColumns + " ORDER BY rowid DESC")
        defer { query.reset() }
        var rows: [Row] = []
        while try query.step() {
            let row = Self.row(query)
            if let time = row.timeMs, time < thresholdMs { break }
            rows.append(row)
        }
        return rows.reversed()
    }

    /// The time of the session's last row below `rowId`: nil when it has none, `.some(nil)` when that row has no time.
    func previousRowTime(session: String, before rowId: Int64) throws -> Int64?? {
        let query = try connection.prepare(
            "SELECT gmt_create FROM chat_message WHERE session_id = ?1 AND rowid < ?2 ORDER BY rowid DESC LIMIT 1")
        defer { query.reset() }
        query.bind(1, session)
        query.bind(2, rowId)
        return try query.step() ? .some(Self.time(query, column: 0)) : nil
    }

    /// Every row of the given sessions, in `rowid` order (history).
    func rows(ofSessions sessions: Set<String>) throws -> [Row] {
        var rows: [Row] = []
        if sessions.count > 8 {
            try connection.prepare(rowColumns + " ORDER BY rowid").forEachRow { query in
                guard let id = query.string(2), sessions.contains(id) else { return }
                rows.append(Self.row(query))
            }
            return rows
        }
        let query = try connection.prepare(rowColumns + " WHERE session_id = ?1 ORDER BY rowid")
        for session in sessions {
            query.bind(1, session)
            try query.forEachRow { rows.append(Self.row($0)) }
        }
        return rows.sorted { $0.rowId < $1.rowId }
    }

    /// Each session's row count and highest `rowid` (history identities); reads no message.
    func rowStats() throws -> [String: (count: Int64, maxRowId: Int64)] {
        var stats: [String: (count: Int64, maxRowId: Int64)] = [:]
        try connection.prepare("SELECT session_id, count(*), max(rowid) FROM chat_message GROUP BY session_id")
            .forEachRow { row in
                guard row.type(0) == SQLITE_TEXT, let id = row.string(0) else { return }
                stats[id] = (row.int64(1), row.int64(2))
            }
        return stats
    }

    private static func row(_ query: SQLiteStatement) -> Row {
        let role: Role? = query.type(3) == SQLITE_TEXT ? query.string(3).map { text in
            switch text {
            case "user": .user
            case "tool": .tool
            case "assistant": .assistant
            default: .other
            }
        } : nil
        return Row(
            rowId: query.int64(0),
            sessionId: query.type(2) == SQLITE_TEXT ? query.string(2) : nil,
            messageId: query.type(1) == SQLITE_TEXT ? DevinMessage.trimmed(query.string(1)) : nil,
            role: role,
            timeMs: time(query, column: 4),
            tokens: query.type(5) == SQLITE_TEXT ? query.withText(5) { QoderJSON.tokens($0) } : nil,
            model: query.type(6) == SQLITE_TEXT ? query.withText(6) { QoderJSON.modelKey($0) } : nil,
            requestId: query.type(7) == SQLITE_TEXT ? query.string(7) : nil
        )
    }

    /// `gmt_create` (Unix ms, an integer or a finite real), truncated and saturated.
    static func time(_ query: SQLiteStatement, column: Int32) -> Int64? {
        switch query.type(column) {
        case SQLITE_INTEGER:
            return query.int64(column)
        case SQLITE_FLOAT:
            let value = query.double(column)
            guard value.isFinite else { return nil }
            if value >= 0x1p63 { return .max }
            if value < -0x1p63 { return .min }
            return Int64(value.rounded(.towardZero))
        default:
            return nil
        }
    }

    // MARK: - Turn records

    /// The latest `finishedAt` of each request's `chat_record` rows (METRICS 4.18 *Agents and phase*); empty when
    /// the database has no turn records.
    func finishedAt(requests: some Sequence<String>) throws -> [String: Int64] {
        guard hasRecords else { return [:] }
        let query = try connection.prepare(
            "SELECT \(Self.bounded("extra")) FROM chat_record WHERE request_id = ?1")
        var finished: [String: Int64] = [:]
        for request in requests {
            query.bind(1, request)
            try query.forEachRow { row in
                guard row.type(0) == SQLITE_TEXT, let at = row.withText(0, { QoderJSON.finishedAt($0) }) else { return }
                finished[request] = max(finished[request] ?? at, at)
            }
        }
        return finished
    }

    func agentId(_ session: String) -> String { path + "#" + session }
}
