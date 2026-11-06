import AgentSpeedCore
import CryptoKit
import Foundation

/// `--watch N`: runs the live monitor and prints one compact snapshot per second; with `--remote` it also watches
/// those SSH hosts (REMOTE) and prints their status and each agent's host, and with `--history` their synced days.
@MainActor
enum WatchMode {
    static func run(configuration: MonitorConfiguration, seconds: Int, remotes: [String] = [], history: Bool = false,
                    historyStore: String? = nil) {
        let store = SpeedStore()
        let monitor = LiveMonitor(store: store, configuration: configuration)
        var coordinator: RemoteCoordinator?
        var remoteHistory: RemoteHistoryProbe?
        if !remotes.isEmpty {
            let remote = RemoteCoordinator(monitor: monitor)
            remote.update(hosts: RemoteHostsDocument(hosts: remotes.map { RemoteHost(id: stableId($0), destination: $0) }),
                          networkEnabled: true, priceTable: PriceBook.bundled.data)
            coordinator = remote
            if history {
                remoteHistory = RemoteHistoryProbe(coordinator: remote, configuration: configuration, storePath: historyStore)
            }
        }
        monitor.start()
        let started = Date()
        for second in 1...seconds {
            RunLoop.main.run(until: started.addingTimeInterval(TimeInterval(second)))
            var text = line(for: store.snapshot)
            if let remoteHistory { text = String(text.dropLast()) + ",\"remoteHistory\":" + remoteHistory.summary + "}" }
            FileHandle.standardOutput.write(Data((text + "\n").utf8))
        }
        coordinator?.stop()
        remoteHistory?.stop()
        monitor.stop()
    }

    /// The same host id for a destination in every run, so a kept history store recognises its host.
    static func stableId(_ destination: String) -> String {
        var bytes = Array(SHA256.hash(data: Data(destination.utf8)).prefix(16))
        bytes[6] = bytes[6] & 0x0F | 0x50
        bytes[8] = bytes[8] & 0x3F | 0x80
        return UUID(uuid: (bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7], bytes[8],
                           bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15])).uuidString
    }

    static func line(for snapshot: SpeedSnapshot) -> String {
        var line = JSONLine()
        line.add("t", Int64?.some(Int64((snapshot.timestamp.timeIntervalSince1970 * 1000).rounded(.down))))
        switch snapshot.state {
        case .starting(let progress):
            line.add("state", "starting")
            line.add("progress", progress)
        case .running:
            line.add("state", "running")
        case .noSources:
            line.add("state", "noSources")
        }
        line.add("live", raw: rate(snapshot.live))
        line.add("estimatedShare", snapshot.liveEstimatedShare)
        line.add("generating", snapshot.generatingCount)
        line.add("tools", snapshot.toolsCount)
        let agents = snapshot.agents.map { agent -> String in
            var item = JSONLine()
            item.add("id", (agent.id as NSString).lastPathComponent)
            if !snapshot.hosts.isEmpty { item.add("host", hostName(agent.host, in: snapshot)) }
            item.add("source", agent.source.rawValue)
            item.add("phase", agent.phase.rawValue)
            item.add("live", agent.live.output)
            item.add("parent", agent.parentId.map { ($0 as NSString).lastPathComponent })
            if !agent.modes.facts.isEmpty, let bytes = try? JSONEncoder().encode(agent.modes), let json = String(data: bytes, encoding: .utf8) {
                item.add("modes", raw: json)
            }
            item.add("effort", raw: DumpToday.attribution(agent.current.effort))
            item.add("speed", raw: DumpToday.attribution(agent.current.speed))
            return item.text
        }
        line.add("agents", raw: JSONLine.array(agents))
        let configured = snapshot.sources.compactMap { source -> String? in
            guard !source.configuredModes.facts.isEmpty,
                  let bytes = try? JSONEncoder().encode(source.configuredModes), let json = String(data: bytes, encoding: .utf8) else { return nil }
            var item = JSONLine()
            item.add("source", source.kind.rawValue)
            item.add("configuredModes", raw: json)
            return item.text
        }
        if !configured.isEmpty { line.add("sources", raw: JSONLine.array(configured)) }
        var today = JSONLine()
        today.add("requests", snapshot.today.requests)
        today.add("input", snapshot.today.input)
        today.add("cachedInput", snapshot.today.cachedInput)
        today.add("cacheWriteInput", snapshot.today.cacheWriteInput)
        today.add("output", snapshot.today.output)
        today.add("reasoning", snapshot.today.reasoning)
        line.add("today", raw: today.text)
        line.add("peak", raw: rate(snapshot.peakToday))
        if !snapshot.hosts.isEmpty { line.add("hosts", raw: JSONLine.array(snapshot.hosts.map(host))) }
        return line.text
    }

    /// One host: its status, clock offset, live value, today's output and main sessions.
    private static func host(_ host: HostStatus) -> String {
        var item = JSONLine()
        item.add("name", host.name)
        item.add("status", host.remote?.status.code ?? (host.isLocal ? "local" : "unknown"))
        if let offset = host.clockOffsetMs { item.add("clockOffsetMs", Int64?.some(offset)) }
        item.add("live", host.live.output)
        item.add("todayOutput", host.today.output)
        item.add("todayRequests", host.today.requests)
        item.add("sessions", host.sessions)
        return item.text
    }

    private static func hostName(_ id: String, in snapshot: SpeedSnapshot) -> String {
        snapshot.hosts.first { $0.id == id }?.name ?? id
    }

    private static func rate(_ rate: Rate) -> String {
        var line = JSONLine()
        line.add("output", rate.output)
        line.add("total", rate.total)
        return line.text
    }
}
