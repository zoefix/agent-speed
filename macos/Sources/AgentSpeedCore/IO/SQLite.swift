import Foundation
import SQLite3

struct SQLiteError: Error, Equatable, CustomStringConvertible {
    var code: Int32
    var message: String

    var description: String { "SQLite error \(code): \(message)" }
}

/// One SQLite connection, used by a single thread or serial queue at a time (`SQLITE_OPEN_NOMUTEX`).
final class SQLiteConnection {
    enum Mode {
        /// `mode=ro`: never writes; the connection is also marked `query_only`.
        case readOnly
        /// `immutable=1`: reads the database file alone (no WAL, no locks) and creates nothing. Only valid while
        /// nothing writes the file.
        case immutable
        case readWriteCreate
    }

    static let defaultBusyTimeoutMs: Int32 = 2000

    private(set) var handle: OpaquePointer?

    init(path: String, mode: Mode, busyTimeoutMs: Int32 = SQLiteConnection.defaultBusyTimeoutMs) throws {
        let flags: Int32 = switch mode {
        case .readOnly: SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX
        case .immutable: SQLITE_OPEN_READONLY | SQLITE_OPEN_URI | SQLITE_OPEN_NOMUTEX
        case .readWriteCreate: SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_NOMUTEX
        }
        let name = mode == .immutable ? Self.immutableURI(path) : path
        var db: OpaquePointer?
        let code = sqlite3_open_v2(name, &db, flags, nil)
        guard code == SQLITE_OK, let db else {
            let message = db.map { String(cString: sqlite3_errmsg($0)) } ?? "cannot open"
            sqlite3_close_v2(db)
            throw SQLiteError(code: code, message: message)
        }
        handle = db
        sqlite3_busy_timeout(db, busyTimeoutMs)
        sqlite3_extended_result_codes(db, 1)
        if mode != .readWriteCreate {
            try execute("PRAGMA query_only = 1")
        }
    }

    deinit { close() }

    /// `file:<percent-encoded path>?immutable=1`.
    static func immutableURI(_ path: String) -> String {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "/-._~")
        let escaped = path.addingPercentEncoding(withAllowedCharacters: allowed) ?? path
        return "file:" + escaped + "?immutable=1"
    }

    func close() {
        guard let handle else { return }
        sqlite3_close_v2(handle)
        self.handle = nil
    }

    func execute(_ sql: String) throws {
        guard let handle else { throw SQLiteError(code: SQLITE_MISUSE, message: "closed") }
        let code = sqlite3_exec(handle, sql, nil, nil, nil)
        guard code == SQLITE_OK else { throw error(code) }
    }

    func prepare(_ sql: String) throws -> SQLiteStatement {
        guard let handle else { throw SQLiteError(code: SQLITE_MISUSE, message: "closed") }
        var statement: OpaquePointer?
        let code = sqlite3_prepare_v2(handle, sql, -1, &statement, nil)
        guard code == SQLITE_OK, let statement else { throw error(code) }
        return SQLiteStatement(statement, connection: self)
    }

    /// Runs `body` inside `BEGIN IMMEDIATE … COMMIT`, rolling back when it throws.
    func transaction<T>(_ body: () throws -> T) throws -> T {
        try execute("BEGIN IMMEDIATE")
        do {
            let value = try body()
            try execute("COMMIT")
            return value
        } catch {
            try? execute("ROLLBACK")
            throw error
        }
    }

    func error(_ code: Int32) -> SQLiteError {
        SQLiteError(code: code, message: handle.map { String(cString: sqlite3_errmsg($0)) } ?? "closed")
    }
}

/// A prepared statement. Every read runs as its own short implicit transaction: the statement is reset as soon as
/// the caller is done with it, so no snapshot outlives a query.
final class SQLiteStatement {
    private let statement: OpaquePointer
    private let connection: SQLiteConnection

    fileprivate init(_ statement: OpaquePointer, connection: SQLiteConnection) {
        self.statement = statement
        self.connection = connection
    }

    deinit { sqlite3_finalize(statement) }

    private static var transient: sqlite3_destructor_type { unsafeBitCast(-1, to: sqlite3_destructor_type.self) }

    func bind(_ index: Int32, _ value: Int64) {
        sqlite3_bind_int64(statement, index, value)
    }

    func bind(_ index: Int32, _ value: Int) {
        sqlite3_bind_int64(statement, index, Int64(value))
    }

    func bind(_ index: Int32, _ value: Int64?) {
        if let value {
            sqlite3_bind_int64(statement, index, value)
        } else {
            sqlite3_bind_null(statement, index)
        }
    }

    func bind(_ index: Int32, _ value: Double) {
        sqlite3_bind_double(statement, index, value)
    }

    func bind(_ index: Int32, _ value: String) {
        sqlite3_bind_text(statement, index, value, -1, Self.transient)
    }

    func bind(_ index: Int32, _ value: String?) {
        if let value { bind(index, value) } else { sqlite3_bind_null(statement, index) }
    }

