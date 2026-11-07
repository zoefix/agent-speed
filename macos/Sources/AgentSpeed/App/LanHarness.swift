#if DEBUG
import AgentSpeedCore
import AppKit

/// `--lan-e2e`: runs the real local network feed (DESIGN 14, LAN) on synthetic live values, for checking it from
/// another machine with `spec/tools/lan_reference.py`. Settings live in the `AgentSpeedLanE2E` suite; no agent data is
/// read. Environment: `AGENT_SPEED_LAN_E2E_PORT` (7247), `AGENT_SPEED_LAN_E2E_KEY` (K7Q4M2X9PT3D),
/// `AGENT_SPEED_LAN_E2E_TARGETS` (`udp=192.168.1.5:7250;http=http://192.168.1.5:8080/hook`),
/// `AGENT_SPEED_LAN_E2E_TARGET_KEY`, `AGENT_SPEED_LAN_E2E_SECONDS` (60). Prints the statuses every 5 s, then quits.
@MainActor
enum LanHarness {
    private static var feed: Timer?
    private static var tick = 0

    static func start() {
        let environment = ProcessInfo.processInfo.environment
        let suite = "AgentSpeedLanE2E"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        let model = AppModel(settings: AppSettings(defaults: defaults),
                             updates: UpdateController(configuration: .current(environment: [:]),
                                                       store: UpdateStore(defaults: defaults)))
        model.activate(live: false)
        var document = LanDocument()
        document.enableServer()
        document.server.port = environment["AGENT_SPEED_LAN_E2E_PORT"].flatMap(Int.init) ?? LanDocument.defaultPort
        document.server.key = environment["AGENT_SPEED_LAN_E2E_KEY"] ?? "K7Q4M2X9PT3D"
        let key = environment["AGENT_SPEED_LAN_E2E_TARGET_KEY"]
        for entry in (environment["AGENT_SPEED_LAN_E2E_TARGETS"] ?? "").split(separator: ";") {
            let parts = entry.split(separator: "=", maxSplits: 1).map(String.init)
            guard parts.count == 2, let kind = LanDocument.Kind(rawValue: parts[0]) else { continue }
            if !document.add(.init(kind: kind, address: parts[1], key: key)) { print("LAN-E2E invalid target \(entry)") }
        }
        model.settings.lan = document
        model.lan.activate()
        print("LAN-E2E port \(document.server.port) key \(LanDocument.displayKey(document.server.key ?? "")) "
              + "targets \(document.targets.count) addresses \(LanController.localAddresses())")
        let timer = Timer(timeInterval: 0.5, repeats: true) { _ in
            MainActor.assumeIsolated { publish(model) }
        }
        RunLoop.main.add(timer, forMode: .common)
        feed = timer
        let seconds = environment["AGENT_SPEED_LAN_E2E_SECONDS"].flatMap(Double.init) ?? 60
        Task { @MainActor in
            let end = Date().addingTimeInterval(seconds)
            while Date() < end {
                try? await Task.sleep(for: .seconds(5))
                let server = model.lan.serverStatus
                let targets = model.settings.lan.targets.map { "\($0.address)=\(model.lan.status(of: $0).state.rawValue)" }
                print("LAN-E2E server \(server.state.rawValue) devices \(server.devices) targets \(targets)")
            }
            model.lan.shutdown()
            UserDefaults.standard.removePersistentDomain(forName: suite)
            exit(0)
        }
    }

    /// A speed that rises and falls over 20 s with idle gaps, one generating Claude Code agent while busy.
    private static func publish(_ model: AppModel) {
        tick += 1
        let phase = Double(tick % 40) / 40
        let busy = phase < 0.75
        let speed = busy ? 40 + 160 * sin(phase / 0.75 * .pi) : 0
        var snapshot = PreviewData.snapshot(speed: speed, redline: 2000)
        if !busy {
            snapshot.live = .zero
            for index in snapshot.sources.indices {
                snapshot.sources[index].live = .zero
                snapshot.sources[index].liveEstimated = .zero
            }
        }
        snapshot.report = ReportLiveFacts(agents: [.claudeCode: ReportAgentCounts(generating: busy ? 1 : 0, tools: 0,
                                                                                  active: 1),
                                                   .codex: ReportAgentCounts(generating: 0, tools: busy ? 0 : 1, active: 1)])
        model.store.publishLive(snapshot)
    }
}
#endif
