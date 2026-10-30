import Foundation

/// One short read of another program's database (EXECUTION-MODES, Session-current facts): a regular file, opened
/// read-only with the rules of METRICS 4.5, so no `-wal` or `-shm` is created, and closed right after the read, since a
/// foreign reader that stays can hold back the owner's maintenance.
enum ForeignDatabase {
    /// The first column of `sql` for each id, where it is text; a file, table, column or row that is missing gives
    /// nothing. `sql` takes the id as `?1`.
    static func text(path: String, sql: String, ids: [String]) -> [String: String] {
        guard !ids.isEmpty, let connection = open(path), let statement = try? connection.prepare(sql) else { return [:] }
        defer { connection.close() }
        var values: [String: String] = [:]
        for id in ids {
            statement.bind(1, id)
            try? statement.forEachRow { row in
                if row.type(0) == 3, let value = row.string(0) { values[id] = value }
            }
        }
        return values
    }

    /// `immutable=1` when the header says WAL and no `-wal` exists, else `mode=ro`, falling back to `immutable=1` only
    /// while no `-wal` exists.
    private static func open(_ path: String) -> SQLiteConnection? {
        guard FileStat.lstat(path)?.isRegularFile == true else { return nil }
        let hasWAL = FileStat.lstat(path + "-wal") != nil
        if !hasWAL, OpencodeDatabase.headerSaysWAL(path) { return connect(path, mode: .immutable) }
        if let connection = connect(path, mode: .readOnly) { return connection }
        return hasWAL ? nil : connect(path, mode: .immutable)
    }

    private static func connect(_ path: String, mode: SQLiteConnection.Mode) -> SQLiteConnection? {
        guard let connection = try? SQLiteConnection(path: path, mode: mode),
              (try? connection.execute("SELECT 1 FROM sqlite_master LIMIT 1")) != nil else { return nil }
        return connection
    }
}
