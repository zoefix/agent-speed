import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// What a floating orb window needs from the window manager: the other orbs (magnetic snapping, placement), persistence
/// and the shared overlays.
@MainActor
protocol OrbWindowManaging: AnyObject {
    /// The other shown orbs on `screen`.
    func otherDiscs(excluding id: String, on screen: NSScreen) -> [OrbDisc]
    /// The shown leaderboard boards on `screen` (DESIGN 2c: boards and orbs never overlap).
    func boardShapes(on screen: NSScreen) -> [FloatingShape]
    /// The orb came to rest at `center` by the user's (or a programmatic) placement; store it for this arrangement.
    func orbSettled(_ id: String, center: CGPoint, radius: CGFloat, on screen: NSScreen)
    /// Pointer down, drag or menu on an orb: bring it to the front and keep its frame rate in a crowd.
    func orbInteracted(_ id: String)
    func orbHoverChanged(_ id: String, hovered: Bool)
    func showGuides(_ guides: [OrbGuide], on screen: NSScreen?)
    func orbMoved(_ id: String)
    /// An orb came to rest, or its plate's content changed: resolve the name plates' placement.
    func nameplatesNeedLayout()
}

/// One floating orb window (DESIGN 2, 2b): placement, drag with edge snapping and orb-to-orb magnetism, click-through
/// outside the disc, visibility and idle fading, its own style, size, opacity and window options from its
/// configuration, its source chip, accessibility and context menu.
@MainActor
final class OrbWindowController {
    private static let hoverScale: CGFloat = 1.03
    /// The source chip appears after this much hover (DESIGN 2b).
    private static let chipHoverDelay: TimeInterval = 0.35

    let id: String
    let runtime: OrbRuntime
    let host: OrbHostView
    let presenter: OrbPresenter
    private(set) var config: OrbConfig
    private let model: AppModel
    private weak var manager: OrbWindowManaging?
    private let interaction: OrbInteraction
    private let panel: OrbPanel
    private let animator: OrbWindowAnimator
    private let pointer = OrbWindowPointerTracker()
    private let fader: OrbWindowFader
    private let visibility: OrbWindowVisibilityMonitor
    private var dimmer: OrbWindowIdleDimmer?
    private var reporter: OrbAccessibilityReporter?
    private var loops: [ObservationLoop] = []
    private let chipPanel = OrbChipPanel(interactive: false)
    /// The name plate (DESIGN 2b, Name plate); while it shows, the source chip folds into it.
    private(set) lazy var nameplate = OrbNameplateController(parent: panel, delegate: self)
    private var chipTimer: Timer?
    private var chipUntil: TimeInterval = -.infinity
    private var hoverChipTimer: Timer?

    /// The click on the unit line (DESIGN 9) and its hover affordance.
    private let unitToggle = OrbUnitToggle()
    private var unitHovered = false
    private var unitHoverTimer: Timer?

    private var drag: OrbDragSession?
    private var dragInterruptedMotion = false
    private var dragRegion: [CGRect] = []
    private var cursorPushed = false
    private var appliedDiameter: CGFloat
    private var wantsShown = false
    private var isClosing = false
    /// Waiting for a staggered entrance to start (the disc still sits at its origin).
    private var isEntering = false

