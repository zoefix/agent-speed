import Foundation

/// One history entry of Devin (METRICS 4.17 *History*, 8): a CLI session with its calls.
struct DevinHistoryEntry: Sendable {
    /// The entry's store key: `<database path>#<session id>`.
    var key: String
    var records: [ResponseRecord]
    var size: Int64
    var modified: Int64
    var fileID: FileID
}

/// Plans Devin's part of the daily history: reads the sessions whose identity changed since the store's, and keeps each
/// database's row statistics in memory so the next analysis of this launch reads less.
///
/// Not thread-safe: owned by `HistoryAnalyzer`.
final class DevinHistoryPlanner {
    private struct DatabaseCache {
        var identity: [Int64]
        var stats: [String: (count: Int64, maxRowId: Int64)]
    }

    private var databases: [String: DatabaseCache] = [:]
    private(set) var discovered = 0

    /// The entries whose identity differs from the stored one (`isUnchanged` says), the databases in list order; a
    /// session an earlier database has is left out.
    func plan(roots: [DevinRoot], isUnchanged: (String, Int64, Int64) -> Bool) -> [DevinHistoryEntry] {
        discovered = 0
        var planned: [DevinHistoryEntry] = []
        var claimed: Set<String> = []
        for (position, path) in DevinPaths.databases(roots).enumerated() {
            guard let database = try? DevinDatabase(path: path) else { continue }
            discovered += 1
            let fileID = DevinSource.firstFileID &+ FileID(position)
            guard let entries = try? database.snapshot({ () throws -> [DevinHistoryEntry] in
                let sessions = try database.sessions().filter { claimed.insert($0.id).inserted }
                let stats = try stats(of: database)
                var changed: [String: DevinDatabase.Session] = [:]
                var identities: [String: (Int64, Int64)] = [:]
                for session in sessions {
                    guard let stat = stats[session.id] else { continue }
                    let identity = (stat.count, Self.modified(maxRowId: stat.maxRowId, model: session.model))
                    if isUnchanged(database.agentId(session.id), identity.0, identity.1) { continue }
                    changed[session.id] = session
                    identities[session.id] = identity
                }
                guard !changed.isEmpty else { return [] }
                var records: [String: [ResponseRecord]] = [:]
                for row in try database.rows(ofSessions: Set(changed.keys)) {
                    guard let session = changed[row.sessionId], let time = row.timeMs, let message = row.message,
                          let record = DevinCalls.record(of: message, session: row.sessionId, rowId: row.rowId,
                                                         endMs: time, sessionModel: session.model,
                                                         agentId: database.agentId(row.sessionId)) else { continue }
                    records[row.sessionId, default: []].append(record)
                }
                return changed.keys.sorted().map { id in
                    DevinHistoryEntry(key: database.agentId(id), records: Self.latest(records[id] ?? []),
                                      size: identities[id]!.0, modified: identities[id]!.1, fileID: fileID)
                }
            }) else { continue }
            planned += entries
        }
        return planned
    }

    /// Row counts and highest row ids, read again only when the database or its `-wal` changed.
    private func stats(of database: DevinDatabase) throws -> [String: (count: Int64, maxRowId: Int64)] {
        let main = FileStat.lstat(database.path), wal = FileStat.lstat(database.path + "-wal")
        let device = main.map { Int64(bitPattern: $0.identity.device) }
        let inode = main.map { Int64(bitPattern: $0.identity.inode) }
        let identity = [device, inode, main?.size, main?.modifiedNs, wal?.size, wal?.modifiedNs].map { $0 ?? -1 }
        if let cached = databases[database.path], cached.identity == identity { return cached.stats }
        let stats = try database.rowStats()
        databases[database.path] = DatabaseCache(identity: identity, stats: stats)
        return stats
    }

    /// The highest row id and the session model in one number: a call without a model of its own takes the latter.
    static func modified(maxRowId: Int64, model: String?) -> Int64 {
        Int64(bitPattern: KeyHash.fnv1a(String(maxRowId) + "|" + (model ?? "")))
    }

    /// The last record of each key, in first-seen order (a call's stored copies update it, METRICS 4.4).
    static func latest(_ records: [ResponseRecord]) -> [ResponseRecord] {
        var order: [String] = []
        var byKey: [String: ResponseRecord] = [:]
        for record in records {
            if byKey.updateValue(record, forKey: record.key) == nil { order.append(record.key) }
        }
        return order.compactMap { byKey[$0] }
    }
}
