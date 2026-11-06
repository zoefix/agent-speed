import Foundation

/// The side tables of the remote hosts' daily history (REMOTE 8.3). Created on every open like `perf_records`
/// (`user_version` does not change and older builds ignore them). `data` is the aggregate's or the record's exact JSON
/// text as the relay sent it, so the digest stays verifiable. A large batch waits in the connection's temporary
/// staging tables until it commits, so nothing of it reaches the store before its `historyDone`.
extension HistoryDatabase {
    static let remoteTablesSQL = """
        CREATE TABLE IF NOT EXISTS remote_hosts (id TEXT PRIMARY KEY, name TEXT, zone TEXT);
        CREATE TABLE IF NOT EXISTS remote_days (host TEXT, day TEXT, source TEXT, data TEXT, digest TEXT,
            PRIMARY KEY (host, day, source));
        CREATE TABLE IF NOT EXISTS remote_perf (host TEXT, day TEXT, source TEXT, model TEXT, data TEXT,
            PRIMARY KEY (host, day, source, model));
        CREATE TEMP TABLE IF NOT EXISTS remote_stage_days (host TEXT, day TEXT, source TEXT, data TEXT, digest TEXT,
            PRIMARY KEY (host, day, source));
        CREATE TEMP TABLE IF NOT EXISTS remote_stage_perf (host TEXT, day TEXT, source TEXT, model TEXT, data TEXT,
            PRIMARY KEY (host, day, source, model));
        """

    /// A committed batch would leave the host with more rows than REMOTE 8.3 allows; nothing was changed.
    struct RemoteHistoryTooLarge: Error {}

    /// The side tables; `remote_hosts.zone` is added to a table created before it existed.
    func createRemoteTables() throws {
        try connection.execute(Self.remoteTablesSQL)
        var columns: Set<String> = []
        try connection.prepare("PRAGMA table_info(remote_hosts)").forEachRow { row in
            if let name = row.string(1) { columns.insert(name) }
        }
        if !columns.contains("zone") { try connection.execute("ALTER TABLE remote_hosts ADD COLUMN zone TEXT") }
    }

    struct RemoteDayRow: Equatable {
        var host: String
        var day: String
        var source: String
        var data: String
        var digest: String
    }

    struct RemotePerfRow: Equatable {
        var host: String
        var day: String
        var source: String
        var model: String
        var data: String
    }

    /// `remote_hosts`: id → name (nil when it was stored without one).
    func remoteHostNames() throws -> [String: String?] {
        var names: [String: String?] = [:]
        try connection.prepare("SELECT id, name FROM remote_hosts").forEachRow { row in
            if let id = row.string(0) { names[id] = row.string(1) }
        }
        return names
    }

    /// `remote_hosts`: id → the zone its rows were computed in, for every host stored with one.
    func remoteHostZones() throws -> [String: String] {
        var zones: [String: String] = [:]
        try connection.prepare("SELECT id, zone FROM remote_hosts WHERE zone IS NOT NULL").forEachRow { row in
            if let id = row.string(0), let zone = row.string(1) { zones[id] = zone }
        }
        return zones
    }

    func remoteDayRows() throws -> [RemoteDayRow] {
        var rows: [RemoteDayRow] = []
        try connection.prepare("SELECT host, day, source, data, digest FROM remote_days").forEachRow { row in
            guard let host = row.string(0), let day = row.string(1), let source = row.string(2), let data = row.string(3),
                  let digest = row.string(4) else { return }
            rows.append(RemoteDayRow(host: host, day: day, source: source, data: data, digest: digest))
        }
        return rows
    }

    func remotePerfRows() throws -> [RemotePerfRow] {
        var rows: [RemotePerfRow] = []
        try connection.prepare("SELECT host, day, source, model, data FROM remote_perf").forEachRow { row in
            guard let host = row.string(0), let day = row.string(1), let source = row.string(2), let model = row.string(3),
                  let data = row.string(4) else { return }
            rows.append(RemotePerfRow(host: host, day: day, source: source, model: model, data: data))
        }
        return rows
    }

    /// Moves complete days of a large batch to the staging tables; `first` clears what a dropped batch left there.
    func stageRemote(_ days: [RemoteHistoryBatch.Day], host: String, first: Bool) throws {
        try connection.transaction {
            if first { try clearRemoteStaging(host: host) }
            try write(days, host: host, days: "temp.remote_stage_days", perf: "temp.remote_stage_perf")
        }
    }

    func clearRemoteStaging(host: String) throws {
        for table in ["temp.remote_stage_perf", "temp.remote_stage_days"] {
            let statement = try connection.prepare("DELETE FROM \(table) WHERE host = ?1")
            statement.bind(1, host)
            try statement.run()
        }
    }

