import AgentSpeedCore
import Darwin
import Foundation

/// `--traffic N [--traffic-interval 0|1|5]`: runs the live monitor and the traffic monitor for real and prints, once per
/// second, this computer's rate, the process sampler's state, every source's own and tool rates with today's totals,
/// and the attributed agent trees; then the classified processes and the cost. Traffic rows go to a scratch store
/// (`--history-store` to choose it), never to the app's history. `--no-live` skips the transcript monitor.
@MainActor
enum TrafficMode {
    static func run(configuration: MonitorConfiguration, seconds: Int, interval: Int?, storePath: String?, live runsLive: Bool) {
        let speed = SpeedStore()
        let live = LiveMonitor(store: speed, configuration: configuration)
        let traffic = TrafficStore()
        let store = storePath.map { URL(fileURLWithPath: $0) }
            ?? FileManager.default.temporaryDirectory.appendingPathComponent("agent-speed-probe-traffic.sqlite")
        let monitor = TrafficMonitor(store: traffic, storeURL: store)
        if runsLive { live.start() }
        monitor.start(enabled: true)
        if let interval { monitor.setForcedInterval(interval) }
        let started = Date()
        let cpuStart = cpuSeconds()
        print("traffic: store \(store.path), pid \(getpid())")
        for second in 1...seconds {
            RunLoop.main.run(until: started.addingTimeInterval(TimeInterval(second)))
            monitor.update(agents: speed.snapshot.agents)
            print(line(second: second, snapshot: traffic.snapshot, agents: speed.snapshot.agents))
        }
        let diagnostics = monitor.diagnostics()
        let cpu = cpuSeconds() - cpuStart
        let elapsed = Date().timeIntervalSince(started)
        monitor.stop()
        live.stop()
        summary(diagnostics, snapshot: traffic.snapshot, days: traffic.days, cpu: cpu, elapsed: elapsed)
    }

    private static func rate(_ rate: TrafficRate?) -> String {
        guard let rate else { return "↓–  ↑–" }
        return "↓\(TrafficFormat.rate(rate.rx)) ↑\(TrafficFormat.rate(rate.tx))"
    }

    private static func total(_ bytes: TrafficBytes) -> String {
        "↓\(TrafficFormat.total(bytes.rx)) ↑\(TrafficFormat.total(bytes.tx))"
    }

    private static func state(_ state: ProcessTrafficState) -> String {
        switch state {
        case .off: "off"
        case .starting: "starting"
        case .standby: "standby"
        case .running(let interval): "running(\(interval)s)"
        case .unavailable(let reason): "unavailable(\(reason.rawValue))"
        }
    }

    /// The quota sources' apps the table found this tick (QUOTAS 6), as the 額度 card reads them.
    private static func quotaApps(_ apps: QuotaApps?) -> String {
        guard let apps else { return "apps –" }
        let running = apps.running.map(\.rawValue).sorted().joined(separator: ",")
        return "apps desktop=\(apps.claudeDesktop) runs \(running.isEmpty ? "–" : running)"
    }

    static func line(second: Int, snapshot: TrafficSnapshot, agents: [AgentStatus]) -> String {
        var parts = ["\(second)s", "machine \(rate(snapshot.machineLive)) today \(snapshot.machineToday.rx)/\(snapshot.machineToday.tx) B",
                     state(snapshot.processState), quotaApps(snapshot.quotaApps)]
        for source in SourceKind.allCases {
            guard let traffic = snapshot.sources[source] else { continue }
            let tools = traffic.liveWithTools.map { all in
                TrafficRate(rx: all.rx - (traffic.liveOwn?.rx ?? 0), tx: all.tx - (traffic.liveOwn?.tx ?? 0))
            }
            parts.append("\(source.rawValue) own \(rate(traffic.liveOwn)) tools \(rate(tools)) today own \(total(traffic.today.own)) tools \(total(traffic.today.tools))")
        }
        let names = Dictionary(agents.map { ($0.id, "\($0.project)/\(($0.id as NSString).lastPathComponent.prefix(12))") },
                               uniquingKeysWith: { first, _ in first })
        for (id, agent) in snapshot.agents.sorted(by: { $0.key < $1.key }) {
            parts.append("agent \(names[id] ?? id) live \(rate(agent.liveWithTools)) today \(total(agent.today.total(includeTools: true)))")
        }
        return parts.joined(separator: " | ")
    }

    private static func summary(_ diagnostics: TrafficDiagnostics?, snapshot: TrafficSnapshot, days: [TrafficDay], cpu: Double,
                                elapsed: TimeInterval) {
        print("--- summary")
        if let diagnostics {
            print("counted interfaces: \(diagnostics.countedInterfaces.joined(separator: " ")) (list read \(diagnostics.interfaceListReads) times)")
            print("process table: \(diagnostics.tableSize) processes, \(diagnostics.processes.count) classified")
            for process in diagnostics.processes {
                let name = (process.executable as NSString).lastPathComponent
                print("  \(process.pid)\t\(process.source.rawValue)/\(process.role)\troot \(process.rootPid)\t\(process.alive ? "alive" : "exited")\t\(name)\t\(process.executable)")
            }
            print("root agent processes with traffic:")
            for root in diagnostics.roots {
                print("  \(root.pid)\t\(root.source?.rawValue ?? "?")\tcwd \(root.workingDirectory ?? "–")\town \(total(root.own))\ttools \(total(root.tools))")
                for (agent, bytes) in root.assigned.sorted(by: { $0.key < $1.key }) {
                    print("      → \(agent.isEmpty ? "unassigned" : agent): \(total(bytes))")
                }
            }
            print("nettop: pid \(diagnostics.nettopPid.map(String.init) ?? "–"), interval \(diagnostics.nettopInterval.map(String.init) ?? "–"), launches \(diagnostics.nettopLaunches), samples \(diagnostics.samples)")
            print(String(format: "ticks: %d, first tick CPU %.0f µs, later ticks %.0f µs each, nettop output parsing %.0f µs in total",
                         diagnostics.ticks, diagnostics.firstTickMicros, diagnostics.averageTickMicros, diagnostics.nettopReadMicros))
        }
        print("machine today \(total(snapshot.machineToday)), sampled \(snapshot.machineSampledMs) ms; process sampled \(snapshot.processSampledMs) ms")
        for (source, traffic) in snapshot.sources.sorted(by: { $0.key.rawValue < $1.key.rawValue }) {
            print("  \(source.rawValue): own \(total(traffic.today.own)), tools \(total(traffic.today.tools)), unassigned \(total(traffic.unassigned))")
        }
        print("stored rows: \(days.count)")
        print(String(format: "probe CPU: %.3f s over %.1f s (%.2f %% of a core)", cpu, elapsed, cpu / max(elapsed, 0.001) * 100))
    }

    private static func cpuSeconds() -> Double {
        var usage = rusage()
        getrusage(RUSAGE_SELF, &usage)
        return Double(usage.ru_utime.tv_sec) + Double(usage.ru_utime.tv_usec) / 1e6
            + Double(usage.ru_stime.tv_sec) + Double(usage.ru_stime.tv_usec) / 1e6
    }
}
