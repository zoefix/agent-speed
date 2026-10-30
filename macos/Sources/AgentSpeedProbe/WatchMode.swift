import AgentSpeedCore
import Foundation

/// `--watch N`: runs the live monitor and prints one compact snapshot per second.
@MainActor
enum WatchMode {
    static func run(configuration: MonitorConfiguration, seconds: Int) {
        let store = SpeedStore()
        let monitor = LiveMonitor(store: store, configuration: configuration)
        monitor.start()
        let started = Date()
        for second in 1...seconds {
            RunLoop.main.run(until: started.addingTimeInterval(TimeInterval(second)))
            FileHandle.standardOutput.write(Data((line(for: store.snapshot) + "\n").utf8))
        }
        monitor.stop()
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
        return line.text
    }

    private static func rate(_ rate: Rate) -> String {
        var line = JSONLine()
        line.add("output", rate.output)
        line.add("total", rate.total)
        return line.text
    }
}
