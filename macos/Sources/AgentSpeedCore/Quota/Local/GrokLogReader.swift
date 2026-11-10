import Foundation

/// Grok Build's billing log (QUOTAS 9.2, *Log*): the last 2 MiB of `unified.jsonl`, read again only when the file
/// changed. No network.
struct GrokLogReader: Sendable {
    static let tailBytes = 2 << 20

    let file: URL
    private var stamp: (size: Int64, modifiedNs: Int64)?
    private(set) var snapshot: QuotaSnapshot?

    init(file: URL) {
        self.file = file
    }

    /// `$GROK_HOME/logs/unified.jsonl`, else `~/.grok/logs/unified.jsonl`.
    static func defaultFile(home: URL, environment: [String: String]) -> URL {
        let root = QuotaCredentials.folder("GROK_HOME", environment) ?? home.appendingPathComponent(".grok")
        return root.appendingPathComponent("logs/unified.jsonl")
    }

    /// True when the snapshot changed.
    @discardableResult
    mutating func refresh() -> Bool {
        guard let stat = FileStat.stat(file.path), stat.isRegularFile else {
            stamp = nil
            return false
        }
        if let stamp, stamp.size == stat.size, stamp.modifiedNs == stat.modifiedNs { return false }
        stamp = (stat.size, stat.modifiedNs)
        guard let lines = Self.tailLines(file, size: stat.size), let found = GrokBilling.fromLog(lines) else { return false }
        let next = QuotaSnapshot(source: .grokBuild, observedMs: found.observedMs, windows: found.windows)
        guard next != snapshot else { return false }
        snapshot = next
        return true
    }

    /// The file's last 2 MiB as lines; a cut first line is dropped.
    static func tailLines(_ url: URL, size: Int64) -> [Data]? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        let start = max(0, size - Int64(tailBytes))
        guard (try? handle.seek(toOffset: UInt64(start))) != nil,
              let data = try? handle.read(upToCount: tailBytes) else { return nil }
        var lines = data.split(separator: UInt8(ascii: "\n"), omittingEmptySubsequences: false).map { Data($0) }
        if start > 0, !lines.isEmpty { lines.removeFirst() }
        return lines.filter { !$0.isEmpty }
    }
}

/// Devin Desktop's cached plan (QUOTAS 9.3): the newest `Devin` or `Windsurf` folder with
/// `User/globalStorage/state.vscdb`, queried read-only, timed by the newest launch folder under `logs`.
struct DevinPlanReader: Sendable {
    let folders: [URL]
    private var stamp: [Int64] = []
    private(set) var snapshot: QuotaSnapshot?

    init(folders: [URL]) {
        self.folders = folders
    }

    static func defaultFolders(home: URL) -> [URL] {
        let support = home.appendingPathComponent("Library/Application Support")
        return [support.appendingPathComponent("Devin"), support.appendingPathComponent("Windsurf")]
    }

    static func database(_ folder: URL) -> URL {
        folder.appendingPathComponent("User/globalStorage/state.vscdb")
    }

    /// True when the snapshot changed.
    @discardableResult
    mutating func refresh(timeZone: TimeZone) -> Bool {
        let found = folders.compactMap { folder -> (URL, FileStat)? in
            FileStat.stat(Self.database(folder).path).flatMap { $0.isRegularFile ? (folder, $0) : nil }
        }
        guard let (folder, stat) = found.max(by: { $0.1.modifiedNs < $1.1.modifiedNs }) else {
            stamp = []
            return false
        }
        let logs = FileStat.stat(folder.appendingPathComponent("logs").path)
        let next = [stat.size, stat.modifiedNs, logs?.modifiedNs ?? 0, Int64(folder.path.hashValue)]
        guard next != stamp else { return false }
        stamp = next
        guard let observed = Self.newestLaunch(folder, timeZone: timeZone) else { return false }
        let rows = Self.rows(Self.database(folder))
        let snapshot = QuotaSnapshot(source: .devin, observedMs: observed, windows: DevinPlan.windows(rows))
        guard snapshot != self.snapshot else { return false }
        self.snapshot = snapshot
        return true
    }

    static func newestLaunch(_ folder: URL, timeZone: TimeZone) -> Int64? {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: folder.appendingPathComponent("logs").path)) ?? []
        return names.compactMap { DevinPlan.launchMs($0, timeZone: timeZone) }.max()
    }

    /// The plan rows: `cachedPlanInfoData` keys, else the older `cachedPlanInfo` ones.
    static func rows(_ database: URL) -> [(key: String, value: Data)] {
        guard let db = try? SQLiteConnection(path: database.path, mode: .immutable) else { return [] }
        defer { db.close() }
        for pattern in DevinPlan.keyPatterns {
            guard let statement = try? db.prepare("SELECT key, value FROM ItemTable WHERE key LIKE ?1") else { return [] }
            statement.bind(1, pattern)
            var rows: [(key: String, value: Data)] = []
            try? statement.forEachRow { row in
                guard rows.count < 64 else { return }
                let value = row.withBlob(1) { Data($0) }
                rows.append((row.string(0) ?? "", value))
            }
            if !rows.isEmpty { return rows }
        }
        return []
    }
}
