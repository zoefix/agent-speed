import Foundation

/// The side table of the performance records (METRICS 6.11 *Store*, 8). Created on every open like
/// `contribution_unpriced` (`user_version` does not change and older builds ignore it); written with each file's
/// contributions; read only where a row's `version` equals its file's. Numbers only, and the model as logged.
extension HistoryDatabase {
    static let perfRecordsTableSQL = """
        CREATE TABLE IF NOT EXISTS perf_records (
            file_id INTEGER NOT NULL, day TEXT NOT NULL, source TEXT NOT NULL, model TEXT NOT NULL,
            n INTEGER NOT NULL, output_sum INTEGER NOT NULL, duration_ms_sum INTEGER NOT NULL,
            latency_bins TEXT NOT NULL, reason_n INTEGER NOT NULL, reason_output INTEGER NOT NULL,
            reason_duration_ms_sum INTEGER NOT NULL, cache_n INTEGER NOT NULL, cache_input INTEGER NOT NULL,
            cache_cached INTEGER NOT NULL, tool_n INTEGER NOT NULL, tool_ln_sum REAL NOT NULL,
            tool_failed INTEGER NOT NULL, active_ms INTEGER NOT NULL, version INTEGER NOT NULL,
            PRIMARY KEY (file_id, day, source, model)) WITHOUT ROWID;
        """

    /// The records of the retired task score (analysis version 8), dropped on every open (METRICS 6.11 *Migration*).
    static let retiredTablesSQL = "DROP TABLE IF EXISTS task_records;"

    func deletePerfRecords(fileId: Int64) throws {
        let statement = try connection.prepare("DELETE FROM perf_records WHERE file_id = ?1")
        statement.bind(1, fileId)
        try statement.run()
    }

    func savePerfRecords(_ records: [PerfDayRecord], fileId: Int64, version: Int64) throws {
        guard !records.isEmpty else { return }
        let statement = try connection.prepare("""
            INSERT OR REPLACE INTO perf_records (file_id, day, source, model, n, output_sum, duration_ms_sum, latency_bins,
                reason_n, reason_output, reason_duration_ms_sum, cache_n, cache_input, cache_cached, tool_n, tool_ln_sum,
                tool_failed, active_ms, version)
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19)
            """)
        for item in records {
            let record = item.record
            statement.bind(1, fileId)
            statement.bind(2, item.day)
            statement.bind(3, item.source.rawValue)
            statement.bind(4, item.model)
            statement.bind(5, Int64(record.n))
            statement.bind(6, record.outputSum)
            statement.bind(7, record.durationMsSum)
            statement.bind(8, record.latencyBinsJSON)
            statement.bind(9, Int64(record.reasonN))
            statement.bind(10, record.reasonOutput)
            statement.bind(11, record.reasonDurationMsSum)
            statement.bind(12, Int64(record.cacheN))
            statement.bind(13, record.cacheInput)
            statement.bind(14, record.cacheCached)
            statement.bind(15, Int64(record.toolN))
            statement.bind(16, record.toolLnSum)
            statement.bind(17, Int64(record.toolFailed))
            statement.bind(18, record.activeMs)
            statement.bind(19, version)
            try statement.run()
        }
    }

    /// The valid records (METRICS 6.11 *Reading*): a day's record of a source and model is the sum over files, in
    /// file order; by day, source order, then model (UTF-8 bytes).
    func perfRecords() throws -> [PerfDayRecord] {
        let statement = try connection.prepare("""
            SELECT p.day, p.source, p.model, p.n, p.output_sum, p.duration_ms_sum, p.latency_bins, p.reason_n,
                   p.reason_output, p.reason_duration_ms_sum, p.cache_n, p.cache_input, p.cache_cached, p.tool_n,
                   p.tool_ln_sum, p.tool_failed, p.active_ms
            FROM perf_records p JOIN files f ON f.id = p.file_id WHERE p.version = f.version
            ORDER BY p.file_id
            """)
        var sums: [PerfRecordKey: PerfRecord] = [:]
        try statement.forEachRow { row in
            guard let day = row.string(0), let source = row.string(1).flatMap(SourceKind.init(rawValue:)),
                  let model = row.string(2) else { return }
            var record = PerfRecord()
            record.n = Int(row.int64(3))
            record.outputSum = row.int64(4)
            record.durationMsSum = row.int64(5)
            record.latencyBins = PerfRecord.bins(fromJSON: row.string(6))
            record.reasonN = Int(row.int64(7))
            record.reasonOutput = row.int64(8)
            record.reasonDurationMsSum = row.int64(9)
            record.cacheN = Int(row.int64(10))
            record.cacheInput = row.int64(11)
            record.cacheCached = row.int64(12)
            record.toolN = Int(row.int64(13))
            record.toolLnSum = row.double(14)
            record.toolFailed = Int(row.int64(15))
            record.activeMs = row.int64(16)
            sums[PerfRecordKey(day: day, source: source, model: model), default: PerfRecord()].add(record)
        }
        return sums.map { PerfDayRecord(key: $0.key, record: $0.value) }.sorted { PerfRecordKey.precedes($0.key, $1.key) }
    }
}
