import AgentSpeedCore
import Foundation

/// `--watch N --remote DESTINATION --history`: the remote hosts' daily history sync (REMOTE 8.2, 8.3) next to the live
/// watch. It prints every sync request and `historyDone` as it happens and, with each watch line, the stored days of
/// every host. The local logs are not analysed: the history service reads empty roots, so only the hosts' rows show.
/// `--history-store PATH` keeps the store between runs (a second run sends the stored digests).
@MainActor
final class RemoteHistoryProbe {
    /// Logs the sync's events, then hands them to the history.
    private final class Sink: RemoteHistorySink {
        let sync: RemoteHistorySync
        let names: [String: String]

        init(sync: RemoteHistorySync, names: [String: String]) {
            self.sync = sync
            self.names = names
        }

        func remoteHistory(_ event: RemoteHostEvent, requests: RemoteHistoryRequests) {
            let name = names[event.hostId] ?? event.hostId
            switch event.kind {
            case .historyDue:
                Self.write(["event": "historyDue", "host": name])
            case .message(.historyDone(let days, let analyzed), _):
                Self.write(["event": "historyDone", "host": name], numbers: ["days": days, "analyzed": analyzed])
            case .message(.error(let error), _):
                Self.write(["event": "historyFailed", "host": name, "detail": error.detail])
            default:
                break
            }
            sync.remoteHistory(event, requests: requests)
        }

        private static func write(_ strings: [String: String], numbers: [String: Int] = [:]) {
            var line = JSONLine()
            for key in strings.keys.sorted() { line.add(key, strings[key]) }
            for key in numbers.keys.sorted() { line.add(key, numbers[key] ?? 0) }
            FileHandle.standardOutput.write(Data((line.text + "\n").utf8))
        }
    }

    private let store = HistoryStore()
    private let sync = RemoteHistorySync()
    private let sink: Sink
    private let service: HistoryService
    private let emptyRoot: URL

    init(coordinator: RemoteCoordinator, configuration: MonitorConfiguration, storePath: String?) {
        let names = coordinator.document.displayNames()
        sink = Sink(sync: sync, names: names)
        emptyRoot = FileManager.default.temporaryDirectory.appendingPathComponent("agent-speed-probe-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: emptyRoot, withIntermediateDirectories: true)
        let storeURL = storePath.map { URL(fileURLWithPath: $0) } ?? emptyRoot.appendingPathComponent("history.sqlite")
        service = HistoryService(store: store, configuration: Self.withoutLocalLogs(configuration, root: emptyRoot),
                                 storeURL: storeURL, remote: sync)
        sync.setHosts(coordinator.document.hosts.map { (id: $0.id, name: names[$0.id] ?? $0.automaticName) })
        coordinator.historySink = sink
        service.start()
    }

    func stop() {
        service.stop()
        try? FileManager.default.removeItem(at: emptyRoot)
    }

    /// Every stored remote host: its days (day and source rows), first and last day, and output.
    var summary: String {
        var hosts: [String: (rows: Int, first: String, last: String, output: Int)] = [:]
        for day in store.days where !day.isLocal {
            var entry = hosts[day.host] ?? (0, day.day, day.day, 0)
            entry.rows += 1
            entry.first = min(entry.first, day.day)
            entry.last = max(entry.last, day.day)
            entry.output += day.totals.output
            hosts[day.host] = entry
        }
        let items = hosts.keys.sorted().map { id -> String in
            let entry = hosts[id]!
            var item = JSONLine()
            item.add("host", store.hostNames[id] ?? id)
            item.add("rows", entry.rows)
            item.add("first", entry.first)
            item.add("last", entry.last)
            item.add("output", entry.output)
            item.add("tooLarge", store.remoteTooLarge.contains(id))
            return item.text
        }
        return JSONLine.array(items)
    }

    /// The configuration with every source's only location an empty folder: the local analysis finds nothing.
    private static func withoutLocalLogs(_ configuration: MonitorConfiguration, root: URL) -> MonitorConfiguration {
        var empty = configuration
        for folder in TranscriptFolder.allCases { empty.setLocation(root, for: folder) }
        return empty
    }
}
