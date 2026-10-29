import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// What one floating orb shows (DESIGN 2b, Engine; METRICS 6.8): its own gauge with its redline, the reading over its
/// sources, its bezel and unavailable state, and its own `newPeak_A`. An orb tracking all sources reads exactly the
/// snapshot's own values and keeps the engine's `newPeak`.
@MainActor
final class OrbRuntime {
    let id: String
    let feed: OrbFeed
    private(set) var config: OrbConfig
    private(set) var reading = SubsetReading()
    private var tracker = SubsetPeakTracker()
    private var trackerArmed = false
    private var drawOnTimer: Timer?
    private var lineQuiet = OrbLineQuietRule()

    init(config: OrbConfig) {
        id = config.id
        self.config = config
        feed = OrbFeed()
    }

    /// The orb's sources as a set for the engine; nil for all sources.
    var sourceSet: SourceSet? { config.sources.sourceSet }

    var isUnavailable: Bool { feed.unavailableTitleKey != nil }

    /// Applies an edited configuration; a new source set recomputes the peak and stays quiet for 60 s.
    func update(config: OrbConfig) {
        let sourcesChanged = config.sources != self.config.sources
        let readoutChanged = config.traffic != self.config.traffic
        self.config = config
        if sourcesChanged { trackerArmed = false }
        if readoutChanged {
            lineQuiet = OrbLineQuietRule()
            publishLines(nil)
        }
    }

    /// Feeds one snapshot (every engine tick). While the effects preview runs, every orb shows the demo.
    func ingest(_ snapshot: SpeedSnapshot, settings: AppSettings, cost: CostDisplay?, isPreviewing: Bool, now: TimeInterval) {
        let metric = settings.metric
        let set = isPreviewing ? nil : sourceSet
        let reading = OrbSubset.reading(snapshot, sources: set)
        self.reading = reading
        updateMoney(snapshot, reading: reading, cost: cost, isPreviewing: isPreviewing)
        let redline = redline(for: metric, settings: settings)
        if feed.gauge.redline != redline { feed.gauge.redline = redline }
        let output = config.redlineOutput.map { SettingsLimits.snapRedline($0, metric: .output) } ?? settings.redlineOutput
        if feed.redlineOutput != output { feed.redlineOutput = output }
        let unavailableKey: String? = switch reading.state {
        case .available: nil
        case .allDisabled: "source.disabled"
        case .allNotFound: "source.notFound"
        }
        if feed.unavailableTitleKey != unavailableKey { feed.unavailableTitleKey = unavailableKey }
        feed.gauge.ingest(unavailableKey == nil ? reading.live.value(for: metric) : 0, now: now)
        let today = metric == .output ? reading.today.output : reading.today.total
        feed.set(generating: reading.generatingSources, todayTokens: today, agentCount: reading.agents.count)
        updateBezel(unavailable: isPreviewing ? [] : reading.unavailable)
        trackPeak(snapshot: snapshot, set: set, isPreviewing: isPreviewing)
        if config.traffic == .intelligence {
            lineQuiet.apply(score: PerfSetReading(score: reading.intelligence.score, agents: reading.agents),
                            thinking: reading.intelligence.thinking, generating: !reading.generatingSources.isEmpty, at: now)
            publishLines(.intelligence)
        }
    }

    /// The network readout (DESIGN 10): its sources' rates with tools per the setting (`sources`), this computer
    /// (`machine`) or nothing; not drawn while measuring is off or, for sources, while per-process traffic is unavailable.
    /// Called once per traffic sample; the quiet rule keeps a quiet orb free of frames.
    func ingestTraffic(_ snapshot: TrafficSnapshot, settings: AppSettings, now: TimeInterval) {
        let mode = config.traffic
        guard mode != .intelligence else { return }
        let enabled = settings.trafficEnabled && snapshot.enabled && mode.isNetwork
            && (mode == .machine || snapshot.processState.unavailableReason == nil)
        let rate: TrafficRate? = switch mode {
        case _ where !enabled: nil
        case .machine: snapshot.machineLive ?? .zero
        default: snapshot.live(of: sourceSet, includeTools: settings.trafficIncludeTools) ?? .zero
        }
        let bits = settings.trafficUnit == .bits
        lineQuiet.apply(rate, bits: bits, at: now)
        publishLines(enabled ? .traffic(bits: bits) : nil)
    }

