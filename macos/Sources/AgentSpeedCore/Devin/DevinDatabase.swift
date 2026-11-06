import Foundation
import SQLite3

/// Devin CLI's `sessions.db`, opened read-only (METRICS 4.17): never written, never given a `-wal` or `-shm`.
final class DevinDatabase {
    struct Session: Equatable, Sendable {
        var id: String
        var workingDirectory: String?
        /// `sessions.model` by the session-model rule (never `adaptive`).
        var model: String?
    }

    struct Row: Sendable {
        var rowId: Int64
        var sessionId: String
        /// `created_at` in Unix milliseconds; nil when it is not a number.
        var timeMs: Int64?
        /// Nil for a malformed `chat_message`.
        var message: DevinMessage?
    }

    static let maxMessageBytes = 32 << 20

    let path: String
    private let connection: SQLiteConnection

    init(path: String) throws {
        guard DevinPaths.isRegularFile(path) else { throw OpencodeDatabase.OpenError.missing }
        self.path = path
        let mode: SQLiteConnection.Mode = FileStat.stat(path + "-wal") == nil && OpencodeDatabase.headerSaysWAL(path)
            ? .immutable : .readOnly
        connection = try SQLiteConnection(path: path, mode: mode)
        _ = try connection.prepare("SELECT id, working_directory, model FROM sessions LIMIT 0")
        _ = try connection.prepare("SELECT row_id, session_id, chat_message, created_at FROM message_nodes LIMIT 0")
    }

    /// Runs `body` in one read transaction, so the sessions and the rows come from one snapshot.
    func snapshot<T>(_ body: () throws -> T) throws -> T {
        try connection.execute("BEGIN")
        defer { try? connection.execute("COMMIT") }
        return try body()
    }

    func sessions() throws -> [Session] {
        var sessions: [Session] = []
        try connection.prepare("SELECT id, working_directory, model FROM sessions ORDER BY rowid").forEachRow { row in
            guard row.type(0) == SQLITE_TEXT, let id = row.string(0) else { return }
            sessions.append(Session(id: id, workingDirectory: row.type(1) == SQLITE_TEXT ? row.string(1) : nil,
                                    model: DevinCalls.sessionModel(row.type(2) == SQLITE_TEXT ? row.string(2) : nil)))
        }
        return sessions
    }

    func maxRowId() throws -> Int64 {
        let query = try connection.prepare("SELECT max(row_id) FROM message_nodes")
        defer { query.reset() }
        return try query.step() ? query.optionalInt64(0) ?? 0 : 0
    }

    private static let columns = """
        SELECT row_id, session_id, created_at,
               CASE WHEN typeof(chat_message) = 'text' AND length(CAST(chat_message AS BLOB)) <= \(maxMessageBytes)
                    THEN chat_message END
        FROM message_nodes
        """

    /// Rows above `rowId`, in `row_id` order.
    func rows(after rowId: Int64) throws -> [Row] {
        let query = try connection.prepare(Self.columns + " WHERE row_id > ?1 ORDER BY row_id")
        query.bind(1, rowId)
        var rows: [Row] = []
        try query.forEachRow { rows.append(Self.row($0)) }
        return rows
    }

    /// The startup read (METRICS 4.17 *Reading*): from the highest `row_id` down while rows are not older than
    /// `thresholdMs` (rows without a time are passed over), returned in `row_id` order.
    func recentRows(since thresholdMs: Int64) throws -> [Row] {
        let query = try connection.prepare(Self.columns + " ORDER BY row_id DESC")
        defer { query.reset() }
        var rows: [Row] = []
        while try query.step() {
            let row = Self.row(query)
            if let time = row.timeMs, time < thresholdMs { break }
            rows.append(row)
        }
        return rows.reversed()
    }

    /// Every row of the given sessions, in `row_id` order (history).
    func rows(ofSessions sessions: Set<String>) throws -> [Row] {
        var rows: [Row] = []
        if sessions.count > 8 {
            try connection.prepare(Self.columns + " ORDER BY row_id").forEachRow { query in
                guard let id = query.string(1), sessions.contains(id) else { return }
                rows.append(Self.row(query))
            }
            return rows
        }
        let query = try connection.prepare(Self.columns + " WHERE session_id = ?1 ORDER BY row_id")
        for session in sessions {
            query.bind(1, session)
            try query.forEachRow { rows.append(Self.row($0)) }
        }
        return rows.sorted { $0.rowId < $1.rowId }
    }

    /// Each session's row count and highest `row_id` (history identities); reads no message.
    func rowStats() throws -> [String: (count: Int64, maxRowId: Int64)] {
        var stats: [String: (count: Int64, maxRowId: Int64)] = [:]
        try connection.prepare("SELECT session_id, count(*), max(row_id) FROM message_nodes GROUP BY session_id")
            .forEachRow { row in
                guard row.type(0) == SQLITE_TEXT, let id = row.string(0) else { return }
                stats[id] = (row.int64(1), row.int64(2))
            }
        return stats
    }

    private static func row(_ query: SQLiteStatement) -> Row {
        let session = query.type(1) == SQLITE_TEXT ? query.string(1) ?? "" : ""
        let message = query.type(3) == SQLITE_TEXT ? query.withText(3) { DevinMessage.parse($0) } : nil
        return Row(rowId: query.int64(0), sessionId: session, timeMs: time(query, column: 2), message: message)
    }

    /// `created_at` (Unix seconds, an integer or a finite real) in milliseconds, truncated and saturated.
    static func time(_ query: SQLiteStatement, column: Int32) -> Int64? {
        switch query.type(column) {
        case SQLITE_INTEGER:
            let (value, overflow) = query.int64(column).multipliedReportingOverflow(by: 1000)
            return overflow ? (query.int64(column) < 0 ? .min : .max) : value
        case SQLITE_FLOAT:
            let value = query.double(column) * 1000
            guard value.isFinite else { return nil }
            if value >= 0x1p63 { return .max }
            if value < -0x1p63 { return .min }
            return Int64(value.rounded(.towardZero))
        default:
            return nil
        }
    }

    func agentId(_ session: String) -> String { path + "#" + session }
}
