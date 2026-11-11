import Foundation

/// The side table of the quota estimate (QUOTAS 10.3): each analysed file's spend per minute within 8 days and its
/// Codex token count lines, written with the file's contributions. Created on every open like `perf_records`; a row
/// of another version is analysed again while its file is recent. Numbers, model ids and plan names only.
extension HistoryDatabase {
    static let estimateTableSQL = """
        CREATE TABLE IF NOT EXISTS estimate_files (
            file_id INTEGER PRIMARY KEY, version INTEGER NOT NULL, data BLOB NOT NULL);
        """

    func deleteEstimate(fileId: Int64) throws {
        let statement = try connection.prepare("DELETE FROM estimate_files WHERE file_id = ?1")
        statement.bind(1, fileId)
        try statement.run()
    }

    func saveEstimate(_ file: QuotaEstimateFile, fileId: Int64) throws {
        let statement = try connection.prepare("INSERT OR REPLACE INTO estimate_files (file_id, version, data) VALUES (?1, ?2, ?3)")
        statement.bind(1, fileId)
        statement.bind(2, QuotaEstimateFile.version)
        let data = file.isEmpty ? Data() : file.encoded()
        data.withUnsafeBytes { statement.bind(3, blob: $0) }
        try statement.run()
    }

    /// Every stored file's share by path, with its version; an unreadable row counts as missing.
    func estimateFiles() throws -> [String: (version: Int64, file: QuotaEstimateFile)] {
        let statement = try connection.prepare("""
            SELECT f.path, e.version, e.data FROM estimate_files e JOIN files f ON f.id = e.file_id
            """)
        var out: [String: (version: Int64, file: QuotaEstimateFile)] = [:]
        try statement.forEachRow { row in
            guard let path = row.string(0) else { return }
            let data = row.withBlob(2) { Data($0) }
            if data.isEmpty {
                out[path] = (row.int64(1), QuotaEstimateFile())
            } else if let file = QuotaEstimateFile.decode(data, file: path) {
                out[path] = (row.int64(1), file)
            }
        }
        return out
    }
}
