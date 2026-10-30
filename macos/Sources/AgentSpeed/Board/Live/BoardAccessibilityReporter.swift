import Accessibility
import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// VoiceOver for one board (DESIGN 2c Accessibility): the board is a list labelled `a11y.board` (its sources, value
/// and title), each shown group row an item labelled `a11y.board.row` with its 性能 words, effort, speed and sub-agent
/// count. While `intelligence` is on, the sub rows are items too, and every row carries its score card as extra content
/// (VoiceOver's "more content") and the custom action `a11y.perf.card`, which shows the card (DESIGN 12). The row with
/// the keyboard focus is the focused element. Labels update at most once per second; a new leader is announced
/// politely at most once per 10 s.
@MainActor
final class BoardAccessibilityReporter {
    static let interval: TimeInterval = 1

    private weak var host: BoardHostView?
    private var publishedAt: TimeInterval = -.infinity
    private var announcer = BoardAnnouncer()
    private var rows: [BoardRowAccessibilityElement] = []
    /// The custom action `a11y.perf.card` on a row (its key), or a press: shows its card, or hides it when it shows.
    var onCardAction: ((String) -> Void)?
    /// An assistive app focused a row (its key): the keyboard focus follows.
    var onFocus: ((String) -> Void)?
    /// The row with the keyboard focus.
    private(set) var focusedKey: String?

    init(host: BoardHostView) {
        self.host = host
        host.setAccessibilityElement(true)
        host.setAccessibilityRole(.list)
    }

    /// After each tick: announces a new leader, then refreshes the labels when a second has passed (or `force`).
    func update(model: AppModel, runtime: BoardRuntime, layout: BoardContentLayout?, frameInScreen: CGRect,
                now: TimeInterval, force: Bool = false) {
        guard let host, let tick = runtime.tick else { return }
        let l10n = model.l10n
        if let leader = runtime.frame.events.first(where: { $0.kind == .newLeader }),
           let group = tick.groups.first(where: { $0.id == leader.id }), announcer.mayAnnounce(now: now) {
            NSAccessibility.post(element: host, notification: .announcementRequested, userInfo: [
                .announcement: l10n("a11y.board.newLeader", Self.project(of: group, l10n)),
                .priority: NSAccessibilityPriorityLevel.medium.rawValue,
            ])
        }
        guard force || now - publishedAt >= Self.interval else { return }
        publishedAt = now
        let sources = model.boards.sourcesText(of: runtime.config, l10n: l10n)
        let label = Self.join([l10n("a11y.board", sources, tick.valueText, tick.title)]
            + Self.metricWords(tick.metrics, currency: model.cost.currencyName, l10n: l10n), l10n)
        if host.accessibilityLabel() != label {
            host.setAccessibilityLabel(label)
            NSAccessibility.post(element: host, notification: .titleChanged)
        }
        host.setAccessibilityHelp(l10n("boards.title"))
        rows = rowElements(tick: tick, layout: layout, frameInScreen: frameInScreen, l10n: l10n, parent: host)
        host.setAccessibilityChildren(rows)
    }

    private func rowElements(tick: BoardTick, layout: BoardContentLayout?, frameInScreen: CGRect, l10n: Localizer,
                             parent: NSView) -> [BoardRowAccessibilityElement] {
        let intelligence = tick.metrics.readouts.intelligence
        return tick.rows.compactMap { row -> BoardRowAccessibilityElement? in
            let label: String, box: CGRect?, card: PerfCardModel?
            switch row.kind {
            case .group(let index) where index < tick.groups.count:
                let group = tick.groups[index]
                label = Self.rowLabel(group, rank: index + 1, l10n: l10n, intelligence: intelligence)
                box = layout?.groupRows[group.id]?.row
                card = intelligence ? BoardScoreCards.group(group, l10n: l10n) : nil
            case .sub(let index, let sub) where intelligence && index < tick.groups.count && sub < tick.groups[index].subs.count:
                let agent = tick.groups[index].subs[sub]
                label = Self.subRowLabel(agent, metric: tick.metric, l10n: l10n)
                box = layout?.subRows[agent.id]
                card = BoardScoreCards.sub(agent, metric: tick.metric, l10n: l10n)
            default:
                return nil
            }
            let element = BoardRowAccessibilityElement()
            element.key = row.key
            element.setAccessibilityRole(.staticText)
            element.setAccessibilityParent(parent)
            let frame = box ?? .zero
            element.setAccessibilityFrame(CGRect(x: frameInScreen.minX + frame.minX, y: frameInScreen.maxY - frame.maxY,
                                                 width: frame.width, height: frame.height))
            element.setAccessibilityLabel(label)
            if row.key == focusedKey { element.setAccessibilityFocused(true) }
            element.onFocus = { [weak self] key in self?.onFocus?(key) }
            guard let card else { return element }
            element.accessibilityCustomContent = Self.cardContent(card, l10n: l10n)
            let key = row.key
            element.onPress = { [weak self] in self?.onCardAction?(key) }
            element.setAccessibilityCustomActions([NSAccessibilityCustomAction(name: l10n("a11y.perf.card")) { [weak self] in
                MainActor.assumeIsolated { self?.onCardAction?(key) }
                return true
            }])
            return element
        }
    }

