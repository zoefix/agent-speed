import Foundation

/// ZCode's databases, one per root of its location list (METRICS 2.1, 4.10), read in list order. Each owns its own
/// dedupe id; a session another database already provides is skipped.
///
/// Not thread-safe: owned by `TranscriptSession` on its serial queue.
final class ZcodeSourceSet {
    let sources: [ZcodeSource]
    private let claims = SessionClaims()
    private var pendingSeeds: Set<Int> = []

    init(roots: [String]) {
        let claims = claims
        sources = roots.enumerated().map { index, root in
            ZcodeSource(root: root, fileID: TranscriptSession.zcodeFileID(index), claims: claims, owner: index)
        }
    }

    var exists: Bool { sources.contains(where: \.exists) }
    var isReady: Bool { sources.contains(where: \.isReady) }
    var existingCount: Int { sources.filter(\.exists).count }
    var readyCount: Int { sources.filter(\.isReady).count }
    var databaseBytes: Int64 { sources.filter(\.isReady).reduce(0) { $0 + $1.databaseSize } }

    /// The first root whose database exists, else the first root (the row's path, METRICS 2).
    var displayRoot: String { sources.first(where: \.exists)?.rootPath ?? sources.first?.rootPath ?? "" }

    var watchedDirectories: [String] { sources.compactMap(\.watchedDirectory) }
    var hasUnsupported: Bool { sources.contains { $0.status == .unsupported } }
    var unreadableCount: Int { sources.filter { $0.status == .unreadable }.count }

    var parentLinks: [String: String] {
        sources.reduce(into: [:]) { links, source in links.merge(source.parentLinks) { first, _ in first } }
    }

    func activeSessionCount(todayStartMs: Int64) -> Int {
        sources.reduce(0) { $0 + $1.activeSessionCount(todayStartMs: todayStartMs) }
    }

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
    func seed(todayStartMs: Int64, deduper: inout Deduper) -> ZcodeSource.Update {
        pendingSeeds = []
        var update = ZcodeSource.Update()
        for source in sources where source.exists { update.merge(source.seed(todayStartMs: todayStartMs, deduper: &deduper)) }
        return update
    }

    func handleChanges(atCanonicalPaths paths: Set<String>, deduper: inout Deduper) -> ZcodeSource.Update {
        var update = ZcodeSource.Update()
        for source in sources where paths.contains(where: source.matches(canonicalPath:)) {
            update.merge(source.refresh(deduper: &deduper))
        }
        return update
    }

    func refreshIfChanged(deduper: inout Deduper) -> ZcodeSource.Update? {
        var update: ZcodeSource.Update?
        for source in sources {
            guard let next = source.refreshIfChanged(deduper: &deduper) else { continue }
            if update == nil { update = next } else { update!.merge(next) }
        }
        return update
    }

    /// Periodic check: databases whose availability changed are seeded again, the others read when they changed.
    /// `seeded`: whether any startup read happened (its responses are not live).
    func rediscover(todayStartMs: Int64, deduper: inout Deduper) -> (seeded: ZcodeSource.Update, live: ZcodeSource.Update?) {
        var seeded = ZcodeSource.Update()
        var live: ZcodeSource.Update?
        let seeding = pendingSeeds
        pendingSeeds = []
        for (index, source) in sources.enumerated() {
            if seeding.contains(index) {
                seeded.merge(source.seed(todayStartMs: todayStartMs, deduper: &deduper))
            } else if let next = source.refreshIfChanged(deduper: &deduper) {
                if live == nil { live = next } else { live!.merge(next) }
            }
        }
        return (seeded, live)
    }
}

extension ZcodeSource.Update {
    mutating func merge(_ other: ZcodeSource.Update) {
        upserts += other.upserts
        agents.merge(other.agents) { _, new in new }
        removedAgents += other.removedAgents.filter { agents[$0] == nil }
    }
}
