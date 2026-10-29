import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// One board on the desktop: its window, fed by its runtime (DESIGN 2c). Lays its content out once per tick and only
/// while it can be seen (and on every frame of a 性能 count-up), plays the tick on the effects, and handles the pointer
/// — a click toggles a group's expansion; hovering a row lifts its tint and, after 0.35 s, shows its score card while
/// `intelligence` is on (group and sub rows, and the hero's 性能) or a group's text tooltip while it is off — the
/// keyboard (DESIGN 12 *Keyboard*: with keyboard navigation on, the score rows take focus with the system focus ring,
/// Space shows the focused row's card, a focus move keeps it on the focused row, Esc closes it) and accessibility.
@MainActor
final class BoardPresenter {
    /// What the pointer is over, for the score card.
    private enum Hover: Equatable {
        case row(String)
        case hero

        var key: String {
            switch self {
            case .row(let key): key
            case .hero: BoardPresenter.heroCardKey
            }
        }

        var row: String? {
            if case .row(let key) = self { return key }
            return nil
        }
    }

    nonisolated static let heroCardKey = "#hero"

    private struct Look: Equatable {
        var style: BoardStyle
        var size: BoardSize
        var dark: Bool
        var language: AppLanguage
    }

    let window: BoardWindowController
    private let model: AppModel
    private let runtime: BoardRuntime
    private let accessibility: BoardAccessibilityReporter
    private let tooltip = OrbChipPanel(interactive: false)
    private var look: Look
    private var builder: BoardContentBuilding
    private var layout: BoardContentLayout?
    /// The tick `layout` was made from: an equal tick (an idle board) lays out and applies nothing.
    private var layoutTick: BoardTick?
    private var layoutStale = true
    /// The row whose tint is lifted.
    private var hovered: String?
    private var hover: Hover?
    private var tooltipTimer: Timer?
    private var tooltipGroup: String?
    /// The score card's window and timing, made on the first hover that needs them.
    private var cards: PerfCardPresenter?
    /// The card's window, closed with the board (AppKit keeps a window until it is closed).
    private var cardPanel: PerfCardPanel?
    private lazy var climbFrames = BoardClimbFrames(window: window.panel) { [weak self] in self?.climbFrame() }
    /// The score rows' keyboard focus; drawn while the board is key.
    private var focus = BoardKeyboardFocus()
    /// The card the keyboard (Space, the row's action) opened: it closes when the board loses the keyboard.
    private var keyboardCard: String?
    private var highContrast = NSWorkspace.shared.accessibilityDisplayShouldIncreaseContrast
    /// Lays the board out again when a sub-gauge cell narrows back with no new tick.
    private var wakeTimer: Timer?
    /// A live metrics switch change crossfades the hero (0.2 s, DESIGN 2c Live metrics, *Switches*).
    static let switchCrossfade = 0.2

    var id: String { runtime.id }
    var config: BoardConfig { window.config }

    init(model: AppModel, runtime: BoardRuntime, center: CGPoint, dark: Bool) {
        self.model = model
        self.runtime = runtime
        look = Look(style: runtime.config.style, size: runtime.config.size, dark: dark, language: model.l10n.language)
        builder = BoardContentBuilders.make(style: look.style, size: look.size, dark: dark, l10n: model.l10n, capsule: nil)
        window = BoardWindowController(config: runtime.config, center: center, dark: dark)
        accessibility = BoardAccessibilityReporter(host: window.host)
        accessibility.onCardAction = { [weak self] key in self?.toggleCard(key) }
        accessibility.onFocus = { [weak self] key in self?.focusFromAccessibility(key) }
        window.host.onKeyDown = { [weak self] event in self?.keyDown(event) ?? false }
        window.panel.onKeyChange = { [weak self] isKey in self?.keyChanged(isKey) }
        window.contentProvider = { [unowned self] in items() }
        window.onClick = { [weak self] point in self?.click(at: point) }
        window.host.onMouseMoved = { [weak self] event in self?.pointerMoved(event) }
        window.host.onMouseExited = { [weak self] _ in self?.pointerLeft() }
        let down = window.host.onMouseDown
        window.host.onMouseDown = { [weak self] event in
            self?.hideTooltip()
            self?.cards?.dismiss()
            down?(event)
        }
        runtime.onTick = { [weak self] _ in self?.tickArrived() }
        if runtime.tick != nil { tickArrived() }
    }

    func close() {
        runtime.onTick = nil
        tooltipTimer?.invalidate()
        wakeTimer?.invalidate()
        climbFrames.stop()
        cards?.dismiss()
        cardPanel?.close()
        tooltip.close()
        window.close()
    }

    // MARK: Settings