    /// Commits one batch of a host in a single transaction (REMOTE 8.3): its staged days (when `staged`), then `days`,
    /// each replacing its row and its performance records; with `replace`, the host's other rows go first. The host
    /// keeps `name` (or its stored name when nil) and `zone`. A host left with more rows than `limits` throws
    /// `RemoteHistoryTooLarge` and nothing changes. The staging rows go either way.
    func commitRemote(_ days: [RemoteHistoryBatch.Day], host: String, name: String?, zone: String, replace: Bool,
                      staged: Bool, limits: RemoteHistoryLimits) throws {
        defer { try? clearRemoteStaging(host: host) }
        try connection.transaction {
            if replace {
                for table in ["remote_perf", "remote_days"] {
                    let statement = try connection.prepare("DELETE FROM \(table) WHERE host = ?1")
                    statement.bind(1, host)
                    try statement.run()
                }
            }
            if staged {
                for sql in [
                    """
                    DELETE FROM remote_perf WHERE host = ?1
                        AND (day, source) IN (SELECT day, source FROM temp.remote_stage_days WHERE host = ?1)
                    """,
                    "INSERT OR REPLACE INTO remote_days SELECT * FROM temp.remote_stage_days WHERE host = ?1",
                    "INSERT OR REPLACE INTO remote_perf SELECT * FROM temp.remote_stage_perf WHERE host = ?1",
                ] {
                    let statement = try connection.prepare(sql)
                    statement.bind(1, host)
                    try statement.run()
                }
            }
            try write(days, host: host, days: "remote_days", perf: "remote_perf")
            let dayRows = try count("remote_days", host: host)
            let perfRows = try count("remote_perf", host: host)
            guard dayRows <= limits.days, perfRows <= limits.perf else { throw RemoteHistoryTooLarge() }
            if dayRows == 0, perfRows == 0 {
                let statement = try connection.prepare("DELETE FROM remote_hosts WHERE id = ?1")
                statement.bind(1, host)
                try statement.run()
                return
            }
            let upsertHost = try connection.prepare("""
                INSERT INTO remote_hosts (id, name, zone) VALUES (?1, ?2, ?3)
                ON CONFLICT(id) DO UPDATE SET name = coalesce(excluded.name, remote_hosts.name), zone = excluded.zone
                """)
            upsertHost.bind(1, host)
            upsertHost.bind(2, name)
            upsertHost.bind(3, zone)
            try upsertHost.run()
        }
    }

    /// Each day replaces its row and its performance records in the given tables.
    private func write(_ batch: [RemoteHistoryBatch.Day], host: String, days: String, perf: String) throws {
        guard !batch.isEmpty else { return }
        let upsertDay = try connection.prepare("""
            INSERT OR REPLACE INTO \(days) (host, day, source, data, digest) VALUES (?1, ?2, ?3, ?4, ?5)
            """)
        let clearPerf = try connection.prepare("DELETE FROM \(perf) WHERE host = ?1 AND day = ?2 AND source = ?3")
        let insertPerf = try connection.prepare("""
            INSERT OR REPLACE INTO \(perf) (host, day, source, model, data) VALUES (?1, ?2, ?3, ?4, ?5)
            """)
        for day in batch {
            upsertDay.bind(1, host)
            upsertDay.bind(2, day.day)
            upsertDay.bind(3, day.source.rawValue)
            upsertDay.bind(4, day.aggregateJSON)
            upsertDay.bind(5, day.digest)
            try upsertDay.run()
            clearPerf.bind(1, host)
            clearPerf.bind(2, day.day)
            clearPerf.bind(3, day.source.rawValue)
            try clearPerf.run()
            for record in day.perf {
                insertPerf.bind(1, host)
                insertPerf.bind(2, day.day)
                insertPerf.bind(3, day.source.rawValue)
                insertPerf.bind(4, record.model)
                insertPerf.bind(5, record.json)
                try insertPerf.run()
            }
        }
    }

    private func count(_ table: String, host: String) throws -> Int {
        let statement = try connection.prepare("SELECT count(*) FROM \(table) WHERE host = ?1")
        statement.bind(1, host)
        var count: Int64 = 0
        try statement.forEachRow { count = $0.int64(0) }
        return Int(count)
    }

    /// Sets a stored host's name; a host without rows is left absent.
    func renameRemoteHost(_ id: String, to name: String) throws {
        let statement = try connection.prepare("UPDATE remote_hosts SET name = ?2 WHERE id = ?1")
        statement.bind(1, id)
        statement.bind(2, name)
        try statement.run()
    }

    /// Deletes every row of a host (REMOTE 2.4).
    func deleteRemoteHost(_ id: String) throws {
        try clearRemoteStaging(host: id)
        try connection.transaction {
            for table in ["remote_perf", "remote_days"] {
                let statement = try connection.prepare("DELETE FROM \(table) WHERE host = ?1")
                statement.bind(1, id)
                try statement.run()
            }
            let statement = try connection.prepare("DELETE FROM remote_hosts WHERE id = ?1")
            statement.bind(1, id)
            try statement.run()
        }
    }
}