    func bind(_ index: Int32, _ value: Double?) {
        if let value { bind(index, value) } else { sqlite3_bind_null(statement, index) }
    }

    func bind(_ index: Int32, blob: UnsafeRawBufferPointer) {
        if let base = blob.baseAddress, blob.count > 0 {
            sqlite3_bind_blob(statement, index, base, Int32(blob.count), Self.transient)
        } else {
            sqlite3_bind_zeroblob(statement, index, 0)
        }
    }

    /// Advances to the next row. Returns false when done.
    func step() throws -> Bool {
        let code = sqlite3_step(statement)
        switch code {
        case SQLITE_ROW: return true
        case SQLITE_DONE: return false
        default: throw connection.error(code)
        }
    }

    /// Runs a statement that returns no rows, then resets it.
    func run() throws {
        defer { reset() }
        while try step() {}
    }

    /// Steps through every row, then resets the statement (also when `body` throws).
    func forEachRow(_ body: (SQLiteStatement) throws -> Void) throws {
        defer { reset() }
        while try step() { try body(self) }
    }

    func reset() {
        sqlite3_reset(statement)
        sqlite3_clear_bindings(statement)
    }

    func isNull(_ column: Int32) -> Bool {
        sqlite3_column_type(statement, column) == SQLITE_NULL
    }

    /// `SQLITE_INTEGER`, `SQLITE_FLOAT`, `SQLITE_TEXT`, `SQLITE_BLOB` or `SQLITE_NULL`.
    func type(_ column: Int32) -> Int32 {
        sqlite3_column_type(statement, column)
    }

    func int64(_ column: Int32) -> Int64 {
        sqlite3_column_int64(statement, column)
    }

    /// Nil for NULL and for values that are not integers.
    func optionalInt64(_ column: Int32) -> Int64? {
        sqlite3_column_type(statement, column) == SQLITE_INTEGER ? sqlite3_column_int64(statement, column) : nil
    }

    func double(_ column: Int32) -> Double {
        sqlite3_column_double(statement, column)
    }

    /// Nil for NULL.
    func optionalDouble(_ column: Int32) -> Double? {
        sqlite3_column_type(statement, column) == SQLITE_NULL ? nil : sqlite3_column_double(statement, column)
    }

    func string(_ column: Int32) -> String? {
        guard sqlite3_column_type(statement, column) != SQLITE_NULL,
              let text = sqlite3_column_text(statement, column) else { return nil }
        let count = Int(sqlite3_column_bytes(statement, column))
        return String(decoding: UnsafeBufferPointer(start: text, count: count), as: UTF8.self)
    }

    /// The column's text bytes, borrowed for the duration of `body` (never copied).
    func withText<T>(_ column: Int32, _ body: (UnsafeRawBufferPointer) -> T) -> T {
        guard let text = sqlite3_column_text(statement, column) else { return body(UnsafeRawBufferPointer(start: nil, count: 0)) }
        let count = Int(sqlite3_column_bytes(statement, column))
        return body(UnsafeRawBufferPointer(start: text, count: count))
    }

    func withBlob<T>(_ column: Int32, _ body: (UnsafeRawBufferPointer) -> T) -> T {
        let pointer = sqlite3_column_blob(statement, column)
        let count = Int(sqlite3_column_bytes(statement, column))
        return body(UnsafeRawBufferPointer(start: pointer, count: pointer == nil ? 0 : count))
    }
}

/// Read-only incremental access to one blob (`sqlite3_blob_open`): only the bytes asked for are copied out, so a large
/// value is never loaded whole.
final class SQLiteBlob {
    private var blob: OpaquePointer?
    let count: Int

    init(_ connection: SQLiteConnection, table: String, column: String, rowid: Int64) throws {
        guard let handle = connection.handle else { throw SQLiteError(code: SQLITE_MISUSE, message: "closed") }
        let code = sqlite3_blob_open(handle, "main", table, column, rowid, 0, &blob)
        guard code == SQLITE_OK, let blob else {
            sqlite3_blob_close(blob)
            throw connection.error(code)
        }
        count = Int(sqlite3_blob_bytes(blob))
    }

    deinit { sqlite3_blob_close(blob) }

    /// `size` bytes from `offset`; throws for a range past the blob.
    func read(_ offset: Int, _ size: Int) throws -> [UInt8] {
        guard offset >= 0, size >= 0, offset <= count - size else {
            throw SQLiteError(code: SQLITE_ERROR, message: "read past the blob")
        }
        var bytes = [UInt8](repeating: 0, count: size)
        guard size > 0 else { return bytes }
        let code = bytes.withUnsafeMutableBytes { sqlite3_blob_read(blob, $0.baseAddress, Int32(size), Int32(offset)) }
        guard code == SQLITE_OK else { throw SQLiteError(code: code, message: "blob read failed") }
        return bytes
    }
}
