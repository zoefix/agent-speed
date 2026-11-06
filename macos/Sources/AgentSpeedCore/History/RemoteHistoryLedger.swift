import Foundation
import os

/// The remote hosts' daily history inside the history worker (REMOTE 8.2, 8.3): the sync state of every host and the
/// stored rows, written through the worker's database. Confined to the worker's queue.
final class RemoteHistoryLedger {
    enum Effect: Equatable {
        case none
        /// The stored rows or names changed: publish again.
        case changed
        /// Only `tooLarge` changed.
        case tooLargeChanged
        /// Send `history` with these digests.
        case request(host: String, digests: [String: String])
    }

    private static let logger = Logger(subsystem: "io.github.zoefix.agentspeed", category: "history")

    private var sync: RemoteHistorySyncState
    private(set) var rows = RemoteHistoryRows()
    /// The settings' display names by host id.
    private var names: [String: String] = [:]
    /// The zone each host's last request ran in, with its connection.
    private var requested: [String: (connection: Int, zone: String)] = [:]
    /// The app's time zone id as the relay gets it (REMOTE 5.3 `zone`).
    private let zone: () -> String
    /// Bumped whenever `rows` changes.
    private(set) var version = 0
    /// Hosts whose last batch exceeded the bounds (`historyTooLarge`, REMOTE 8.3) until a sync succeeds.
    private(set) var tooLarge: Set<String> = []
    /// Hosts whose current batch could not be staged: it commits nothing, so the next sync sends its days again.
    private var unstaged: Set<String> = []

    init(limits: RemoteHistoryLimits = RemoteHistoryLimits(), flushBytes: Int = 32 << 20,
         zone: @escaping () -> String = { RemoteZone.current().id }) {
        sync = RemoteHistorySyncState(limits: limits, flushBytes: flushBytes)
        self.zone = zone
    }

    /// Hosts whose relay is connected, in id order.
    var connectedHosts: [String] { sync.connected.keys.sorted() }

    /// Reads the rows of `database` (none when they cannot be read) and gives stored hosts their settings names.
    func load(from database: HistoryDatabase) {
        do {
            rows = try RemoteHistoryRows(database: database)
        } catch {
            Self.logger.error("Remote history rows could not be read")
            rows = RemoteHistoryRows()
        }
        version += 1
        _ = applyNames(database)
    }

    /// *Clear history*: the store was deleted, with every batch in progress.
    func reset() {
        sync.dropBatches()
        requested.removeAll()
        unstaged.removeAll()
        rows = RemoteHistoryRows()
        version += 1
    }

    func receive(_ event: RemoteHostEvent, database: HistoryDatabase) -> Effect {
        switch sync.apply(event) {
        case .none:
            return .none
        case .request(let host):
            // Rows of another zone are not compared: the whole history comes again and replaces them.
            let current = zone()
            requested[host] = (event.connection, current)
            let sameZone = rows.zones[host].map { $0 == current } ?? true
            let digests = sameZone ? rows.digests(host: host) : [:]
            return .request(host: host, digests: digests)
        case .stage(let host, let days, let first):
            if first { unstaged.remove(host) }
            do {
                try database.stageRemote(days, host: host, first: first)
            } catch {
                Self.logger.error("Remote history could not be staged")
                unstaged.insert(host)
            }
            return .none
        case .commit(let host, let connection, let days, let staged):
            guard !(staged && unstaged.remove(host) != nil) else {
                requested[host] = nil
                try? database.clearRemoteStaging(host: host)
                return .none
            }
            return commit(days, host: host, connection: connection, staged: staged, database: database)
        case .overflow(let host):
            unstaged.remove(host)
            try? database.clearRemoteStaging(host: host)
            return .none
        case .tooLarge(let host):
            requested[host] = nil
            return tooLarge.insert(host).inserted ? .tooLargeChanged : .none
        }
    }

    /// A `historyDone`: the batch merges into the host's rows, or replaces them when its request ran in another zone
    /// than they were computed in. Rows stored without a zone take the batch's. Without a request of this connection,
    /// the rows keep their zone.
    private func commit(_ days: [RemoteHistoryBatch.Day], host: String, connection: Int, staged: Bool,
                        database: HistoryDatabase) -> Effect {
        let request = requested.removeValue(forKey: host).flatMap { $0.connection == connection ? $0 : nil }
        let stored = rows.zones[host]
        let zone = request?.zone ?? stored ?? zone()
        let replace = stored.map { $0 != zone } ?? false
        let relabels = stored == nil && rows.hosts.contains(host)
        guard replace || staged || !days.isEmpty || relabels else { return succeeded(host) }
        do {
            try database.commitRemote(days, host: host, name: names[host], zone: zone, replace: replace, staged: staged,
                                      limits: sync.limits)
        } catch is HistoryDatabase.RemoteHistoryTooLarge {
            return tooLarge.insert(host).inserted ? .tooLargeChanged : .none
        } catch {
            // Nothing is stored, so the next sync sends the days again.
            Self.logger.error("Remote history could not be saved")
            return .none
        }
        _ = succeeded(host)
        if staged {
            load(from: database)
        } else {
            rows.store(days, host: host, name: names[host], zone: zone, replace: replace)
            version += 1
        }
        return .changed
    }

    private func succeeded(_ host: String) -> Effect {
        tooLarge.remove(host) != nil ? .tooLargeChanged : .none
    }

    /// The settings' hosts with their display names; stored hosts get the new names, removed ones keep theirs.
    func setHosts(_ hosts: [String: String], database: HistoryDatabase?) -> Effect {
        names = hosts
        guard let database else { return .none }
        return applyNames(database) ? .changed : .none
    }

    /// Deletes a host's rows and batch (REMOTE 2.4).
    func deleteHost(_ id: String, database: HistoryDatabase) -> Effect {
        sync.drop(host: id)
        requested[id] = nil
        unstaged.remove(id)
        let wasTooLarge = tooLarge.remove(id) != nil
        do {
            try database.deleteRemoteHost(id)
        } catch {
            Self.logger.error("Remote history could not be deleted")
            return wasTooLarge ? .tooLargeChanged : .none
        }
        guard rows.hosts.contains(id) || rows.names[id] != nil else { return wasTooLarge ? .tooLargeChanged : .none }
        rows.remove(host: id)
        version += 1
        return .changed
    }

    private func applyNames(_ database: HistoryDatabase) -> Bool {
        var changed = false
        for host in rows.hosts.sorted() {
            guard let name = names[host], rows.names[host] != name else { continue }
            guard (try? database.renameRemoteHost(host, to: name)) != nil else { continue }
            rows.rename(host: host, to: name)
            changed = true
        }
        if changed { version += 1 }
        return changed
    }
}
