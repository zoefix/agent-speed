import Darwin
import Foundation

/// Reads the account's limits from the usage response Claude Desktop caches, read-only (QUOTAS 4). It remembers the
/// usage entries it found (candidates) and checks them with one `stat` each; a search lists the folder and reads the
/// first bytes of the newest entries until it meets a usage entry. Not thread-safe: one queue uses it.
public final class DesktopQuotaReader {
    public static let checkIntervalMs: Int64 = 60_000
    public static let searchIntervalMs: Int64 = 30 * 60_000
    /// The newest candidate older than this while Desktop runs: Desktop may have changed its query or organization.
    public static let staleCandidateMs: Int64 = 20 * 60_000
    static let maxEntryAgeMs: Int64 = 7 * 24 * 3_600_000
    static let maxHeaderReads = 4000
    static let maxKeyBytes = 8192

    /// `~/Library/Application Support/Claude/Cache/Cache_Data`.
    public static var defaultFolder: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Claude/Cache/Cache_Data", isDirectory: true)
    }

    private struct Candidate {
        var size: Int64
        var modifiedMs: Int64
        var entry: DesktopUsage.Entry?
    }

    public let folder: String
    private var candidates: [String: Candidate] = [:]
    private var searchedMs: Int64?
    private var searchWanted = true
    private var checkedMs: Int64?
    /// Header reads of the last search, for tests and the probe.
    public private(set) var lastSearchReads = 0

    public init(folder: URL = DesktopQuotaReader.defaultFolder) {
        self.folder = folder.path
    }

    /// Asks for a search at the next poll (Claude Desktop started, QUOTAS 4.4).
    public func requestSearch() {
        searchWanted = true
    }

    /// One poll: a search when it is due, then a check of every candidate when 60 s have passed (or after a search).
    /// Returns the newest snapshot, nil when no usage entry is known.
    public func poll(nowMs: Int64, desktopRunning: Bool) -> QuotaSnapshot? {
        var searched = false
        if searchDue(nowMs: nowMs, desktopRunning: desktopRunning) {
            search(nowMs: nowMs)
            searched = true
        }
        if searched || checkedMs.map({ nowMs - $0 >= Self.checkIntervalMs }) ?? true {
            check()
            checkedMs = nowMs
        }
        return snapshot
    }

    /// The newest usage entry among the candidates.
    public var snapshot: QuotaSnapshot? {
        let entries = candidates.compactMap { name, candidate in
            candidate.entry.map { (entry: $0, mtimeMs: candidate.modifiedMs, tag: name) }
        }
        guard let best = DesktopUsage.newest(entries.sorted { $0.tag < $1.tag }) else { return nil }
        return QuotaSnapshot(source: .claudeDesktop, observedMs: best.entry.observedMs, windows: best.entry.windows)
    }

    public var candidateNames: [String] { candidates.keys.sorted() }

    private func searchDue(nowMs: Int64, desktopRunning: Bool) -> Bool {
        if searchWanted { return true }
        guard desktopRunning, let last = searchedMs, nowMs - last >= Self.searchIntervalMs else { return false }
        let newest = candidates.values.map(\.modifiedMs).max()
        return newest.map { nowMs - $0 > Self.staleCandidateMs } ?? true
    }

    /// Lists the folder and reads the keys of `_0` files, newest first, up to the first usage entry; files older than
    /// 7 days are skipped and at most 4000 headers are read.
    func search(nowMs: Int64) {
        searchWanted = false
        searchedMs = nowMs
        lastSearchReads = 0
        guard let files = CacheFolderListing.files(in: folder, where: Self.isEntryName) else { return }
        let recent = files.filter { nowMs - $0.modifiedMs <= Self.maxEntryAgeMs }.sorted { $0.modifiedMs > $1.modifiedMs }
        for file in recent.prefix(Self.maxHeaderReads) {
            lastSearchReads += 1
            guard let key = readKey(file.name), DesktopUsage.isUsageKey(key) else { continue }
            if candidates[file.name] == nil { candidates[file.name] = Candidate(size: -1, modifiedMs: file.modifiedMs) }
            break
        }
    }

    /// One `stat` per candidate; a changed one is read again, a missing one dropped.
    func check() {
        for name in Array(candidates.keys) {
            let path = folder + "/" + name
            guard let stat = FileStat.lstat(path), stat.isRegularFile else {
                candidates[name] = nil
                continue
            }
            guard let known = candidates[name], known.size != stat.size || known.modifiedMs != stat.modifiedMs else { continue }
            let bytes = stat.size <= Int64(SimpleCacheEntry.fileLimit) ? Self.read(path, limit: SimpleCacheEntry.fileLimit) : nil
            let entry = bytes.flatMap { DesktopUsage.entry($0, mtimeMs: stat.modifiedMs) }
            candidates[name] = Candidate(size: stat.size, modifiedMs: stat.modifiedMs, entry: entry)
        }
    }

    /// `<16 hex digits>_0`.
    static func isEntryName(_ name: String) -> Bool {
        let utf8 = name.utf8
        guard utf8.count == 18, name.hasSuffix("_0") else { return false }
        return utf8.prefix(16).allSatisfy { (UInt8(ascii: "0")...UInt8(ascii: "9")).contains($0)
            || (UInt8(ascii: "a")...UInt8(ascii: "f")).contains($0) }
    }

    /// The key from the first `24 + key length` bytes.
    private func readKey(_ name: String) -> String? {
        let descriptor = open(folder + "/" + name, O_RDONLY | O_CLOEXEC | O_NOFOLLOW)
        guard descriptor >= 0 else { return nil }
        defer { close(descriptor) }
        var header = [UInt8](repeating: 0, count: SimpleCacheEntry.headerSize)
        guard pread(descriptor, &header, header.count, 0) == header.count,
              let length = SimpleCacheEntry.keyLength(ofHeader: header), length > 0, length <= Self.maxKeyBytes else { return nil }
        var key = [UInt8](repeating: 0, count: length)
        guard pread(descriptor, &key, length, off_t(SimpleCacheEntry.headerSize)) == length else { return nil }
        return String(bytes: key, encoding: .utf8)
    }

    static func read(_ path: String, limit: Int) -> [UInt8]? {
        let descriptor = open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW)
        guard descriptor >= 0 else { return nil }
        defer { close(descriptor) }
        var bytes: [UInt8] = []
        var chunk = [UInt8](repeating: 0, count: 64 * 1024)
        while true {
            let count = Darwin.read(descriptor, &chunk, chunk.count)
            if count < 0 { return nil }
            if count == 0 { return bytes }
            bytes.append(contentsOf: chunk[0..<count])
            if bytes.count > limit { return nil }
        }
    }
}