    init(model: AppModel, runtime: OrbRuntime, manager: OrbWindowManaging) {
        self.model = model
        self.runtime = runtime
        self.manager = manager
        id = runtime.id
        config = runtime.config
        let diameter = CGFloat(runtime.config.footprintDiameter)
        let interaction = OrbInteraction()
        let panel = OrbPanel()
        self.interaction = interaction
        self.panel = panel
        appliedDiameter = diameter
        let host = OrbHostView(diameter: diameter)
        self.host = host
        presenter = OrbPresenter(model: model, feed: runtime.feed, surface: .floating, style: runtime.config.style, diameter: diameter,
                                 dark: host.isDark, interaction: interaction, canvas: host.canvas)
        let orbID = runtime.id
        presenter.titleTheme = { [weak model] in
            guard let model else { return .preset(.vehicles) }
            return model.titles.theme(forOrb: model.orbs.orb(orbID))
        }
        animator = OrbWindowAnimator(center: .zero, diameter: diameter)
        fader = OrbWindowFader(panel: panel)
        visibility = OrbWindowVisibilityMonitor(window: panel)

        panel.contentView = host
        panel.ignoresMouseEvents = true
        host.delegate = self
        host.onAppearanceChange = { [weak self] dark in
            self?.presenter.dark = dark
            self?.refreshBody()
            self?.refreshNameplate()
        }
        animator.attach(to: host)
        animator.onChange = { [weak self] state in self?.apply(state) }
        animator.onCenterSettled = { [weak self] in self?.manager?.nameplatesNeedLayout() }
        fader.follower = nameplate.window
        nameplate.chipItems = { [weak self] in
            guard let self else { return [] }
            return OrbNameplateContentBuilder.items(model: self.model, config: self.config)
        }
        pointer.onMove = { [weak self] location, isPlainMove in
            guard let self else { return }
            if isPlainMove, self.drag != nil { self.finishPendingDrag() }
            self.updatePointer(at: location)
        }
        visibility.onChange = { [weak self] in self?.updateVisibleOnScreen() }
        unitToggle.onToggle = { [weak self] in
            guard let self else { return }
            self.model.orbs.toggleUnit(of: self.id)
        }
        observe()
        applyConfiguration()
    }

    // MARK: Lifecycle