    /// A changed configuration: the window applies it, a new style or size takes a new content builder.
    func apply(_ config: BoardConfig) {
        guard config != window.config else { return }
        window.apply(config)
        setLook(style: config.style, size: config.size, dark: look.dark, language: look.language)
    }

    func setLook(style: BoardStyle? = nil, size: BoardSize? = nil, dark: Bool, language: AppLanguage) {
        let next = Look(style: style ?? look.style, size: size ?? look.size, dark: dark, language: language)
        guard next != look else { return }
        look = next
        window.dark = dark
        builder = BoardContentBuilders.make(style: next.style, size: next.size, dark: dark, l10n: model.l10n, capsule: nil)
        builder.reduceMotion = window.reduceMotion
        climbFrames.stop()
        layoutStale = true
        layoutTick = nil
        window.contentChanged(animated: false)
        publishAccessibility(force: true)
    }

    func setEffects(_ intensity: BoardEffectsIntensity, celebrations: Bool, reduceMotion: Bool, highContrast: Bool) {
        if window.reduceMotion != reduceMotion { window.reduceMotion = reduceMotion }
        builder.reduceMotion = reduceMotion
        if self.highContrast != highContrast {
            self.highContrast = highContrast
            if window.panel.isKeyWindow, focus.key != nil { window.contentChanged(animated: false) }
        }
        window.driver.highContrast = highContrast
        window.host.content.ambience = intensity != .off
        window.host.content.celebrations = celebrations
        window.driver.update { fx in
            if fx.intensity != intensity { fx.setIntensity(intensity) }
            if fx.celebrations != celebrations { fx.celebrations = celebrations }
        }
    }

    // MARK: Ticks

    private func items() -> [BoardContentItem] {
        guard let tick = runtime.tick else { return [] }
        if layoutStale || layout == nil {
            layout = builder.layout(tick)
            layoutTick = tick
            layoutStale = false
        }
        guard var items = layout?.takeItems() else { return [] }
        if let hovered, let index = items.firstIndex(where: { $0.key == hovered }) {
            items[index].plate = Self.lifted(items[index].plate, dark: look.dark)
        }
        // The focus ring shows only while the board has the keyboard, as in any window, over every piece of its row.
        if window.panel.isKeyWindow, let focused = focus.key, let row = items.first(where: { $0.key == focused }) {
            items.append(BoardFocusRing.item(around: row, style: look.style, dark: look.dark, highContrast: highContrast))
        }
        return items
    }

    private func tickArrived() {
        guard let tick = runtime.tick else { return }
        updateFocus(tick)
        if layoutStale || tick != layoutTick {
            let switched = layoutTick.map { $0.metrics.readouts != tick.metrics.readouts } ?? false
            layoutStale = true
            window.contentChanged(crossfade: switched ? Self.switchCrossfade : nil)
            scheduleWake()
            scheduleClimb()
        }
        let laidOut = window.isRendering && !layoutStale ? layout : nil
        let punch = runtime.frame.punch
        window.driver.update { fx in BoardEffectsPlayer.play(tick, layout: laidOut, punch: punch, on: &fx) }
        publishAccessibility()
        if let tooltipGroup, tooltip.isShown { showTooltip(for: tooltipGroup) }
        refreshCard(tick)
    }

    /// A count-up of the hero's 性能 lays the board out on every frame until it lands (only while it can be seen).
    private func scheduleClimb() {
        guard window.isRendering, !layoutStale, let until = builder.climbsUntil(after: Clock.now) else { return }
        climbFrames.run(until: until)
    }

    private func climbFrame() {
        guard window.isRendering else { return climbFrames.stop() }
        layoutStale = true
        window.contentChanged(animated: false)
    }

