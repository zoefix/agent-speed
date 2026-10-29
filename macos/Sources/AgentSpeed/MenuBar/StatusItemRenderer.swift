import AgentSpeedCore
import AppKit

/// Keeps the status item image and length current. Draws on change and runs at most 10 frames per second, only
/// while the ring animates (springs, glow fade, redline flicker); otherwise it is idle and costs nothing.
@MainActor
final class StatusItemRenderer {
    static let frameInterval: TimeInterval = 1.0 / 10

    private let model: AppModel
    private let item: NSStatusItem
    private var widthGovernor = StatusItemWidthGovernor()
    private var secondaryGovernor = StatusItemWidthGovernor()
    private var intelligenceGovernor = StatusItemWidthGovernor()
    private var shrinkTimer: Timer?
    private var scheduledShrink: TimeInterval?
    private var loop: ObservationLoop?
    private var timer: Timer?
    private var lastState: StatusItemState?
    private var lastImageAt: TimeInterval = -.infinity

    init(model: AppModel, item: NSStatusItem) {
        self.model = model
        self.item = item
        loop = ObservationLoop { [weak self] in
            guard let self else { return }
            let gauge = self.model.gauge
            let settings = self.model.settings
            _ = (gauge.revision, gauge.tier, gauge.readoutValue)
            _ = (settings.barMode, settings.barShowsUnit, settings.barColoredGlyph, settings.effects)
            _ = (self.model.l10n.language, self.model.accessibility.increaseContrast, self.model.accessibility.reduceMotion)
            _ = settings.barSecondary
            if settings.liveUnit == .cost || settings.barSecondary {
                // The money value follows the snapshot, the currency, the rates and the marks (DESIGN 9).
                _ = (self.model.snapshot.sources, self.model.cost.currency, self.model.cost.rate, settings.billableSources)
            }
            _ = settings.liveUnit
            _ = (settings.trafficEnabled, settings.barTraffic, settings.trafficUnit, settings.barIntelligence)
            if settings.trafficEnabled, settings.barTraffic.isNetwork {
                let traffic = self.model.visibleTraffic
                _ = (traffic.machineLive, traffic.sources, settings.trafficIncludeTools)
            }
            // The intelligence block follows every engine tick (the score, its supplying agent's provisional mark) and
            // the end of the thinking hold (DESIGN 10, 12).
            if settings.barIntelligence {
                _ = (self.model.snapshot.intelligence, self.model.snapshot.agents, self.model.barThinking.hidden)
            }
            self.render()
        }
    }

    func stop() {
        loop?.cancel()
        timer?.invalidate()
        timer = nil
        shrinkTimer?.invalidate()
        shrinkTimer = nil
    }

    /// The image resolves the menu bar appearance itself each time it is drawn, so appearance changes need no
    /// new image (observing them would loop: the system flips the button's appearance while snapshotting).
    private func render() {
        let now = Clock.now
        // Each image change makes the system re-snapshot the item on every display, so cap the rate.
        if now - lastImageAt < Self.frameInterval * 0.9 {
            ensureTimer()
            return
        }
        let state = state(at: now)
        if state != lastState {
            lastState = state
            lastImageAt = now
            if item.length != state.layout.length { item.length = state.layout.length }
            item.button?.image = StatusItemArtist.image(for: state)
        }
        scheduleShrink()
        scheduleFrames(now: now)
    }

    private func scheduleFrames(now: TimeInterval) {
        guard StatusItemState.needsFrames(model, at: now) || state(at: now) != lastState else {
            timer?.invalidate()
            timer = nil
            return
        }
        ensureTimer()
    }

    /// The current state, with the value's room decided by the width governor.
    private func state(at time: TimeInterval) -> StatusItemState {
        let needed = StatusItemLayout.valueWidth(of: StatusItemValue.current(model).text)
        _ = widthGovernor.update(needed: needed, now: time)
        _ = secondaryGovernor.update(needed: StatusItemSecondary(model: model)?.numberWidth ?? 0, now: time)
        _ = intelligenceGovernor.update(needed: StatusItemIntelligence(model: model)?.cell ?? 0, now: time)
        return StatusItemState(model: model, time: time, valueWidth: widthGovernor.width,
                               secondaryWidth: secondaryGovernor.width, intelligenceWidth: intelligenceGovernor.width)
    }

    private func scheduleShrink() {
        let deadline = [widthGovernor.shrinkDeadline, secondaryGovernor.shrinkDeadline, intelligenceGovernor.shrinkDeadline]
            .compactMap { $0 }.min()
        guard deadline != scheduledShrink else { return }
        scheduledShrink = deadline
        shrinkTimer?.invalidate()
        shrinkTimer = nil
        guard let deadline else { return }
        let timer = Timer(timeInterval: max(0.05, deadline - Clock.now + 0.02), repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.render() }
        }
        RunLoop.main.add(timer, forMode: .common)
        shrinkTimer = timer
    }

    private func ensureTimer() {
        guard timer == nil else { return }
        let timer = Timer(timeInterval: Self.frameInterval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.render() }
        }
        timer.tolerance = 0.01
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }
}
