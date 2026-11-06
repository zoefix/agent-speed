import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import Darwin

/// `--orb-bench DIR`: puts real floating orbs (the production window manager, panels, presenters and frame clock) in
/// the top-right corner of the main display, drives them with scripted snapshots and response events, and measures
/// each scenario of the DESIGN 2a budget over a 10 s window: process CPU, frames per orb, CPU and GPU time per frame.
/// Also counts heap allocations of warm frames per style. Writes `DIR/orb-bench.txt` and quits. Use a release build.
///
/// `AGENT_SPEED_BENCH_STYLES` (comma separated) limits the styles, `AGENT_SPEED_BENCH_QUICK=1` runs only the single
/// 148 pt orb scenarios, `AGENT_SPEED_BENCH_SECONDS` sets the measuring window (10), `AGENT_SPEED_BENCH_DISPLAY_SYNC=0`
/// turns `displaySync` off.
@MainActor
final class OrbBenchmark {
    enum Check {
        case shown
        case hidden
        case occluded
    }

    struct State {
        let name: String
        /// Speed as a fraction of the redline.
        let fraction: Double
        let level: EffectsLevel
        var check = Check.shown

        static let idle = State(name: "idle", fraction: 0, level: .standard)
        static let cruise = State(name: "cruise", fraction: 0.1, level: .standard)
        static let redline = State(name: "redline", fraction: 0.9, level: .standard)
        static let overdrive = State(name: "overdrive", fraction: 1.3, level: .standard)
        static let redlineMax = State(name: "redline", fraction: 0.9, level: .max)
    }

    struct Case {
        let label: String
        let styles: [OrbStyleKind]
        let size: CGFloat
        let count: Int
        let states: [State]
        var clickThrough = true
    }

    struct Row {
        let label: String
        let count: Int
        let size: CGFloat
        let state: State
        let cpu: Double
        let fps: Double
        let cpuMicros: Double
        let gpuMillis: Double
    }

    private static var current: OrbBenchmark?

    @MainActor
    private final class SpeedBox {
        var value = 0.0
    }

    private let directory: URL
    private let seconds: Double
    private var rows: [Row] = []
    private var lines: [String] = []
    private var random = OrbRandom(seed: 0xB3_4C_11)
    private var eventCarry = 0.0

    static func start(into directory: URL) {
        let bench = OrbBenchmark(directory: directory)
        current = bench
        Task { @MainActor in
            await bench.run()
            NSApp.terminate(nil)
        }
    }