    /// A fixed cell of the sub-gauge narrows 6 s after the last value that needed it, even if no tick changes.
    private func scheduleWake() {
        wakeTimer?.invalidate()
        wakeTimer = nil
        guard let at = builder.wakeAt, at > Clock.now else { return }
        let timer = Timer(timeInterval: max(0.05, at - Clock.now + 0.01), repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.layoutStale = true
                self.window.contentChanged()
                self.scheduleWake()
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        wakeTimer = timer
    }

    private func publishAccessibility(force: Bool = false) {
        accessibility.update(model: model, runtime: runtime, layout: layoutStale ? nil : layout,
                             frameInScreen: window.panel.frame, now: Clock.now, force: force)
    }

    // MARK: Pointer

    private func group(at point: CGPoint) -> String? {
        guard let layout else { return nil }
        return layout.groupRows.first { $0.value.row.contains(point) }?.key
    }

    /// The card's anchor under `point`: a group or sub row, or the hero's 性能, while `intelligence` is on; only a group
    /// row (its text tooltip) while it is off.
    private func target(at point: CGPoint) -> Hover? {
        if let id = group(at: point) { return .row(id) }
        guard usesCards, let layout else { return nil }
        if let id = layout.subRows.first(where: { $0.value.contains(point) })?.key { return .row(id) }
        if let area = layout.scoreArea, area.contains(point) { return .hero }
        return nil
    }

    private var usesCards: Bool { runtime.tick?.metrics.readouts.intelligence == true }

    private func boardPoint(_ event: NSEvent) -> CGPoint {
        let local = window.host.convert(event.locationInWindow, from: nil)
        return CGPoint(x: local.x, y: window.side - local.y)
    }

    private func click(at point: CGPoint) {
        // A click on a row of a board that has the keyboard moves the focus there; it never brings the keyboard.
        if window.panel.isKeyWindow, case .row(let key) = target(at: point), focus.focus(key) { focusMoved(key) }
        guard let id = group(at: point) else { return }
        runtime.toggleExpansion(of: id, model: model, now: Clock.now)
    }

    private func pointerMoved(_ event: NSEvent) {
        runtime.touch(now: Clock.now)
        setHover(target(at: boardPoint(event)))
    }

    private func pointerLeft() {
        setHover(nil)
    }

    private func setHover(_ next: Hover?) {
        guard next != hover else { return }
        let previous = hover
        hover = next
        if next?.row != hovered {
            hovered = next?.row
            window.contentChanged(animated: false)
        }
        hideTooltip()
        if usesCards {
            if let previous { cards?.exit(previous.key) }
            guard let next else { return }
            cardPresenter().enter(next.key, provider: provider(for: next))
            return
        }
        cards?.dismiss()
        guard let id = next?.row else { return }
        let timer = Timer(timeInterval: BoardTooltip.delay, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.showTooltip(for: id) }
        }
        RunLoop.main.add(timer, forMode: .common)
        tooltipTimer = timer
    }

    private func showTooltip(for id: String) {
        guard let tick = runtime.tick, let group = tick.groups.first(where: { $0.id == id }),
              let row = layout?.groupRows[id]?.row, window.panel.isVisible else {
            hideTooltip()
            return
        }
        tooltipGroup = id
        let lines = BoardTooltip.lines(group, metric: tick.metric, l10n: model.l10n)
        let board = window.panel.frame
        let screen = (window.panel.screen ?? NSScreen.main)?.visibleFrame ?? board
        tooltip.show(BoardTooltipView(lines: lines), level: NSWindow.Level(rawValue: window.panel.level.rawValue + 1),
                     appearance: NSAppearance(named: look.dark ? .darkAqua : .aqua)) { size in
            let rowTop = board.maxY - row.minY
            var x = board.maxX + 4
            if x + size.width > screen.maxX { x = board.minX - 4 - size.width }
            let y = min(max(rowTop - size.height + 8, screen.minY), screen.maxY - size.height)
            return CGRect(x: max(screen.minX, x), y: y, width: size.width, height: size.height)
        }
    }

    private func hideTooltip() {
        tooltipTimer?.invalidate()
        tooltipTimer = nil
        tooltipGroup = nil
        tooltip.hide()
    }

    // MARK: Score card

    private func cardPresenter() -> PerfCardPresenter {
        if let cards { return cards }
        let accessibility = model.accessibility
        let panel = PerfCardPanel(l10n: model.l10n)
        cardPanel = panel
        let made = PerfCardPresenter(display: panel, delay: PerfCardPresenter.boardDelay) {
            (accessibility.reduceMotion, accessibility.reduceTransparency)
        }
        cards = made
        return made
    }

    private func provider(for hover: Hover) -> PerfCardPresenter.Provider {
        { [weak self] in self?.cardRequest(hover) }
    }

    /// The card of `hover` beside the board on the side with more room, level with its row (the hero's: with its
    /// 性能); nil when there is nothing to show (性能 off, the row gone, the hero's value none, the board hidden).
    private func cardRequest(_ hover: Hover) -> PerfCardRequest? {
        guard let tick = runtime.tick, tick.metrics.readouts.intelligence, let layout, window.panel.isVisible else { return nil }
        let l10n = model.l10n
        let model: PerfCardModel?
        let rect: CGRect?
        switch hover {
        case .hero:
            model = BoardScoreCards.hero(tick, l10n: l10n)
            rect = layout.scoreArea
        case .row(let key):
            model = BoardScoreCards.row(key, in: tick, l10n: l10n)
            rect = layout.groupRows[key]?.row ?? layout.subRows[key]
        }
        guard let model, let rect else { return nil }
        let board = window.panel.frame
        let row = CGRect(x: board.minX + rect.minX, y: board.maxY - rect.maxY, width: rect.width, height: rect.height)
        let visible = (window.panel.screen ?? NSScreen.main)?.visibleFrame ?? board
        return PerfCardRequest(model: model, anchor: PerfCardAnchor(row: row, container: board, visibleFrame: visible,
                                                                              side: .moreRoom),
                                    window: window.panel)
    }

