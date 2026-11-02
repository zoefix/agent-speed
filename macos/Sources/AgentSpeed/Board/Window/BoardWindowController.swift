import AgentSpeedOrbModel
import AppKit

/// One floating board (DESIGN 2c): its panel, the effects window riding above it, the content layers and effects
/// driver, and the drag with magnetism and edge snapping shared with the orbs (boards and orbs avoid each other).
/// The center is in screen points (AppKit, y up).
@MainActor
final class BoardWindowController {
    let panel = BoardPanel()
    let effectsPanel = BoardEffectsPanel()
    let driver: BoardEffectsDriver
    let host: BoardHostView
    let interaction = OrbInteraction()
    private(set) var config: BoardConfig
    private(set) var center: CGPoint
    private(set) var isDimmed = false
    var dark: Bool {
        didSet { if dark != oldValue { applyLook() } }
    }
    var reduceMotion = false {
        didSet {
            host.content.reduceMotion = reduceMotion
            driver.update { $0.setReduceMotion(reduceMotion) }
        }
    }
    /// The other floating windows (orbs and boards) for magnetism and placement.
    var obstacles: () -> [FloatingShape] = { [] }
    /// The board came to rest at a new center after a drag.
    var onMoved: ((CGPoint) -> Void)?
    var onContextMenu: ((NSEvent) -> Void)?
    /// A click (a press that never became a drag) at a point of the board (board points, y down).
    var onClick: ((CGPoint) -> Void)?
    /// Lays out the board's content; asked on `contentChanged()` while the board renders, and once when it renders
    /// again after being hidden or occluded (a board nobody sees lays out and rasterizes nothing).
    var contentProvider: (() -> [BoardContentItem])?
    /// The board can be seen, so content and effects are worth drawing.
    var isRendering: Bool { driver.canRender }
    /// Keyboard navigation is on and the rows take focus: the panel may become key, the host takes the keys.
    var keyboardFocusable: Bool {
        get { panel.keyboardFocusable }
        set {
            guard newValue != panel.keyboardFocusable else { return }
            panel.keyboardFocusable = newValue
            host.keyboardFocusable = newValue
            panel.initialFirstResponder = newValue ? host : nil
        }
    }

    private let animator: OrbWindowAnimator
    private var visibility: OrbWindowVisibilityMonitor?
    private var drag: OrbDragSession?
    private var shown = false
    private var contentStale = true
    private var fadeGeneration = 0

    init(config: BoardConfig, center: CGPoint, dark: Bool) {
        self.config = config
        self.center = center
        self.dark = dark
        driver = BoardEffectsDriver(style: config.style, side: config.size.side)
        host = BoardHostView(warp: driver.warp.layer)
        animator = OrbWindowAnimator(center: center, diameter: CGFloat(config.size.side))
        panel.contentView = host
        effectsPanel.contentView = driver.overlay
        animator.attach(to: host)
        animator.onChange = { [weak self] state in self?.place(at: state.center) }
        host.onMouseDown = { [weak self] _ in self?.beginDrag() }
        host.onMouseDragged = { [weak self] _ in self?.continueDrag() }
        host.onMouseUp = { [weak self] event in self?.endDrag(event) }
        host.onRightMouseDown = { [weak self] event in self?.onContextMenu?(event) }
        host.onHoverChanged = { [weak self] hovered in
            guard let self else { return }
            self.interaction.isHovered = !self.config.clickThrough && hovered
        }
        visibility = OrbWindowVisibilityMonitor(window: panel)
        visibility?.onChange = { [weak self] in self?.updateRendering() }
        applyLook()
        applyWindowOptions()
        place(at: center)
    }

    var side: CGFloat { CGFloat(config.size.side) }

    var shape: FloatingShape {
        .board(center: center, side: side, corner: CGFloat(config.style.cornerRadius))
    }

    var targetAlpha: CGFloat {
        CGFloat(min(max(config.opacity, 0), 1)) * (isDimmed ? OrbWindowFader.dimmedAlpha : 1)
    }

    // MARK: Showing

    func show() {
        guard !shown else { return }
        shown = true
        place(at: center)
        panel.orderFrontRegardless()
        effectsPanel.orderFrontRegardless()
        panel.addChildWindow(effectsPanel, ordered: .above)
        updateRendering()
    }

    func hide() {
        guard shown else { return }
        shown = false
        interaction.isHovered = false
        interaction.isDragging = false
        panel.removeChildWindow(effectsPanel)
        effectsPanel.orderOut(nil)
        panel.orderOut(nil)
        updateRendering()
    }

    /// A new board fades in where it was placed (DESIGN 2c `boards.add`, as a new orb appears).
    func fadeIn(duration: TimeInterval = 0.25) {
        let alpha = targetAlpha
        panel.alphaValue = 0
        effectsPanel.alphaValue = 0
        NSAnimationContext.runAnimationGroup { context in
            context.duration = duration
            panel.animator().alphaValue = alpha
            effectsPanel.animator().alphaValue = alpha
        }
    }

