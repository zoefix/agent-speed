import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// What one board shows (DESIGN 2c Content; METRICS 6.8): its `BoardLive` state, its own tier and title trackers
/// (section 1 hysteresis and hold) with the General redline and title theme, its click-to-expand group, and the tick
/// its content and effects draw. `onTick` runs after every engine tick and after an expansion change.
@MainActor
final class BoardRuntime {
    let id: String
    private(set) var config: BoardConfig
    private(set) var live: BoardLive
    private(set) var tick: BoardTick?
    /// The frame behind `tick`, with the raw ranking events (before Celebrate new peaks folds new leaders).
    private(set) var frame = BoardLiveFrame()
    private(set) var expansion = BoardExpansion()
    let gauge = LiveGauge()
    var onTick: ((BoardRuntime) -> Void)?
    private var pendingEnginePeak = false
    /// 思考 hidden at zero (DESIGN 2c Live metrics).
    private(set) var thinkingFold = BoardThinkingFold()

    init(config: BoardConfig) {
        id = config.id
        self.config = config
        live = BoardLive(sources: config.sources, hosts: config.hosts)
    }

    var layoutMetrics: BoardLayoutMetrics { BoardLayoutMetrics(BoardGeometry(config.style, config.size, readouts: config.readouts)) }

    func update(config: BoardConfig) {
        // Switching 思考 on shows it.
        if config.readouts.thinking, !self.config.readouts.thinking { thinkingFold = BoardThinkingFold() }
        self.config = config
        live.setSources(config.sources)
        live.setHosts(config.hosts)
    }

    /// The engine's all-sources `newPeak`; an all-sources board celebrates it on its next tick.
    func engineReachedNewPeak() {
        pendingEnginePeak = true
    }

    /// Feeds one snapshot (every engine tick, and once more when a setting the tick depends on changes).
    func ingest(_ snapshot: SpeedSnapshot, model: AppModel, now: TimeInterval) {
        let previewing = model.store.isPreviewing
        expansion.expire(now: now, present: Set(frame.groups.map(\.id)))
        let metric = model.settings.metric
        frame = live.ingest(snapshot, metric: metric, layout: layoutMetrics, expanded: expansion.group,
                            engineNewPeak: pendingEnginePeak, previewing: previewing)
        pendingEnginePeak = false
        let redline = model.settings.redline(for: metric)
        if gauge.redline != redline { gauge.redline = redline }
        gauge.ingest(frame.value, now: now)
        let readouts = config.readouts
        let sums = frame.intelligence
        thinkingFold.update(eligible: readouts.thinkingMayHide, quiet: !(sums.output > 0),
                            shown: SpeedFormat.speed(sums.thinking), at: now)
        tick = makeTick(model: model, snapshot: snapshot, previewing: previewing)
        onTick?(self)
    }

    // MARK: Expansion

    /// A click on a group row toggles its expansion (DESIGN 2c Layout per size).
    func toggleExpansion(of group: String, model: AppModel, now: TimeInterval) {
        expansion.toggle(group, now: now)
        replan(model: model)
    }

    /// The pointer over the board keeps an expansion open.
    func touch(now: TimeInterval) {
        expansion.touch(now: now)
    }

    private func replan(model: AppModel) {
        frame = live.replan(layout: layoutMetrics, expanded: expansion.group)
        guard var tick else { return }
        tick.rows = frame.rows
        tick.events = []
        tick.newPeak = false
        self.tick = tick
        onTick?(self)
    }

    // MARK: Tick

    private func makeTick(model: AppModel, snapshot: SpeedSnapshot, previewing: Bool) -> BoardTick {
        let l10n = model.l10n
        let settings = model.settings
        let redline = settings.redline(for: frame.metric)
        let unavailableKey: String? = switch frame.state {
        case .available: nil
        case .allDisabled: "source.disabled"
        case .allNotFound: "source.notFound"
        case .hostsOffline: "remote.orb.offline"
        }
        let title = unavailableKey.map { l10n($0) }
            ?? model.titles.text(gauge.title, in: model.titles.theme(forOrb: nil), l10n: l10n)
        let celebrate = settings.celebratePeaks
        let events = celebrate ? frame.events : frame.events.map { event in
            var plain = event
            if plain.kind == .newLeader { plain.kind = .overtake }
            return plain
        }
        let cost = costReading(model: model, snapshot: snapshot, previewing: previewing)
        var tick = BoardTick(
            value: frame.value,
            valueText: unavailableKey == nil ? SpeedFormat.speed(frame.value) : "–",
            metric: frame.metric,
            redline: redline,
            heat: HeatPalette.heat(value: frame.value, redline: redline),
            overdrive: HeatPalette.overdrive(value: frame.value, redline: redline),
            title: title,
            generating: frame.generating,
            tools: frame.tools,
            history: frame.history,
            groups: frame.groups,
            rows: frame.rows,
            events: events,
            newPeak: frame.newPeak && celebrate,
            todayText: SpeedFormat.tokens(frame.todayOutput),
            costText: costText(cost, model: model),
            peakText: SpeedFormat.speed(frame.peak)
        )
        let readouts = config.readouts
        guard readouts.hero else { return tick }
        let format = model.cost.boardMoney
        if readouts.cost, cost?.state == .subscriptionOnly {
            tick.costText = format.tag
            tick.costIsTag = true
        }
        // The preview has no prices: its costs read `–`.
        let hero = cost.map(format.money) ?? BoardMoney(number: "–", unit: "", state: .unknown)
        tick.metrics = .make(readouts: readouts, intelligence: frame.intelligence, agents: frame.groups.flatMap(\.members),
                             cost: hero, costUnit: format.unit, thinkingHidden: thinkingFold.hidden)
        return tick
    }

    /// The board's billable cost (METRICS 9.4, 9.5); nil while the preview runs (it has no prices) or no source can
    /// show anything.
    private func costReading(model: AppModel, snapshot: SpeedSnapshot, previewing: Bool) -> CostReading? {
        guard !previewing, frame.state == .available else { return nil }
        return OrbSubset.cost(snapshot, sources: config.sources.sourceSet, hosts: config.hosts.hostSet,
                              billable: model.cost.billable)
    }

    /// Today's cost of the board's billable sources (DESIGN 9), as the dashboard writes it; nil when nothing is
    /// billable or there is no reading.
    private func costText(_ reading: CostReading?, model: AppModel) -> String? {
        guard let reading, reading.state != .subscriptionOnly else { return nil }
        let cost = model.cost
        guard reading.todayRequests > 0 else { return cost.amountText(microUsd: 0) }
        let text = cost.tallyText(reading.today, requests: reading.todayRequests)
        return text == "–" ? nil : text
    }
}
