import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// Keeps one orb's accessibility label, value and hint current, publishing at most once per second (DESIGN 2b,
/// Accessibility and keyboard). A single orb tracking all sources reads exactly as before: the app name, today's
/// `a11y.orb` value and the platform hint. With several orbs (or a subset) the label is `a11y.orb.named` (or
/// `a11y.orb.unavailable`) and the hint names the sources.
@MainActor
final class OrbAccessibilityReporter {
    static let interval: TimeInterval = 1

    private let model: AppModel
    private let runtime: OrbRuntime
    private weak var host: OrbHostView?
    private var loop: ObservationLoop?
    private var timer: Timer?
    /// Publishes again when the thinking line's hold ends, which changes nothing the loop observes.
    private var holdTimer: Timer?
    private var holdEnd: TimeInterval?
    private var publishedAt: TimeInterval = -.infinity
    /// The unit last published; a change is announced once (DESIGN 9, Accessibility).
    private var publishedUnit: OrbUnit?

    init(model: AppModel, runtime: OrbRuntime, host: OrbHostView) {
        self.model = model
        self.runtime = runtime
        self.host = host
        loop = ObservationLoop { [weak self] in self?.observe() }
    }

    func cancel() {
        loop?.cancel()
        timer?.invalidate()
        timer = nil
        holdTimer?.invalidate()
        holdTimer = nil
        holdEnd = nil
    }

    private func observe() {
        let gauge = runtime.feed.gauge
        _ = (gauge.readoutValue, gauge.title, runtime.feed.unavailableTitleKey, model.l10n.language)
        _ = (runtime.feed.money, runtime.feed.unit, runtime.feed.lines)
        _ = model.orbs.document
        _ = model.titles.document
        let elapsed = Clock.now - publishedAt
        if elapsed >= Self.interval {
            publish()
            return
        }
        guard timer == nil else { return }
        let timer = Timer(timeInterval: Self.interval - elapsed, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.timer = nil
                self.publish()
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    private func publish() {
        guard let host else { return }
        publishedAt = Clock.now
        let l10n = model.l10n
        let gauge = runtime.feed.gauge
        let orbs = model.orbs
        let speed = SpeedFormat.speed(gauge.readoutValue)
        let title = model.titles.text(gauge.title, in: model.titles.theme(forOrb: runtime.config), l10n: l10n)
        let single = orbs.orbs.count == 1 && runtime.config.sources.isAll
        var label: String
        var value: String
        let help: String
        // The money unit reads the amount with the currency's name (DESIGN 9, Accessibility).
        let money = runtime.feed.money
        let amount = money.map { $0.atLeast ? l10n("a11y.orb.costAtLeast", $0.number) : $0.number }
        let currency = model.cost.currencyName
        if single {
            label = l10n("app.name")
            if let money, money.unknown {
                value = l10n("a11y.orb.costUnknown", title)
            } else if let amount {
                value = l10n("a11y.orb.cost", amount, currency, title)
            } else {
                value = l10n("a11y.orb", speed, title)
            }
            help = l10n("mac.a11y.orbHint")
        } else {
            let name = orbs.displayName(of: runtime.id)
            if runtime.isUnavailable {
                label = l10n("a11y.orb.unavailable", name)
                value = ""
            } else if let money, money.unknown {
                label = name
                value = l10n("a11y.orb.costUnknown", title)
            } else if let amount {
                label = l10n("a11y.orb.costNamed", name, amount, currency, title)
                value = ""
            } else {
                label = l10n("a11y.orb.named", name, speed, title)
                value = ""
            }
            let names = runtime.config.sources.isAll
                ? l10n("orbs.sources.all")
                : runtime.config.sources.knownSources.map { l10n($0.titleKey) }.joined(separator: l10n("list.separator"))
            help = l10n("a11y.orb.sources", names)
        }
        if let lines = lineSentence(l10n) {
            let separator = l10n.language == .ja ? "、" : l10n.language.usesLatinCase ? ", " : "，"
            if single { value += separator + lines } else { label += separator + lines }
        }
        host.accessibilityHelpText = help
        scheduleHoldEnd()
        let labelChanged = label != host.accessibilityLabelText
        let valueChanged = value != host.accessibilityValueText
        host.accessibilityLabelText = label
        host.accessibilityValueText = value
        if valueChanged { NSAccessibility.post(element: host, notification: .valueChanged) }
        if labelChanged, !single { NSAccessibility.post(element: host, notification: .titleChanged) }
        let unit = runtime.feed.unit
        if let publishedUnit, publishedUnit != unit {
            let name = unit == .cost ? model.cost.unitPerSecond : l10n("unit.tokS")
            NSAccessibility.post(element: host, notification: .announcementRequested, userInfo: [
                .announcement: l10n("a11y.unitChanged", name),
                .priority: NSAccessibilityPriorityLevel.high.rawValue,
            ])
        }
        publishedUnit = unit
    }

    private func scheduleHoldEnd() {
        let now = Clock.now
        let end = runtime.feed.lines?.reading?.thinking.holdEnd(after: now)
        guard end != holdEnd else { return }
        holdTimer?.invalidate()
        holdTimer = nil
        holdEnd = end
        guard let end else { return }
        let timer = Timer(timeInterval: end - now + 0.05, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.holdTimer = nil
                self.holdEnd = nil
                self.observe()
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        holdTimer = timer
    }

    /// While the readout is visible (DESIGN 10, Accessibility): `a11y.orb.traffic` with rates read as words and units as
    /// written, or `a11y.orb.intel` with the score words (`a11y.orb.intel.only` while the thinking line is hidden).
    private func lineSentence(_ l10n: Localizer) -> String? {
        let config = runtime.config
        guard let lines = runtime.feed.lines, let reading = lines.reading, !runtime.isUnavailable else { return nil }
        let now = Clock.now
        guard now >= reading.shownAt, !reading.isGone(at: now) || now < reading.quietAt,
              OrbLineMetrics.fits(.of(config.style), diameter: CGFloat(config.size), format: lines.format,
                                  language: l10n.language) else { return nil }
        switch reading.content {
        case .traffic(let rx, let tx):
            return l10n("a11y.orb.traffic", TrafficText.spoken(rx), TrafficText.spoken(tx))
        case .intelligence(let score, let thinking):
            return Self.intelligenceSentence(score, thinking: reading.thinking.isHidden(at: now) ? nil : thinking, l10n)
        }
    }

    /// The performance readout's words (DESIGN 12, *Accessibility*): the score words in `a11y.orb.intel` with the
    /// thinking rate, or in `a11y.orb.intel.only` without it (`thinking` nil); while the value is none `intel.none`
    /// replaces the 性能 words and the thinking words stay.
    static func intelligenceSentence(_ value: PerfSetReading, thinking: String?, _ l10n: Localizer) -> String {
        guard let words = PerfScoreText.words(value, l10n) else {
            guard let thinking else { return l10n("intel.none") }
            let marker = "\u{E000}"
            let full = l10n("a11y.orb.intel", marker, thinking)
            let intelligence = l10n("a11y.orb.intel.only", marker)
            guard let range = full.range(of: intelligence) else { return l10n("intel.none") }
            return full.replacingCharacters(in: range, with: l10n("intel.none"))
        }
        return thinking.map { l10n("a11y.orb.intel", words, $0) } ?? l10n("a11y.orb.intel.only", words)
    }
}