    private func observe() {
        loops.append(ObservationLoop { [weak self] in self?.refreshBody() })
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            let title = self.model.l10n("app.name")
            if self.panel.title != title { self.panel.title = title }
        })
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            let unavailable = self.runtime.feed.unavailableTitleKey != nil
            self.fader.setFactor(unavailable ? 0.55 : 1)
        })
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            self.host.setHighlighted(self.model.orbs.highlightedOrb == self.id && self.fader.isShown)
        })
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            _ = (self.model.l10n.language, self.runtime.feed.bezel, self.model.orbs.orbs.count)
            if self.chipPanel.isShown { self.showChip(refreshOnly: true) }
        })
        loops.append(ObservationLoop { [weak self] in self?.refreshNameplate() })
        dimmer = OrbWindowIdleDimmer(gauge: runtime.feed.gauge, enabled: { [weak self] in self?.config.fadeWhenIdle ?? false },
                                     interaction: interaction) { [weak self] dimmed in
            guard let self else { return }
            self.fader.setDimmed(dimmed, reduceMotion: self.model.accessibility.reduceMotion)
        }
        reporter = OrbAccessibilityReporter(model: model, runtime: runtime, host: host)
        installAccessibilityActions()
    }

    /// Removes the window at once.
    func close() {
        isClosing = true
        unitToggle.cancel()
        unitHoverTimer?.invalidate()
        loops.forEach { $0.cancel() }
        loops.removeAll()
        dimmer?.cancel()
        reporter?.cancel()
        pointer.stop()
        animator.stop()
        presenter.stop()
        chipTimer?.invalidate()
        hoverChipTimer?.invalidate()
        chipPanel.close()
        nameplate.close()
        panel.orderOut(nil)
        panel.close()
    }

    /// Fades the window out (optionally flying toward `target`) and closes it (removed and merged orbs).
    func dismiss(flyingTo target: CGPoint?, reduceMotion: Bool, completion: @escaping @MainActor () -> Void) {
        isClosing = true
        pointer.stop()
        chipPanel.hide()
        if let target, !reduceMotion {
            animator.animate(center: target, motion: .ease(duration: 0.3))
            animator.animate(presence: 0.85, motion: .ease(duration: 0.3))
        }
        fader.fadeOut(duration: reduceMotion ? 0.2 : 0.3) { [weak self] in
            self?.close()
            completion()
        }
    }

    // MARK: Configuration

    /// Applies an edited configuration live (DESIGN 5: every change applies and persists immediately).
    func update(config: OrbConfig) {
        guard config != self.config else { return }
        let previous = self.config
        self.config = config
        applyConfiguration()
        if previous.sources != config.sources { showSourcesBriefly() }
        if previous.fadeWhenIdle != config.fadeWhenIdle { dimmer?.refresh() }
    }

    private func applyConfiguration() {
        presenter.setStyle(config.style)
        host.setHitShape(config.style.hitShape)
        panel.apply(alwaysOnTop: config.alwaysOnTop, allSpaces: config.allSpaces)
        chipPanel.panel.level = panel.level
        nameplate.setLevel(panel.level, behavior: panel.collectionBehavior)
        fader.followerFactor = CGFloat(config.opacity)
        fader.reorderIfShown()
        resize(to: CGFloat(config.footprintDiameter))
        updateTracking()
        refreshBody()
        refreshNameplate()
    }

    /// The plate's content (nil when this orb shows none), material and motion settings.
    private func refreshNameplate() {
        let settings = model.settings
        _ = (model.l10n.language, runtime.feed.bezel)
        let wanted = NameplateVisibility.isDrawn(config, mode: settings.orbNames, shownOrbs: model.orbs.shownOrbs.count, isShown: true)
        let content = wanted ? OrbNameplateContentBuilder.content(model: model, config: config) : nil
        let changed = content != nameplate.content
        let accessibility = model.accessibility
        nameplate.setTokens(NameplateTokens(dark: presenter.dark, reduceTransparency: accessibility.reduceTransparency,
                                            increaseContrast: accessibility.increaseContrast),
                            reduceMotion: accessibility.reduceMotion)
        nameplate.setAppearance(host.effectiveAppearance)
        nameplate.setContent(content)
        nameplate.refreshExpansion()
        if changed { manager?.nameplatesNeedLayout() }
    }

    private func refreshBody() {
        let hideBlur = model.accessibility.reduceTransparency
        let opacity = CGFloat(config.opacity)
        if host.blurHidden != hideBlur { host.blurHidden = hideBlur }
        if host.blurView.alphaValue != opacity { host.blurView.alphaValue = opacity }
        let contrast = model.accessibility.increaseContrast
        let dark = presenter.dark
        let style = config.style
        let rim = runtime.feed.bezel == nil
        host.setBody { body in
            body = OrbBody(diameter: body.diameter, canvasSide: body.canvasSide, tokens: dark ? .dark : .light,
                           opacity: Double(opacity), solid: hideBlur, increaseContrast: contrast, style: style, rim: rim)
        }
    }

    // MARK: Placement

    var radius: CGFloat { animator.targetState.diameter / 2 }
    var center: CGPoint { animator.targetState.center }
    var disc: OrbDisc { OrbDisc(center: center, radius: radius) }
    var isShown: Bool { fader.isShown }
    var level: NSWindow.Level { panel.level }
    var screen: NSScreen? { Self.screen(containing: center) }

    private var reduceMotion: Bool { model.accessibility.reduceMotion }

    /// Motion for a programmatic move: none while hidden, an ease under Reduce Motion.
    private func motion(_ preferred: OrbWindowMotion) -> OrbWindowMotion? {
        guard fader.isShown else { return nil }
        return reduceMotion ? .reduced : preferred
    }

    static func screen(containing center: CGPoint) -> NSScreen? {
        let screens = NSScreen.screens
        guard let index = OrbSnap.screenIndex(for: center, in: screens.map(\.orbSnapScreen)) else { return nil }
        return screens[index]
    }

    /// Puts the disc at `center` at once (launch, arrangement changes while hidden).
    func jump(to center: CGPoint) {
        animator.jump(center: center, diameter: appliedDiameter)
    }

    /// Moves the disc to `center` (arrangement changes, pushes, a11y corner); `persist` stores it as placed.
    func move(to center: CGPoint, motion preferred: OrbWindowMotion = .layout, persist: Bool) {
        if center != animator.targetState.center {
            animator.animate(center: center, motion: motion(preferred))
        }
        if persist { settle(at: center) }
    }

    /// Stores the resting position for the current arrangement.
    private func settle(at center: CGPoint) {
        guard let screen = Self.screen(containing: center) else { return }
        manager?.orbSettled(id, center: center, radius: radius, on: screen)
    }

    /// A created or split orb springs in from 0.6 × (or 0.85 ×) scale and 0 opacity, optionally from `origin`.
    func animateEntrance(from origin: CGPoint?, to target: CGPoint, scale: CGFloat = 0.6, delay: TimeInterval = 0) {
        let reduceMotion = self.reduceMotion
        nameplate.delayNextAppearance(reduceMotion ? 0 : 0.28)
        isEntering = true
        if let origin, !reduceMotion { animator.jump(center: origin, diameter: appliedDiameter) } else { animator.jump(center: target, diameter: appliedDiameter) }
        if !reduceMotion { animator.setPresence(scale) }
        let start = { @MainActor [weak self] in
            guard let self, !self.isClosing else { return }
            self.isEntering = false
            self.manager?.nameplatesNeedLayout()
            self.setShown(self.wantsShown)
            if !reduceMotion {
                self.animator.animate(center: target, motion: .entrance)
                self.animator.animate(presence: 1, motion: .entrance)
            }
        }
        if delay > 0 {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { MainActor.assumeIsolated { start() } }
        } else {
            start()
        }
    }

    /// The merged orb springs from 0.85 back to 1; the split original dips to 0.85 and back.
    func pop(from scale: CGFloat) {
        guard !reduceMotion, fader.isShown else { return }
        animator.setPresence(scale)
        animator.animate(presence: 1, motion: .entrance)
    }

    private func resize(to diameter: CGFloat) {
        guard diameter != appliedDiameter else { return }
        let oldRadius = appliedDiameter / 2
        appliedDiameter = diameter
        let animation: OrbWindowMotion? = fader.isShown && !reduceMotion ? .layout : nil
        animator.animate(diameter: diameter, motion: animation)
        guard drag == nil else { return }
        let current = animator.targetState.center
        guard let screen = Self.screen(containing: current) else { return }
        let center = OrbSnap.resized(current, from: oldRadius, to: diameter / 2, in: screen.visibleFrame)
        animator.animate(center: center, motion: animation)
        settle(at: center)
    }

    private func apply(_ state: OrbWindowAnimator.State) {
        let side = OrbPanel.side(for: state.diameter)
        let scale = max(1, panel.screen?.backingScaleFactor ?? panel.backingScaleFactor)
        let origin = NSPoint(
            x: ((state.center.x - side / 2) * scale).rounded() / scale,
            y: ((state.center.y - side / 2) * scale).rounded() / scale
        )
        let frame = NSRect(origin: origin, size: NSSize(width: side, height: side))
        if panel.frame.size != frame.size {
            panel.setFrame(frame, display: true)
        } else if panel.frame.origin != origin {
            panel.setFrameOrigin(origin)
        }
        host.setDisc(diameter: state.diameter, hoverScale: state.scale)
        presenter.diameter = state.diameter
        presenter.hoverScale = state.scale
        if drag == nil { refreshPointer() }
        if chipPanel.isShown { repositionChip() }
        let area = Self.screen(containing: state.center)?.visibleFrame ?? .zero
        nameplate.follow(disc: OrbDisc(center: state.center, radius: state.diameter * state.scale / 2), area: area)
        manager?.orbMoved(id)
    }

    // MARK: Visibility

    /// Shows or hides the window (its `visible` flag and the master switch).
    func setShown(_ shown: Bool) {
        wantsShown = shown
        guard !isClosing, shown != fader.isShown else { return }
        if shown {
            fader.setShown(true)
        } else {
            animator.finish()
            fader.setShown(false)
            chipPanel.hide()
            host.setHighlighted(false)
        }
        nameplate.setOrbShown(shown)
        manager?.nameplatesNeedLayout()
        updateTracking()
        updateVisibleOnScreen()
    }

    private func updateVisibleOnScreen() {
        let visible = fader.isShown && visibility.allowsVisibility
        if interaction.isVisibleOnScreen != visible { interaction.isVisibleOnScreen = visible }
    }

    /// Re-applies front ordering (z-order: the last dragged or clicked orb in front).
    func orderFront() {
        guard fader.isShown else { return }
        panel.orderFrontRegardless()
        if chipPanel.isShown { chipPanel.panel.orderFrontRegardless() }
    }

    // MARK: Pointer

    private func updateTracking() {
        if fader.isShown, !config.clickThrough {
            pointer.start()
        } else {
            pointer.stop()
        }
        refreshPointer()
    }

    private func refreshPointer() {
        updatePointer(at: NSEvent.mouseLocation)
    }

    /// Re-reads the pointer after a stretch in which no move reached the monitors: a menu or modal session of the
    /// app took every mouse event, so the pointer may now rest on this orb (or have left it) unseen.
    func pointerMayHaveMoved() {
        refreshPointer()
    }

    /// Lets the panel take the mouse only while the pointer is over the disc, and the plate only over its capsule;
    /// everything else clicks through.
    private func updatePointer(at location: NSPoint) {
        guard drag == nil else { return }
        let interactive = fader.isShown && !config.clickThrough && !pointer.isForeignDragActive && !isClosing
        let inside = interactive && host.discContains(screenPoint: location)
        let onPlate = interactive && !inside && nameplate.contains(screenPoint: location)
        let nearPlate = onPlate || (interactive && interaction.isHovered
            && nameplate.contains(screenPoint: location, slack: nameplate.hoverSlack))
        if panel.ignoresMouseEvents == inside {
            panel.ignoresMouseEvents = !inside
            OrbWindowLog.debug("\(id.prefix(8)) takes the mouse: \(inside) at \(OrbWindowLog.describe(location))")
        }
        nameplate.setAcceptsMouse(onPlate)
        setHovered(inside || nearPlate)
        setUnitHovered(inside && isOnUnit(screenPoint: location))
    }

    // MARK: Unit toggle (DESIGN 9)

    /// The unit's hit area in the host view; nil when the readout shows only the value at thumbnail size.
    private var unitHitRect: NSRect? {
        let content = presenter.content
        guard content.showsReadout, !content.valueOnly, content.unavailableTitle == nil else { return nil }
        return OrbUnitToggle.hitRect(
            layout: presenter.animator.renderer.readoutLayout.at(diameter: host.discDiameter),
            discDiameter: host.discDiameter * host.hoverScale,
            in: host.bounds, unit: content.unit, value: presenter.animator.readout.odometer.current
        )
    }

    private func isOnUnit(local point: NSPoint) -> Bool {
        guard let rect = unitHitRect else { return false }
        return OrbUnitToggle.contains(point, rect: rect, discDiameter: host.discDiameter * host.hoverScale, in: host.bounds)
    }

    private func isOnUnit(screenPoint point: NSPoint) -> Bool {
        guard let window = host.window else { return false }
        return isOnUnit(local: host.convert(window.convertPoint(fromScreen: point), from: nil))
    }

    /// Over the unit: the pointing hand, the unit raised from 55 % to 85 % with a faint underline (0.15 s) and the
    /// `orbs.unit.toggleHint` tooltip.
    private func setUnitHovered(_ hovered: Bool) {
        guard hovered != unitHovered else { return }
        unitHovered = hovered
        if interaction.isHovered {
            let cursor: NSCursor = hovered ? .pointingHand : .openHand
            host.cursor = cursor
            cursor.set()
        }
        host.removeAllToolTips()
        if hovered, let rect = unitHitRect { host.addToolTip(rect, owner: model.l10n("orbs.unit.toggleHint") as NSString, userData: nil) }
        let readout = presenter.animator.readout
        let start = readout.unitHoverAmount
        let target = hovered ? 1.0 : 0.0
        let began = Clock.now
        unitHoverTimer?.invalidate()
        guard !reduceMotion else {
            readout.unitHoverAmount = target
            presenter.canvas.setNeedsFrame()
            return
        }
        let timer = Timer(timeInterval: 1.0 / 60, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                let progress = ((Clock.now - began) / 0.15).clamped(to: 0...1)
                readout.unitHoverAmount = start + (target - start) * Easing.easeOut(progress)
                self.presenter.canvas.setNeedsFrame()
                if progress >= 1 {
                    self.unitHoverTimer?.invalidate()
                    self.unitHoverTimer = nil
                }
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        unitHoverTimer = timer
    }

    private func setHovered(_ hovered: Bool) {
        guard interaction.isHovered != hovered else { return }
        interaction.isHovered = hovered
        host.cursor = hovered ? .openHand : nil
        nameplate.cursor = host.cursor
        if hovered {
            NSCursor.openHand.set()
        } else {
            NSCursor.arrow.set()
        }
        let scale = hovered && !reduceMotion ? Self.hoverScale : 1
        animator.animate(hoverScale: scale, motion: reduceMotion ? nil : .hover)
        manager?.orbHoverChanged(id, hovered: hovered)
        hoverChipTimer?.invalidate()
        hoverChipTimer = nil
        if hovered {
            let timer = Timer(timeInterval: Self.chipHoverDelay, repeats: false) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let self, self.interaction.isHovered else { return }
                    self.showChip()
                }
            }
            RunLoop.main.add(timer, forMode: .common)
            hoverChipTimer = timer
        } else {
            updateChipVisibility()
        }
    }

    private func beginDragging() {
        interaction.isDragging = true
        host.cursor = .closedHand
        nameplate.cursor = .closedHand
        NSCursor.closedHand.push()
        cursorPushed = true
        showChip()
    }

    private func endDragging() {
        drag = nil
        dragInterruptedMotion = false
        if interaction.isDragging { interaction.isDragging = false }
        if cursorPushed {
            NSCursor.pop()
            cursorPushed = false
        }
        manager?.showGuides([], on: nil)
        refreshPointer()
        host.cursor = interaction.isHovered ? .openHand : nil
        nameplate.cursor = host.cursor
        (interaction.isHovered ? NSCursor.openHand : NSCursor.arrow).set()
        updateChipVisibility()
        manager?.nameplatesNeedLayout()
    }

    /// Ends a tracked press that will not see its mouse-up (a plain move arrived, or a menu took over).
    private func finishPendingDrag() {
        guard let session = drag else { return }
        if session.isMoving || dragInterruptedMotion { release(velocity: .zero) }
        endDragging()
    }

    /// Snaps the disc on release: screen edge first, then touching another orb, then alignment; never overlapping
    /// another orb and always fully on screen (DESIGN 2b, Creating, placing and snapping).
    private func release(velocity: CGVector) {
        let current = animator.state.center
        guard let screen = Self.screen(containing: current) else { return }
        let others = manager?.otherDiscs(excluding: id, on: screen) ?? []
        var target = OrbMagnet.releaseTarget(raw: current, radius: radius, others: others, visible: screen.visibleFrame)
        let boards = manager?.boardShapes(on: screen) ?? []
        let shape = FloatingShape(OrbDisc(center: target, radius: radius))
        if !FloatingPlacement.isFree(shape, others: boards) {
            target = FloatingMagnet.releaseTarget(raw: target, shape: shape, others: others.map(FloatingShape.init) + boards,
                                                  visible: screen.visibleFrame)
        }
        animator.animate(center: target, motion: motion(.snap), velocity: reduceMotion ? .zero : velocity)
        settle(at: target)
    }

    /// The a11y action `a11y.orb.moveCorner`: the next corner of its display, placed as a drag release.
    func moveToNextCorner() {
        guard let screen = Self.screen(containing: center) else { return }
        let others = manager?.otherDiscs(excluding: id, on: screen) ?? []
        let target = OrbPlacement.cornerCycle(current: disc, visible: screen.visibleFrame, others: others)
        move(to: target, motion: .snap, persist: true)
    }

    // MARK: Source chip

    private var chipVisibleByTimer: Bool { Clock.now < chipUntil }

    /// Shows the chip for 2 s after create, split or merge; an orb with a name plate shows its plate instead, which
    /// fades in after landing and stays (DESIGN 2b, Name plate, Chip and undo).
    func showChipBriefly(after delay: TimeInterval = 0) {
        guard !nameplate.wantsPlate else { return }
        showSourcesBriefly(after: delay)
    }

    /// Shows the chip, or widens the plate to it, for 2 s (also after a sources change).
    private func showSourcesBriefly(after delay: TimeInterval = 0) {
        let show = { @MainActor [weak self] in
            guard let self, self.fader.isShown || self.wantsShown else { return }
            self.chipUntil = Clock.now + 2
            self.showChip()
            self.chipTimer?.invalidate()
            let timer = Timer(timeInterval: 2.05, repeats: false) { [weak self] _ in
                MainActor.assumeIsolated { self?.updateChipVisibility() }
            }
            RunLoop.main.add(timer, forMode: .common)
            self.chipTimer = timer
        }
        if delay > 0 {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { MainActor.assumeIsolated { show() } }
        } else {
            show()
        }
    }

    private func updateChipVisibility() {
        let showing = chipPanel.isShown || nameplate.isExpanded
        let wanted = fader.isShown && (interaction.isDragging || chipVisibleByTimer || (interaction.isHovered && showing))
        if !wanted {
            chipPanel.hide()
            nameplate.setExpanded(false)
        }
    }

    private func showChip(refreshOnly: Bool = false) {
        if nameplate.foldsChip {
            chipPanel.hide()
            if !refreshOnly { nameplate.setExpanded(true) }
            return
        }
        guard fader.isShown || wantsShown, let items = chipItems() else {
            chipPanel.hide()
            return
        }
        if refreshOnly, !chipPanel.isShown { return }
        let appearance = host.effectiveAppearance
        chipPanel.show(OrbChipView(items: items), level: panel.level, appearance: appearance) { [weak self] size in
            self?.chipFrame(size) ?? .zero
        }
    }

    private func repositionChip() {
        chipPanel.reposition { [weak self] size in self?.chipFrame(size) ?? .zero }
    }

    private func chipFrame(_ size: CGSize) -> CGRect {
        let state = animator.state
        let disc = OrbDisc(center: state.center, radius: state.diameter * state.scale / 2)
        guard let screen = Self.screen(containing: state.center) else { return CGRect(origin: .zero, size: size) }
        let others = manager?.otherDiscs(excluding: id, on: screen) ?? []
        return OrbChipPlacement.frame(size: size, disc: disc, visible: screen.visibleFrame, others: others)
    }

    /// Chip content (DESIGN 2b): dot + name for up to two sources, else the first and `+n`; unavailable sources in
    /// the secondary color with their status. An all-sources orb shows `orbs.sources.all` only with several orbs.
    private func chipItems() -> [OrbChipItem]? {
        let l10n = model.l10n
        if config.sources.isAll {
            guard model.orbs.orbs.count > 1 else { return nil }
            return [OrbChipItem(id: "all", color: nil, text: l10n("orbs.sources.all"), secondary: false)]
        }
        let dots = model.orbs.dots(of: id)
        guard !dots.isEmpty else { return nil }
        let status = runtime.feed.unavailableTitleKey
        func item(_ dot: (source: SourceKind, available: Bool)) -> OrbChipItem {
            let name = l10n(dot.source.titleKey)
            guard !dot.available else { return OrbChipItem(id: dot.source.rawValue, color: dot.source.tint, text: name, secondary: false) }
            let reason = model.snapshot.sources.first { $0.kind == dot.source }.map { $0.enabled ? "source.notFound" : "source.disabled" }
                ?? status ?? "source.notFound"
            return OrbChipItem(id: dot.source.rawValue, color: dot.source.tint, text: name + " · " + l10n(reason), secondary: true)
        }
        if dots.count <= 2 { return dots.map(item) }
        var first = item(dots[0])
        first.text += "  +\(dots.count - 1)"
        return [first]
    }

    // MARK: Accessibility

    private func installAccessibilityActions() {
        let l10n = model.l10n
        func action(_ key: String, _ handler: @escaping @MainActor () -> Void) -> NSAccessibilityCustomAction {
            NSAccessibilityCustomAction(name: l10n(key)) {
                MainActor.assumeIsolated { handler() }
                return true
            }
        }
        host.accessibilityActions = [
            action("menu.dashboard") { [weak self] in self?.model.commands?.showDashboard() },
            action("a11y.orb.showMenu") { [weak self] in self?.hostShowMenu(nil) },
            action("a11y.orb.toggleUnit") { [weak self] in
                guard let self else { return }
                self.model.orbs.toggleUnit(of: self.id)
            },
            action("a11y.orb.moveCorner") { [weak self] in self?.moveToNextCorner() },
            action("menu.orb.settings") { [weak self] in
                guard let self else { return }
                self.model.commands?.showOrbSettings(self.id)
            },
        ]
    }

    func refreshAccessibilityActions() {
        installAccessibilityActions()
    }

    #if DEBUG
    /// `--orbs-demo`: drags the disc to `raw` as a pointer drag would (magnet, guides, chip).
    func debugDrag(to raw: CGPoint) {
        if !interaction.isDragging {
            animator.freezeCenter()
            dragRegion = OrbSnap.dragRegion(screens: NSScreen.screens.map(\.orbSnapScreen), radius: radius)
            interaction.isDragging = true
            showChip()
        }
        let banded = OrbSnap.rubberBanded(raw, region: dragRegion, radius: radius)
        guard let screen = Self.screen(containing: banded) else { return }
        let others = manager?.otherDiscs(excluding: id, on: screen) ?? []
        let magnet = OrbMagnet.dragTarget(raw: banded, radius: radius, others: others, visible: screen.visibleFrame)
        manager?.showGuides(magnet.guides, on: screen)
        animator.animate(center: magnet.target, motion: .follow)
    }

    /// `--orbs-demo`: releases a debug drag.
    func debugRelease() {
        release(velocity: .zero)
        interaction.isDragging = false
        manager?.showGuides([], on: nil)
        updateChipVisibility()
    }
    #endif
}