    private init(directory: URL) {
        self.directory = directory
        seconds = ProcessInfo.processInfo.environment["AGENT_SPEED_BENCH_SECONDS"].flatMap(Double.init) ?? 10
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    private static var cases: [Case] {
        let environment = ProcessInfo.processInfo.environment
        let styles = environment["AGENT_SPEED_BENCH_STYLES"].map { $0.split(separator: ",").compactMap { OrbStyleKind(rawValue: String($0)) } }
            ?? OrbStyleKind.allCases
        let quick = environment["AGENT_SPEED_BENCH_QUICK"] == "1"
        var cases: [Case] = []
        if environment["AGENT_SPEED_BENCH_VISIBILITY"] == "1" {
            var hidden = State.redline
            hidden.check = .hidden
            var occluded = State.redline
            occluded.check = .occluded
            var occludedIdle = State.idle
            occludedIdle.check = .occluded
            return [Case(label: "led", styles: [.led], size: 148, count: 1, states: [hidden, occluded, occludedIdle])]
        }
        for style in styles {
            cases.append(Case(label: style.rawValue, styles: [style], size: 148, count: 1,
                              states: [.idle, .cruise, .redline, .overdrive, .redlineMax]))
            guard !quick else { continue }
            cases.append(Case(label: style.rawValue, styles: [style], size: 240, count: 1, states: [.redline, .redlineMax]))
            cases.append(Case(label: style.rawValue, styles: [style], size: 148, count: 4, states: [.redline]))
            cases.append(Case(label: style.rawValue, styles: [style], size: 148, count: 8, states: [.redline]))
        }
        if let only = environment["AGENT_SPEED_BENCH_STATES"]?.split(separator: ",").map(String.init) {
            return cases.map { item in
                Case(label: item.label, styles: item.styles, size: item.size, count: item.count,
                     states: item.states.filter { only.contains($0.name + ($0.level == .max ? "-max" : "")) })
            }.filter { !$0.states.isEmpty }
        }
        guard !quick, styles.count == OrbStyleKind.allCases.count else { return cases }
        let mixed = OrbStyleKind.allCases + [.led, .needle, .reactor]
        cases.append(Case(label: "mixed", styles: mixed, size: 148, count: 8, states: [.idle, .redline, .redlineMax]))
        var hidden = State.redline
        hidden.check = .hidden
        var occluded = State.redline
        occluded.check = .occluded
        cases.append(Case(label: "led", styles: [.led], size: 148, count: 1, states: [hidden, occluded]))
        return cases
    }

    // MARK: Run

    private func run() async {
        #if DEBUG
        lines.append("build: debug (use `swift build -c release` for representative numbers)")
        #else
        lines.append("build: release")
        #endif
        let screen = NSScreen.main ?? NSScreen.screens.first
        let refresh = screen?.maximumFramesPerSecond ?? 60
        lines.append(String(format: "display: %d Hz, %.0f× backing scale; CPU = user + system time of the whole process over %.0f s",
                            refresh, screen?.backingScaleFactor ?? 2, seconds))
        if ProcessInfo.processInfo.environment["AGENT_SPEED_BENCH_NO_ALLOC"] != "1" {
            lines.append(contentsOf: OrbAllocationCheck.run())
        }
        lines.append("")
        for benchCase in Self.cases {
            await run(benchCase)
        }
        writeReport()
    }

    private func run(_ benchCase: Case) async {
        guard let screen = NSScreen.main ?? NSScreen.screens.first, let display = screen.orbDisplayUUID else { return }
        let suite = "AgentSpeedOrbBench"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        defaults.set(Self.seed(benchCase, screen: screen, display: display).data(), forKey: OrbsController.documentKey)
        defaults.set(true, forKey: "orbsVisible")
        defaults.set("dark", forKey: "appearance")
        if let sync = ProcessInfo.processInfo.environment["AGENT_SPEED_BENCH_DISPLAY_SYNC"] {
            defaults.set(sync != "0", forKey: "displaySync")
        }
        let model = AppModel(
            settings: AppSettings(defaults: defaults),
            updates: UpdateController(configuration: .current(environment: [:]), store: UpdateStore(defaults: defaults))
        )
        model.activate(live: false)
        let speed = SpeedBox()
        let redline = model.settings.redline
        let publisher = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                self?.publish(model: model, speed: speed.value, redline: redline)
                try? await Task.sleep(for: .seconds(0.5))
            }
        }
        defer { publisher.cancel() }
        let manager = OrbWindowManager(model: model)
        try? await Task.sleep(for: .seconds(1.5))