    /// Idle fade ("fade when idle", DESIGN 2c): 45 % of configured opacity after 60 s idle, back on activity or hover.
    func setDimmed(_ dimmed: Bool) {
        guard dimmed != isDimmed else { return }
        isDimmed = dimmed
        fadeGeneration &+= 1
        let alpha = targetAlpha
        guard shown, !reduceMotion else {
            panel.alphaValue = alpha
            effectsPanel.alphaValue = alpha
            updateRendering()
            return
        }
        let current = fadeGeneration
        NSAnimationContext.runAnimationGroup { context in
            context.duration = dimmed ? 0.6 : 0.12
            context.timingFunction = CAMediaTimingFunction(name: dimmed ? .easeInEaseOut : .easeOut)
            panel.animator().alphaValue = alpha
            effectsPanel.animator().alphaValue = alpha
        } completionHandler: { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.fadeGeneration == current else { return }
                self.updateRendering()
            }
        }
    }

    func close() {
        hide()
        animator.stop()
        driver.canRender = false
        panel.close()
        effectsPanel.close()
    }

    /// Applies a changed configuration (style, size, sources, opacity, window toggles).
    func apply(_ newConfig: BoardConfig) {
        let old = config
        config = newConfig
        if newConfig.style != old.style || newConfig.size != old.size {
            driver.update { $0.setStyle(newConfig.style) }
            applyLook()
            place(at: center)
        }
        if !newConfig.fadeWhenIdle, isDimmed { isDimmed = false }
        applyWindowOptions()
        if newConfig.visible != old.visible { newConfig.visible ? show() : hide() }
        updateRendering()
    }

    func move(to target: CGPoint, animated: Bool) {
        animator.animate(center: target, motion: animated ? (reduceMotion ? .reduced : .layout) : nil)
    }

    private func applyLook() {
        host.apply(style: config.style, side: side, dark: dark)
        driver.dark = dark
        driver.setBoard(side: Double(side), corner: CGFloat(config.style.cornerRadius))
        host.content.spring = .of(config.style)
    }

    private func applyWindowOptions() {
        panel.apply(alwaysOnTop: config.alwaysOnTop, allSpaces: config.allSpaces)
        BoardPanel.apply(effectsPanel, alwaysOnTop: config.alwaysOnTop, allSpaces: config.allSpaces)
        panel.ignoresMouseEvents = config.clickThrough
        if config.clickThrough { interaction.isHovered = false }
        let alpha = targetAlpha
        panel.alphaValue = alpha
        effectsPanel.alphaValue = alpha
    }

    /// The data behind the content changed (≤ 2 per second). `crossfade`: a live metrics switch changed (the hero
    /// crossfades, DESIGN 2c Live metrics).
    func contentChanged(animated: Bool = true, crossfade: Double? = nil) {
        guard isRendering, let contentProvider else {
            contentStale = true
            return
        }
        contentStale = false
        host.content.apply(contentProvider(), animated: animated, crossfade: crossfade)
    }

    private func updateRendering() {
        driver.canRender = shown && panel.isVisible && config.visible && targetAlpha > 0.01
            && (visibility?.allowsVisibility ?? true)
        if isRendering, contentStale { contentChanged(animated: false) }
    }

    private func place(at point: CGPoint) {
        center = point
        let side = self.side
        let margin = CGFloat(BoardEffects.margin)
        let frame = NSRect(x: (point.x - side / 2).rounded(), y: (point.y - side / 2).rounded(), width: side, height: side)
        if panel.frame != frame { panel.setFrame(frame, display: false) }
        let outer = frame.insetBy(dx: -margin, dy: -margin)
        if effectsPanel.frame != outer { effectsPanel.setFrame(outer, display: false) }
        driver.overlay.frame = CGRect(origin: .zero, size: outer.size)
    }

    // MARK: Dragging

    private var screens: [OrbSnapScreen] { NSScreen.screens.map(\.orbSnapScreen) }

    private func visibleFrame(at point: CGPoint) -> CGRect {
        let screen = NSScreen.screens.first { $0.frame.contains(point) } ?? panel.screen ?? NSScreen.main
        return screen?.visibleFrame ?? CGRect(x: 0, y: 0, width: 1440, height: 900)
    }

    private func beginDrag() {
        animator.freezeCenter()
        interaction.isDragging = true
        drag = OrbDragSession(pointer: NSEvent.mouseLocation, center: animator.state.center)
    }

    private func continueDrag() {
        guard var session = drag, let raw = session.center(for: NSEvent.mouseLocation) else { return }
        let half = side / 2
        let banded = OrbSnap.rubberBanded(raw, region: OrbSnap.dragRegion(screens: screens, radius: half), radius: half)
        let result = FloatingMagnet.dragTarget(raw: banded, shape: shape.moved(to: banded), others: obstacles(),
                                               visible: visibleFrame(at: banded))
        animator.animate(center: result.target, motion: reduceMotion ? nil : .follow)
        session.record(result.target)
        drag = session
    }

    private func endDrag(_ event: NSEvent) {
        guard let session = drag else { return }
        drag = nil
        interaction.isDragging = false
        guard session.isMoving else {
            let local = host.convert(event.locationInWindow, from: nil)
            onClick?(CGPoint(x: local.x, y: side - local.y))
            return
        }
        let current = animator.targetState.center
        let target = FloatingMagnet.releaseTarget(raw: current, shape: shape.moved(to: current), others: obstacles(),
                                                  visible: visibleFrame(at: current))
        animator.animate(center: target, motion: reduceMotion ? .reduced : .snap, velocity: session.releaseVelocity())
        onMoved?(target)
    }
}