// MARK: - Name plate placement

extension OrbWindowController: OrbNameplateHost {
    var isMovingForNameplate: Bool { drag != nil || interaction.isDragging || isEntering || animator.isMovingCenter }
}

// MARK: - Host input

extension OrbWindowController: OrbHostViewDelegate {
    func hostPointerMoved() {
        refreshPointer()
    }

    func hostMouseDown(_ event: NSEvent) {
        manager?.orbInteracted(id)
        if event.modifierFlags.contains(.control) {
            hostShowMenu(event)
            return
        }
        unitToggle.mouseDown(event, onUnit: event.window === panel && isOnUnit(local: host.convert(event.locationInWindow, from: nil)))
        if event.clickCount >= 2 {
            endDragging()
            model.commands?.showDashboard()
            return
        }
        dragInterruptedMotion = animator.isMovingCenter
        animator.freezeCenter()
        drag = OrbDragSession(pointer: NSEvent.mouseLocation, center: animator.state.center)
        dragRegion = OrbSnap.dragRegion(screens: NSScreen.screens.map(\.orbSnapScreen), radius: radius)
    }

    func hostMouseDragged(_ event: NSEvent) {
        guard var session = drag else { return }
        guard let raw = session.center(for: NSEvent.mouseLocation) else { return }
        if !interaction.isDragging { beginDragging() }
        let banded = OrbSnap.rubberBanded(raw, region: dragRegion, radius: radius)
        var target = banded
        var guides: [OrbGuide] = []
        if let screen = Self.screen(containing: banded) {
            let others = manager?.otherDiscs(excluding: id, on: screen) ?? []
            if !others.isEmpty {
                let magnet = OrbMagnet.dragTarget(raw: banded, radius: radius, others: others, visible: screen.visibleFrame)
                target = magnet.target
                guides = magnet.guides
            }
            manager?.showGuides(guides, on: screen)
        }
        if reduceMotion || (target == banded && !animator.isMovingCenter) {
            animator.setCenter(target)
        } else {
            animator.animate(center: target, motion: .follow)
        }
        session.record(target)
        drag = session
    }

    func hostMouseUp(_ event: NSEvent) {
        guard let session = drag else { return }
        let moved = session.isMoving || dragInterruptedMotion
        unitToggle.mouseUp(event, moved: moved)
        let velocity = session.releaseVelocity()
        drag = nil
        if moved { release(velocity: velocity) }
        endDragging()
    }

    func hostShowMenu(_ event: NSEvent?) {
        finishPendingDrag()
        manager?.orbInteracted(id)
        let menu = MenuFactory.orbContextMenu(model: model, orbId: id)
        // At the pointer for a right-click on the disc or the name plate; at the disc's centre from accessibility.
        let point = event.map { $0.window?.convertPoint(toScreen: $0.locationInWindow) ?? NSEvent.mouseLocation }
            ?? NSPoint(x: panel.frame.midX, y: panel.frame.midY)
        menu.popUpDetached(at: point, appearance: host.effectiveAppearance)
        refreshPointer()
    }

    func hostPress() {
        model.commands?.showDashboard()
    }
}
