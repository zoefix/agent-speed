import Foundation

/// OpenCode's databases, one per root of its location list (METRICS 2.1, 4.5), read in list order. Each owns its own
/// dedupe id; a session another database already provides is skipped.
///
/// Not thread-safe: owned by `TranscriptSession` on its serial queue.
final class OpencodeSourceSet {
    let sources: [OpencodeSource]
    private let claims = SessionClaims()
    private var pendingSeeds: Set<Int> = []

    init(roots: [String]) {
        let claims = claims
        sources = roots.enumerated().map { index, root in
            OpencodeSource(root: root, fileID: TranscriptSession.opencodeFileID(index), claims: claims, owner: index)
        }
    }

    var exists: Bool { sources.contains(where: \.exists) }
    var isReady: Bool { sources.contains(where: \.isReady) }
    var existingCount: Int { sources.filter(\.exists).count }
    var readyCount: Int { sources.filter(\.isReady).count }
    var databaseBytes: Int64 { sources.filter(\.isReady).reduce(0) { $0 + $1.databaseSize } }

    /// The first root whose database exists, else the first root (the row's path, METRICS 2).
    var displayRoot: String { sources.first(where: \.exists)?.rootPath ?? sources.first?.rootPath ?? "" }

    var watchedDirectories: [String] { sources.filter(\.isReady).compactMap(\.watchedDirectory) }
    var hasUnsupported: Bool { sources.contains { $0.status == .unsupported } }
    var unreadableCount: Int { sources.filter { $0.status == .unreadable }.count }
    var malformedRows: Int { sources.reduce(0) { $0 + $1.malformedRows } }

    var parentLinks: [String: String] {
        sources.reduce(into: [:]) { links, source in links.merge(source.parentLinks) { first, _ in first } }
    }

    func project(ofAgent agentId: String) -> String? {
        sources.lazy.compactMap { $0.project(ofAgent: agentId) }.first
    }

    func activeSessionCount(todayStartMs: Int64) -> Int {
        sources.reduce(0) { $0 + $1.activeSessionCount(todayStartMs: todayStartMs) }
    }

    /// Re-checks every database; one that appeared, vanished or was replaced is seeded again. True when any changed.
    @discardableResult
    func refreshAvailability() -> Bool {
        var changed = false
        for (index, source) in sources.enumerated() where source.refreshAvailability() {
            changed = true
            pendingSeeds.insert(index)
        }
        return changed
    }

    /// Startup read of every database that exists, in list order.
    func seed(todayStartMs: Int64, deduper: inout Deduper) -> OpencodeSource.Update {
        pendingSeeds = []
        var update = OpencodeSource.Update()
        for source in sources where source.exists { update.merge(source.seed(todayStartMs: todayStartMs, deduper: &deduper)) }
        return update
    }

    /// Reads the databases a set of changed (canonical) paths names.
    func handleChanges(atCanonicalPaths paths: Set<String>, deduper: inout Deduper) -> OpencodeSource.Update? {
        var update: OpencodeSource.Update?
        for source in sources where paths.contains(where: source.matches(canonicalPath:)) {
            update.merge(source.refresh(deduper: &deduper))
        }
        return update
    }

    func refreshIfChanged(deduper: inout Deduper) -> OpencodeSource.Update? {
        var update: OpencodeSource.Update?
        for source in sources {
            if let next = source.refreshIfChanged(deduper: &deduper) { update.merge(next) }
        }
        return update
    }

    /// Periodic check: databases whose availability changed are read again like at startup (`seeded`, not live), the
    /// others rediscovered (`live`).
    func rediscover(nowMs: Int64, todayStartMs: Int64, deduper: inout Deduper)
        -> (seeded: OpencodeSource.Update?, live: OpencodeSource.Update) {
        var seeded: OpencodeSource.Update?
        var live = OpencodeSource.Update()
        let seeding = pendingSeeds
        pendingSeeds = []
        for (index, source) in sources.enumerated() {
            if seeding.contains(index) {
                seeded.merge(source.seed(todayStartMs: todayStartMs, deduper: &deduper))
            } else {
                live.merge(source.rediscover(nowMs: nowMs, deduper: &deduper))
            }
        }
        return (seeded, live)
    }
}

extension OpencodeSource.Update {
    mutating func merge(_ other: OpencodeSource.Update) {
        upserts += other.upserts
        agents.merge(other.agents) { _, new in new }
        removedAgents += other.removedAgents.filter { agents[$0] == nil }
        reset = reset || other.reset
    }
}

extension Optional where Wrapped == OpencodeSource.Update {
    mutating func merge(_ other: OpencodeSource.Update) {
        if self == nil { self = other } else { self!.merge(other) }
    }
}