    /// The engine tick: the shown card takes its row's new content, and leaves when its row or value is gone.
    private func refreshCard(_ tick: BoardTick) {
        guard let cards, let key = cards.shownKey else { return }
        let hover: Hover = key == Self.heroCardKey ? .hero : .row(key)
        guard cardRequest(hover) != nil else { return cards.dismiss() }
        cards.refresh()
    }

    /// The row's custom action `a11y.perf.card`: shows its card at once, or hides it when it shows (a board never takes
    /// the keyboard focus, so Esc cannot reach it).
    private func toggleCard(_ key: String) {
        let cards = cardPresenter()
        if cards.shownKey == key {
            keyboardCard = nil
            return cards.dismiss()
        }
        keyboardCard = key
        cards.showNow(key, provider: provider(for: .row(key)))
    }

    // MARK: Keyboard

    /// The rows that take focus for this tick (keyboard navigation is read again on every tick: it has no notification).
    private func updateFocus(_ tick: BoardTick) {
        let before = focus.key
        focus.update(BoardKeyboardFocus.keys(tick.rows, intelligence: tick.metrics.readouts.intelligence,
                                             keyboardNavigation: NSApplication.shared.isFullKeyboardAccessEnabled))
        window.keyboardFocusable = focus.isEnabled
        guard focus.key != before else { return }
        if focus.key == nil, let keyboardCard, cards?.shownKey == keyboardCard { closeKeyboardCard() }
        guard window.panel.isKeyWindow else { return }
        accessibility.setFocus(focus.key)
        if let key = focus.key { cards?.focus(key, provider: provider(for: .row(key))) }
        // The ring moves even when the tick lays nothing out again (keyboard navigation switched off on an idle board).
        if !layoutStale, runtime.tick == layoutTick { window.contentChanged(animated: false) }
    }

    private func keyChanged(_ isKey: Bool) {
        if isKey {
            window.panel.makeFirstResponder(window.host)
            accessibility.setFocus(focus.enter())
        } else {
            accessibility.setFocus(nil)
            if let keyboardCard, cards?.shownKey == keyboardCard { closeKeyboardCard() }
        }
        window.contentChanged(animated: false)
    }

    private func keyDown(_ event: NSEvent) -> Bool {
        // Shortcuts with ⌘, ⌥ or ⌃ belong to the system (⌘` cycles the windows).
        guard event.modifierFlags.intersection([.command, .option, .control]).isEmpty,
              let pressed = BoardKey(keyCode: event.keyCode, shift: event.modifierFlags.contains(.shift)) else { return false }
        let before = focus.key
        guard let effect = focus.handle(pressed, cardShown: cards?.isShown == true) else { return false }
        switch effect {
        case .moved(let key):
            focusMoved(key)
        case .showCard(let key):
            if key != before { focusMoved(key) }
            keyboardCard = key
            cardPresenter().showNow(key, provider: provider(for: .row(key)))
        case .closeCard:
            closeKeyboardCard()
        case .none:
            break
        }
        return true
    }

    /// The focus moved: the ring follows (one small piece is drawn), VoiceOver follows, an open card follows.
    private func focusMoved(_ key: String) {
        window.contentChanged(animated: false)
        accessibility.setFocus(key)
        guard let cards, cards.isShown else { return }
        cards.focus(key, provider: provider(for: .row(key)))
        if cards.shownKey == key, keyboardCard != nil { keyboardCard = key }
    }

    /// Full Keyboard Access or VoiceOver focused a row: with keyboard navigation on, the board takes the keyboard (as a
    /// non-activating panel, without activating the app) and the focus moves there.
    private func focusFromAccessibility(_ key: String) {
        guard focus.key != key, focus.focus(key) else { return }
        if !window.panel.isKeyWindow { window.panel.makeKey() }
        focusMoved(key)
    }

    private func closeKeyboardCard() {
        keyboardCard = nil
        cards?.dismiss()
    }

    /// Hover lifts the row's tint only (DESIGN 2c Interaction).
    static func lifted(_ plate: BoardContentItem.Plate?, dark: Bool) -> BoardContentItem.Plate {
        let lift = NSColor(white: dark ? 1 : 0, alpha: dark ? 0.07 : 0.045)
        guard let plate, let base = NSColor(cgColor: plate.color)?.usingColorSpace(.sRGB) else {
            return BoardContentItem.Plate(color: lift.cgColor, cornerRadius: 8)
        }
        let blended = base.blended(withFraction: 0.1, of: dark ? .white : .black) ?? base
        let alpha = min(1, base.alphaComponent + lift.alphaComponent)
        return BoardContentItem.Plate(color: blended.withAlphaComponent(alpha).cgColor, cornerRadius: plate.cornerRadius)
    }
}