        for state in benchCase.states {
            model.settings.effects = ProcessInfo.processInfo.environment["AGENT_SPEED_BENCH_LEVEL"]
                .flatMap(EffectsLevel.init(rawValue:)) ?? state.level
            speed.value = redline * state.fraction
            var occluder: NSWindow?
            switch state.check {
            case .shown: break
            case .hidden: model.orbs.masterVisible = false
            case .occluded: occluder = Self.occluder(over: benchCase, manager: manager, model: model)
            }
            // Idle needs the 3 s idle delay and the 0.8 s hold before the orb rests.
            try? await Task.sleep(for: .seconds(state.fraction == 0 ? 6 : 2.5))
            let canvases = model.orbs.orbs.compactMap { manager.window(for: $0.id)?.host.canvas }
            for canvas in canvases {
                canvas.resetStats()
                _ = canvas.encoder?.gpuTime.drain()
            }
            let cpuStart = Self.cpuTime()
            let wallStart = Date()
            try? await Task.sleep(for: .seconds(seconds))
            let wall = Date().timeIntervalSince(wallStart)
            if state.check == .occluded {
                let states = model.orbs.orbs.compactMap { manager.window(for: $0.id)?.host.window?.occlusionState.contains(.visible) }
                lines.append("occlusion check: an opaque window over the panels; the system reports them visible = \(states)")
            }
            let cpu = (Self.cpuTime() - cpuStart) / wall * 100
            let frames = canvases.reduce(0) { $0 + $1.framesDrawn }
            let nanoseconds = canvases.reduce(UInt64(0)) { $0 + $1.cpuNanoseconds }
            var gpuSeconds = 0.0
            var gpuFrames = 0
            for canvas in canvases {
                guard let (seconds, count) = canvas.encoder?.gpuTime.drain() else { continue }
                gpuSeconds += seconds
                gpuFrames += count
            }
            rows.append(Row(
                label: benchCase.label, count: benchCase.count, size: benchCase.size, state: state, cpu: cpu,
                fps: Double(frames) / wall / Double(max(1, canvases.count)),
                cpuMicros: frames > 0 ? Double(nanoseconds) / Double(frames) / 1000 : 0,
                gpuMillis: gpuFrames > 0 ? gpuSeconds / Double(gpuFrames) * 1000 : 0
            ))
            occluder?.orderOut(nil)
            if state.check == .hidden { model.orbs.masterVisible = true }
            print(Self.format(rows[rows.count - 1]))
        }
        manager.shutdown()
        model.shutdown()
        try? await Task.sleep(for: .seconds(0.6))
    }

    /// One engine tick: the live speed plus the prototype's response events (`0.3 + 1.5 h` per second, speed
    /// `v × U(0.5, 1.3)`, output U(80, 6000)).
    private var snapshots: [Double: SpeedSnapshot] = [:]

    private func publish(model: AppModel, speed: Double, redline: Double) {
        // One snapshot per speed, re-stamped: the harness itself should cost next to nothing.
        var snapshot = snapshots[speed] ?? PreviewData.snapshot(speed: speed, redline: redline)
        snapshots[speed] = snapshot
        snapshot.timestamp = Date()
        var events: [SpeedEvent] = []
        if speed > 0 {
            let heat = HeatPalette.heat(value: speed, redline: redline)
            eventCarry += (0.3 + 1.5 * heat) * 0.5 * random.range(0.5...1.5)
            while eventCarry >= 1 {
                eventCarry -= 1
                let output = Int(random.range(80...6000))
                let responseSpeed = speed * random.range(0.5...1.3)
                events.append(.responseCompleted(ResponseSummary(
                    source: .claudeCode, project: "bench", model: nil, outputTokens: output, totalTokens: output * 20,
                    duration: Double(output) / responseSpeed, speed: responseSpeed, endedAt: Date()
                )))
            }
        }
        model.store.publishLive(snapshot, events: events)
    }

    /// The case's orbs in rows of four from the top-right corner of the usable area, touching at 12 pt.
    private static func seed(_ benchCase: Case, screen: NSScreen, display: String) -> OrbsDocument {
        let visible = screen.visibleFrame
        let radius = benchCase.size / 2
        let first = OrbSnap.defaultCenter(radius: radius, in: visible)
        var orbs: [OrbConfig] = []
        var document = OrbsDocument(orbs: [])
        for index in 0..<benchCase.count {
            let style = benchCase.styles[index % benchCase.styles.count]
            // Click-through: the pointer monitors wake the app on every mouse move anywhere on screen, which depends on
            // what the user does meanwhile; `pointer` rows measure that cost on its own.
            orbs.append(OrbConfig(id: UUID().uuidString, sources: .all, style: style, size: Double(benchCase.size),
                                  clickThrough: benchCase.clickThrough))
        }
        document = OrbsDocument(orbs: orbs)
        for (index, orb) in orbs.enumerated() {
            let column = CGFloat(index % 4)
            let row = CGFloat(index / 4)
            let center = CGPoint(x: first.x - column * (benchCase.size + 12), y: first.y - row * (benchCase.size + 12))
            let record = OrbPositionRecord(center: center, radius: radius, visible: visible)
            document.setEntry(OrbLayoutEntry(display: display, record: record), orb: orb.id, arrangement: OrbsController.arrangementKey)
        }
        return document
    }

    /// An opaque window over every orb panel, so the system reports them fully occluded.
    private static func occluder(over benchCase: Case, manager: OrbWindowManager, model: AppModel) -> NSWindow? {
        let frames = model.orbs.orbs.compactMap { manager.window(for: $0.id)?.host.window?.frame }
        guard let first = frames.first else { return nil }
        let rect = frames.dropFirst().reduce(first) { $0.union($1) }.insetBy(dx: -8, dy: -8)
        let window = NSWindow(contentRect: rect, styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.ignoresMouseEvents = true
        window.isOpaque = true
        window.backgroundColor = NSColor(srgbRed: 0.16, green: 0.2, blue: 0.27, alpha: 1)
        window.level = .statusBar
        window.hasShadow = false
        let content = NSView(frame: CGRect(origin: .zero, size: rect.size))
        content.wantsLayer = true
        content.layer?.backgroundColor = NSColor(srgbRed: 0.16, green: 0.2, blue: 0.27, alpha: 1).cgColor
        content.layer?.isOpaque = true
        window.contentView = content
        window.orderFrontRegardless()
        return window
    }

    // MARK: Report

    /// DESIGN 2a, Budgets (macOS, % of one core).
    private static func budget(_ row: Row) -> Double? {
        if row.count == 8 { return row.state.fraction == 0 ? nil : (row.state.level == .standard ? 12 : nil) }
        guard row.count == 1 else { return nil }
        if row.state.check != .shown { return 0.2 }
        switch row.state.fraction {
        case 0: return 0.2
        case ..<0.45: return 1.5
        default: return row.state.level == .max ? 4 : 3
        }
    }

    private static func format(_ row: Row) -> String {
        let check: String = switch row.state.check {
        case .shown: ""
        case .hidden: " (hidden)"
        case .occluded: " (occluded)"
        }
        let budget = Self.budget(row)
        let budgetText = budget.map { String(format: "≤ %4.1f", $0) } ?? "     –"
        let pass = budget.map { row.cpu <= $0 ? "ok" : "OVER" } ?? ""
        var gpuNote = ""
        if row.size >= 240, row.gpuMillis > 1 { gpuNote = " GPU OVER" }
        let name = "\(row.state.name)\(row.state.level == .max ? " Max" : "")\(check)"
        return String(format: "%-8@ %d × %3.0f  %-22@ CPU %6.2f %%  %@  %-4@  %6.1f fps/orb  %6.0f µs/frame  %5.2f ms GPU%@",
                      row.label, row.count, row.size, name, row.cpu, budgetText, pass, row.fps, row.cpuMicros, row.gpuMillis, gpuNote)
    }

    private func writeReport() {
        var text = lines.joined(separator: "\n") + "\n"
        text += "Budget (DESIGN 2a, macOS): idle ≤ 0.2 %, cruise ≤ 1.5 %, redline/overdrive Standard ≤ 3 %, Max ≤ 4 %, eight orbs at redline ≤ 12 %, GPU ≤ 1.0 ms per orb frame at S 240 @2x\n\n"
        text += rows.map(Self.format).joined(separator: "\n") + "\n"
        try? text.write(to: directory.appendingPathComponent("orb-bench.txt"), atomically: true, encoding: .utf8)
        print(text, terminator: "")
    }

    /// User + system CPU seconds of this process.
    private static func cpuTime() -> Double {
        var usage = rusage()
        getrusage(RUSAGE_SELF, &usage)
        func seconds(_ t: timeval) -> Double { Double(t.tv_sec) + Double(t.tv_usec) / 1e6 }
        return seconds(usage.ru_utime) + seconds(usage.ru_stime)
    }
}
