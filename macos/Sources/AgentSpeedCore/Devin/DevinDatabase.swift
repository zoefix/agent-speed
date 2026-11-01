import Foundation
import SQLite3

struct DevinSession: Sendable {
    var id: String
    var workingDirectory: String?
    var model: String?
    var title: String?
    var lastActivityMs: Int64
}

struct DevinMessageRow: Sendable {
    var rowId: Int64
    var sessionId: String
    var createdMs: Int64
    var role: String?
    var metadata: Data?
}

final class DevinDatabase {
    let path: String
    private let connection: SQLiteConnection
    private let hasTitle: Bool

    init(path: String) throws {
        guard FileStat.stat(path)?.isRegularFile == true else { throw OpencodeDatabase.OpenError.missing }
        self.path = path
        let mode: SQLiteConnection.Mode = FileStat.stat(path + "-wal") == nil && OpencodeDatabase.headerSaysWAL(path)
            ? .immutable : .readOnly
        connection = try SQLiteConnection(path: path, mode: mode)
        _ = try connection.prepare("SELECT id,working_directory,model,last_activity_at FROM sessions LIMIT 0")
        _ = try connection.prepare("SELECT row_id,session_id,chat_message,created_at FROM message_nodes LIMIT 0")
        hasTitle = Self.hasColumn("title", table: "sessions", connection: connection)
    }

    func sessions() throws -> [DevinSession] {
        let title = hasTitle ? "title" : "NULL"
        let query = try connection.prepare("SELECT id,working_directory,model,\(title),last_activity_at FROM sessions")
        var result: [DevinSession] = []
        try query.forEachRow { row in
            guard let id = row.string(0) else { return }
            result.append(DevinSession(id: id, workingDirectory: row.string(1), model: row.string(2), title: row.string(3),
                                       lastActivityMs: Self.milliseconds(seconds: row.int64(4))))
        }
        return result
    }

    func messages(after rowId: Int64, sinceMs: Int64) throws -> [DevinMessageRow] {
        let query = try connection.prepare("""
            SELECT row_id,session_id,created_at,
                   CASE WHEN json_valid(chat_message) THEN json_extract(chat_message,'$.role') ELSE 'invalid' END,
                   CASE WHEN json_valid(chat_message) THEN json_extract(chat_message,'$.metadata') END
            FROM message_nodes WHERE row_id>?1 AND created_at>=?2 ORDER BY row_id
            """)
        query.bind(1, rowId)
        query.bind(2, sinceMs <= 0 ? 0 : sinceMs / 1000)
        var result: [DevinMessageRow] = []
        try query.forEachRow { row in
            let metadata = row.withText(4) { $0.isEmpty ? nil : Data($0) }
            result.append(DevinMessageRow(rowId: row.int64(0), sessionId: row.string(1) ?? "",
                                          createdMs: Self.milliseconds(seconds: row.int64(2)), role: row.string(3), metadata: metadata))
        }
        return result
    }

    private static func hasColumn(_ name: String, table: String, connection: SQLiteConnection) -> Bool {
        guard let query = try? connection.prepare("SELECT name FROM pragma_table_info('\(table)')") else { return false }
        var found = false
        try? query.forEachRow { row in
            if row.string(0) == name { found = true }
        }
        return found
    }

    private static func milliseconds(seconds: Int64) -> Int64 {
        let result = seconds.multipliedReportingOverflow(by: 1000)
        return result.overflow ? (seconds < 0 ? .min : .max) : result.partialValue
    }
}
