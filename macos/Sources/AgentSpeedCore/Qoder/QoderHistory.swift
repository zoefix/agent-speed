import Foundation

/// Plans Qoder IDE's part of the daily history (METRICS 4.18 *History*, 8): reads the chat sessions whose identity
/// (row count, highest `rowid`) changed since the store's, and keeps each database's row statistics in memory so the
/// next analysis of this launch reads less.
///
/// Not thread-safe: owned by `HistoryAnalyzer`.
final class QoderHistoryPlanner {
    private struct DatabaseCache {
        var identity: [Int64]
        var stats: [String: (count: Int64, maxRowId: Int64)]
    }

    private var databases: [String: DatabaseCache] = [:]
    private(set) var discovered = 0

    /// The entries whose identity differs from the stored one (`isUnchanged` says), the databases in list order; a
    /// session an earlier database has is left out.
    func plan(roots: [QoderRoot], isUnchanged: (String, Int64, Int64) -> Bool) -> [DevinHistoryEntry] {
        discovered = 0
        var planned: [DevinHistoryEntry] = []
        var claimed: Set<String> = []
        for (position, path) in QoderPaths.databases(roots).enumerated() {
            guard let database = try? QoderDatabase(path: path) else { continue }
            discovered += 1
            let fileID = QoderSource.firstFileID &+ FileID(position)
            guard let entries = try? database.snapshot({ () throws -> [DevinHistoryEntry] in
                let sessions = try database.sessions().map(\.id).filter { claimed.insert($0).inserted }
                let stats = try stats(of: database)
                var changed: [String: (count: Int64, maxRowId: Int64)] = [:]
                for session in sessions {
                    guard let stat = stats[session], !isUnchanged(database.agentId(session), stat.count, stat.maxRowId)
                    else { continue }
                    changed[session] = stat
                }
                guard !changed.isEmpty else { return [] }
                let rows = Dictionary(grouping: try database.rows(ofSessions: Set(changed.keys)), by: { $0.sessionId ?? "" })
                return changed.keys.sorted().map { session in
                    let agentId = database.agentId(session)
                    let records = QoderCalls.records(of: rows[session] ?? [], session: session, agentId: agentId)
                    return DevinHistoryEntry(key: agentId, records: DevinHistoryPlanner.latest(records),
                                             size: changed[session]!.count, modified: changed[session]!.maxRowId,
                                             fileID: fileID)
                }
            }) else { continue }
            planned += entries
        }
        return planned
    }

    /// Row counts and highest rowids, read again only when the database or its `-wal` changed.
    private func stats(of database: QoderDatabase) throws -> [String: (count: Int64, maxRowId: Int64)] {
        let main = FileStat.lstat(database.path), wal = FileStat.lstat(database.path + "-wal")
        let device = main.map { Int64(bitPattern: $0.identity.device) }
        let inode = main.map { Int64(bitPattern: $0.identity.inode) }
        let identity = [device, inode, main?.size, main?.modifiedNs, wal?.size, wal?.modifiedNs].map { $0 ?? -1 }
        if let cached = databases[database.path], cached.identity == identity { return cached.stats }
        let stats = try database.rowStats()
        databases[database.path] = DatabaseCache(identity: identity, stats: stats)
        return stats
    }
}