    /// The keyboard focus moved to `key` (nil: it left the board): that row becomes the focused element at once.
    func setFocus(_ key: String?) {
        guard key != focusedKey else { return }
        focusedKey = key
        for element in rows where element.isAccessibilityFocused() != (element.key == key) {
            element.setAccessibilityFocused(element.key == key)
        }
        if let element = rows.first(where: { $0.key == key }) {
            NSAccessibility.post(element: element, notification: .focusedUIElementChanged)
        }
    }

    /// The focused row's element, for the host's focused element.
    var focusedElement: Any? { rows.first { $0.key == focusedKey && focusedKey != nil } }

    /// A sub row in the group rows' style: `explore，子 agent，118 tok/s，性能 9，尚未驗證，推理強度 max`.
    static func subRowLabel(_ agent: AgentStatus, metric: SpeedMetric, l10n: Localizer) -> String {
        let name = agent.project.isEmpty ? l10n(agent.source.titleKey) : agent.project
        var parts = [name, l10n("agents.subagent"),
                     l10n("mac.settings.speedValue", SpeedFormat.speed(max(0, agent.live.value(for: metric))))]
        if let words = PerfScoreText.words(PerfSetReading(agent: agent), l10n) {
            parts.append(l10n("a11y.agent.intel", words))
        }
        let capsule = EffortCapsule(agent.current)
        if capsule.effort != nil || capsule.speed != nil { parts.append(capsule.accessibilityText(l10n)) }
        return join(parts, l10n)
    }

    /// The card's lines as a row's extra content, in the card's order (DESIGN 12 *Accessibility*).
    static func cardContent(_ model: PerfCardModel, l10n: Localizer) -> [AXCustomContent] {
        let title = l10n("a11y.perf.card")
        return PerfCardText.accessibilityLines(model, l10n).enumerated().map { index, line in
            let content = AXCustomContent(label: title, value: line)
            content.importance = index == 0 ? .high : .default
            return content
        }
    }

    /// `第 1 名，tihu，每秒 7.88k 個 token，性能 86，3 個 agent 中最高，推理強度 xhigh，加速 Fast，100 個子 agent`: with 性能 on,
    /// its score words right after the speed (nothing for none). Rows name no cost (the board's label carries 費用).
    static func rowLabel(_ group: BoardGroup, rank: Int, l10n: Localizer, intelligence: Bool = false) -> String {
        var parts = [l10n("a11y.board.row", rank, project(of: group, l10n), SpeedFormat.speed(group.live))]
        if intelligence, let words = PerfScoreText.words(group.intelligenceReading, l10n) {
            parts.append(l10n("a11y.agent.intel", words))
        }
        let capsule = EffortCapsule(group.root.current)
        if capsule.effort != nil || capsule.speed != nil { parts.append(capsule.accessibilityText(l10n)) }
        if !group.subs.isEmpty { parts.append(l10n("boards.subCount", group.subs.count)) }
        return join(parts, l10n)
    }

    /// The board label's live metrics words (DESIGN 2c Live metrics, *Accessibility*), in order: the intelligence form
    /// with the score words (with 思考 while it shows), or 思考 alone, `intel.none` in place of the 性能 words while it is
    /// none; then the cost.
    static func metricWords(_ metrics: BoardMetricsTick, currency: String, l10n: Localizer) -> [String] {
        let readouts = metrics.readouts
        var words: [String] = []
        if readouts.intelligence {
            // While 性能 is none, `intel.none` replaces only its words; a shown 思考 is still read.
            words.append(OrbAccessibilityReporter.intelligenceSentence(
                metrics.intelligence, thinking: metrics.showsThinking ? metrics.thinking : nil, l10n))
        } else if readouts.thinking {
            words.append(l10n("a11y.board.thinking", metrics.quiet ? "0" : metrics.thinking))
        }
        if readouts.cost {
            switch metrics.cost.state {
            case .value:
                let number = metrics.cost.atLeast ? l10n("a11y.orb.costAtLeast", metrics.cost.number) : metrics.cost.number
                words.append(l10n("a11y.board.cost", number, currency))
            case .unknown: words.append(l10n("a11y.board.costUnknown"))
            case .tag: words.append(l10n("cost.subscriptionOnly"))
            }
        }
        return words
    }

    static func join(_ parts: [String], _ l10n: Localizer) -> String {
        let separator = l10n.language == .ja ? "、" : l10n.language.usesLatinCase ? ", " : "，"
        return parts.joined(separator: separator)
    }

    static func project(of group: BoardGroup, _ l10n: Localizer) -> String {
        group.root.project.isEmpty ? l10n(group.root.source.titleKey) : group.root.project
    }
}

/// A row element that carries its score card's lines as VoiceOver's "more content"; a press shows the card (Full
/// Keyboard Access's Space) and an assistive app focusing it moves the board's keyboard focus there.
final class BoardRowAccessibilityElement: NSAccessibilityElement, AXCustomContentProvider {
    var accessibilityCustomContent: [AXCustomContent]! = []
    var key = ""
    var onPress: (() -> Void)?
    var onFocus: ((String) -> Void)?

    override func accessibilityPerformPress() -> Bool {
        guard let onPress else { return false }
        onPress()
        return true
    }

    override func setAccessibilityFocused(_ accessibilityFocused: Bool) {
        let changed = accessibilityFocused != isAccessibilityFocused()
        super.setAccessibilityFocused(accessibilityFocused)
        if accessibilityFocused, changed { onFocus?(key) }
    }
}