    /// The intelligence readout (DESIGN 10) follows every engine tick in `ingest`; it needs no traffic measuring.
    private func publishLines(_ format: OrbLineFormat?) {
        let lines = format.map { OrbLines(format: $0, reading: lineQuiet.reading) }
        if feed.lines != lines { feed.lines = lines }
    }

    /// The money readout of a cost orb (METRICS 6.8 Cost, DESIGN 9 Orb): its billable sources' live cost rate.
    /// The effects preview has no prices, so it shows the demo's output at a nominal $15 per million tokens.
    private func updateMoney(_ snapshot: SpeedSnapshot, reading: SubsetReading, cost: CostDisplay?, isPreviewing: Bool) {
        if feed.unit != config.unit { feed.unit = config.unit }
        guard config.unit == .cost, let cost else {
            if feed.money != nil { feed.money = nil }
            return
        }
        var costReading = OrbSubset.cost(snapshot, sources: isPreviewing ? nil : sourceSet, billable: cost.billable)
        if isPreviewing {
            costReading = CostReading(microUsdPerSecond: reading.live.output * Self.previewMicroUsdPerToken,
                                      liveOutput: reading.live.output)
        }
        let money = cost.live(costReading, agentCount: reading.agents.count)
        if feed.money != money { feed.money = money }
    }

    static let previewMicroUsdPerToken = 15.0

    /// The orb's redline of `metric`: its override, else the General redline.
    func redline(for metric: SpeedMetric, settings: AppSettings) -> Double {
        let custom = metric == .output ? config.redlineOutput : config.redlineTotal
        return custom.map { SettingsLimits.snapRedline($0, metric: metric) } ?? settings.redline(for: metric)
    }

    /// Delivers an engine event if it concerns this orb (METRICS 6.8, Events).
    func handle(_ event: SpeedEvent, isPreviewing: Bool) {
        guard OrbSubset.accepts(event, sources: isPreviewing ? nil : sourceSet) else { return }
        switch event {
        case .responseCompleted(let response): feed.emit(.response(output: response.outputTokens, speed: response.speed))
        case .newPeak: feed.emit(.newPeak)
        }
    }

    private func trackPeak(snapshot: SpeedSnapshot, set: SourceSet?, isPreviewing: Bool) {
        guard let set, !isPreviewing, case .running = snapshot.state else { return }
        let nowMs = Int64((snapshot.timestamp.timeIntervalSince1970 * 1000).rounded())
        if !trackerArmed {
            tracker.restart(nowMs: nowMs)
            trackerArmed = true
        }
        let dayStartMs = Int64((snapshot.dayStart.timeIntervalSince1970 * 1000).rounded())
        let fired = tracker.update(liveOutput: reading.live.output, measuredPeakOutput: snapshot.subsetPeaks[set],
                                   dayStartMs: dayStartMs, nowMs: nowMs)
        if fired { feed.emit(.newPeak) }
    }

    // MARK: Bezel

    private func updateBezel(unavailable: [SourceKind]) {
        guard !config.sources.isAll else {
            if feed.bezel != nil { feed.bezel = nil }
            return
        }
        let arcs = config.sources.knownSources.map { OrbBezel.Arc(color: $0.tint, available: !unavailable.contains($0)) }
        let drawOn = feed.bezel?.drawOn ?? 1
        let bezel = arcs.isEmpty ? nil : OrbBezel(arcs: arcs, drawOn: drawOn)
        if feed.bezel != bezel { feed.bezel = bezel }
    }

    /// The bezel draws on over 0.3 s after a split (DESIGN 2b, Split and merge motion).
    func drawBezelOn(delay: TimeInterval = 0, reduceMotion: Bool) {
        drawOnTimer?.invalidate()
        guard !reduceMotion, feed.bezel != nil else { return }
        feed.bezel?.drawOn = 0
        let start = Clock.now + delay
        let timer = Timer(timeInterval: 1.0 / 60, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                let progress = ((Clock.now - start) / 0.3).clamped(to: 0...1)
                self.feed.bezel?.drawOn = Easing.easeOut(progress)
                if progress >= 1 {
                    self.drawOnTimer?.invalidate()
                    self.drawOnTimer = nil
                }
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        drawOnTimer = timer
    }

    func stop() {
        drawOnTimer?.invalidate()
        drawOnTimer = nil
    }
}
