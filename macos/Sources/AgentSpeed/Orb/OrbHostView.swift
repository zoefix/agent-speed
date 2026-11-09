import AgentSpeedOrbModel
import AppKit
import SwiftUI

/// Receives pointer and accessibility input from the disc.
@MainActor
protocol OrbHostViewDelegate: AnyObject {
    func hostPointerMoved()
    func hostMouseDown(_ event: NSEvent)
    func hostMouseDragged(_ event: NSEvent)
    func hostMouseUp(_ event: NSEvent)
    func hostShowMenu(_ event: NSEvent?)
    func hostPress()
}

/// Content view of the orb panel: the clipped desktop blur, the static body (SwiftUI, redrawn only when its inputs
/// change) and the Metal canvas of the dynamic layers. Only the disc takes part in hit testing; the rest of the canvas
/// lets clicks through. The disc is also the single accessibility element.
final class OrbHostView: NSView {
    /// Extra reach around the disc edge for pointer hits.
    static let hitTolerance: CGFloat = 2

    weak var delegate: OrbHostViewDelegate?
    /// Shown while the pointer is over the disc; nil leaves the cursor alone.
    var cursor: NSCursor?

    var accessibilityLabelText = ""
    var accessibilityValueText = ""
    var accessibilityHelpText = ""
    /// Custom VoiceOver actions (DESIGN 2b, Accessibility and keyboard).
    var accessibilityActions: [NSAccessibilityCustomAction] = []
    /// Called when the effective appearance changes (light / dark body and tokens).
    var onAppearanceChange: ((Bool) -> Void)?

    let blurView = OrbBlurView(frame: .zero)
    let canvas = OrbCanvasView(frame: .zero)
    private let bodyView: NSHostingView<OrbBody>
    private(set) var discDiameter: CGFloat
    private(set) var hoverScale: CGFloat = 1
    private var trackingArea: NSTrackingArea?
    private var bodyInputs: OrbBody
    /// The Settings highlight ring (DESIGN 2b): 2 pt accent ring 4 pt outside the disc at 60 %.
    private let highlightLayer = CAShapeLayer()
    private var isHighlighted = false

    init(diameter: CGFloat) {
        discDiameter = diameter
        let side = OrbPanel.side(for: diameter)
        bodyInputs = OrbBody(diameter: diameter, canvasSide: side, tokens: .dark, opacity: 1, solid: false, increaseContrast: false)
        bodyView = NSHostingView(rootView: bodyInputs)
        super.init(frame: NSRect(x: 0, y: 0, width: side, height: side))
        wantsLayer = true

        bodyView.sizingOptions = []
        bodyView.safeAreaRegions = []
        bodyView.frame = bounds
        bodyView.autoresizingMask = [.width, .height]
        bodyView.setAccessibilityElement(false)
        canvas.frame = bounds
        canvas.autoresizingMask = [.width, .height]

        addSubview(blurView)
        addSubview(bodyView)
        addSubview(canvas)
        highlightLayer.fillColor = nil
        highlightLayer.lineWidth = 2
        highlightLayer.opacity = 0
        highlightLayer.actions = ["path": NSNull(), "position": NSNull(), "bounds": NSNull()]
        layer?.addSublayer(highlightLayer)
        updateBlurFrame()
    }

    /// Rings the orb on the desktop while its Settings row is hovered, selected or focused (fade-in 0.15 s).
    func setHighlighted(_ highlighted: Bool) {
        guard highlighted != isHighlighted else { return }
        isHighlighted = highlighted
        highlightLayer.strokeColor = NSColor.controlAccentColor.withAlphaComponent(0.6).cgColor
        let animation = CABasicAnimation(keyPath: "opacity")
        animation.fromValue = highlightLayer.presentation()?.opacity ?? highlightLayer.opacity
        animation.toValue = highlighted ? 1 : 0
        animation.duration = highlighted ? 0.15 : 0.2
        highlightLayer.opacity = highlighted ? 1 : 0
        highlightLayer.add(animation, forKey: "fade")
        updateHighlightPath()
    }

    private func updateHighlightPath() {
        let radius = discDiameter * hoverScale / 2 + 4 + 1
        highlightLayer.frame = bounds
        if hitShape == .disc {
            highlightLayer.path = CGPath(ellipseIn: CGRect(x: bounds.midX - radius, y: bounds.midY - radius, width: 2 * radius,
                                                           height: 2 * radius), transform: nil)
        } else {
            let box = discRect(scale: hoverScale).insetBy(dx: -5, dy: -5)
            highlightLayer.path = CGPath(roundedRect: box, cornerWidth: box.width / 2, cornerHeight: box.width / 2, transform: nil)
        }
        if highlightLayer.superlayer != nil, layer?.sublayers?.last !== highlightLayer {
            highlightLayer.removeFromSuperlayer()
            layer?.addSublayer(highlightLayer)
        }
    }

    var isDark: Bool { effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        onAppearanceChange?(isDark)
    }

    /// Updates the static body; redraws only when an input changed.
    func setBody(_ update: (inout OrbBody) -> Void) {
        var next = bodyInputs
        update(&next)
        next = OrbBody(diameter: discDiameter, canvasSide: bounds.width, tokens: next.tokens, opacity: next.opacity,
                       solid: next.solid, increaseContrast: next.increaseContrast, style: next.style, rim: next.rim,
                       scale: hoverScale)
        guard next != bodyInputs else { return }
        bodyInputs = next
        bodyView.rootView = next
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not supported")
    }

    func setDisc(diameter: CGFloat, hoverScale: CGFloat) {
        guard diameter != discDiameter || hoverScale != self.hoverScale else { return }
        discDiameter = diameter
        self.hoverScale = hoverScale
        updateBlurFrame()
        setBody { _ in }
        if isHighlighted { updateHighlightPath() }
    }

    /// Whether a point in screen coordinates is on the disc (including the hover scale and tolerance).
    func discContains(screenPoint point: NSPoint) -> Bool {
        guard let window else { return false }
        let local = convert(window.convertPoint(fromScreen: point), from: nil)
        return discContains(local)
    }

    /// The disc in screen coordinates.
    var discScreenFrame: NSRect {
        guard let window else { return .zero }
        return window.convertToScreen(convert(discRect(scale: hoverScale), to: nil))
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        updateBlurFrame()
        setBody { _ in }
        if isHighlighted { updateHighlightPath() }
    }

    override var isOpaque: Bool { false }
    override var mouseDownCanMoveWindow: Bool { false }

    // MARK: Hit testing and pointer

    override func hitTest(_ point: NSPoint) -> NSView? {
        let local = superview.map { convert(point, from: $0) } ?? point
        return discContains(local) ? self : nil
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingArea { removeTrackingArea(trackingArea) }
        let area = NSTrackingArea(
            rect: .zero,
            options: [.mouseEnteredAndExited, .mouseMoved, .cursorUpdate, .activeAlways, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(area)
        trackingArea = area
    }

    override func mouseEntered(with event: NSEvent) { delegate?.hostPointerMoved() }
    override func mouseExited(with event: NSEvent) { delegate?.hostPointerMoved() }
    override func mouseMoved(with event: NSEvent) { delegate?.hostPointerMoved() }

    override func cursorUpdate(with event: NSEvent) {
        if let cursor, discContains(convert(event.locationInWindow, from: nil)) {
            cursor.set()
        } else {
            super.cursorUpdate(with: event)
        }
    }

    override func mouseDown(with event: NSEvent) { delegate?.hostMouseDown(event) }
    override func mouseDragged(with event: NSEvent) { delegate?.hostMouseDragged(event) }
    override func mouseUp(with event: NSEvent) { delegate?.hostMouseUp(event) }
    override func rightMouseDown(with event: NSEvent) { delegate?.hostShowMenu(event) }

    // MARK: Accessibility

    override func isAccessibilityElement() -> Bool { true }
    override func accessibilityRole() -> NSAccessibility.Role? { .image }
    override func accessibilityLabel() -> String? { accessibilityLabelText }
    override func accessibilityValue() -> Any? { accessibilityValueText }
    override func accessibilityHelp() -> String? { accessibilityHelpText }
    override func accessibilityChildren() -> [Any]? { [] }
    override func accessibilityFrame() -> NSRect { discScreenFrame }
    override func accessibilityHitTest(_ point: NSPoint) -> Any? { self }

    override func accessibilityPerformPress() -> Bool {
        delegate?.hostPress()
        return true
    }

    override func accessibilityPerformShowMenu() -> Bool {
        delegate?.hostShowMenu(nil)
        return true
    }

    override func accessibilityCustomActions() -> [NSAccessibilityCustomAction]? { accessibilityActions }

    // MARK: Geometry

    /// The clickable part of the disc: the whole disc, or the character's body (DESIGN 2a.6).
    private(set) var hitShape = OrbHitShape.disc

    /// The character style has no glass body or blur; the window is otherwise transparent.
    func setHitShape(_ shape: OrbHitShape) {
        guard shape != hitShape else { return }
        hitShape = shape
        blurView.isHidden = shape != .disc || blurHidden
        bodyView.isHidden = shape != .disc
        updateBlurFrame()
        if isHighlighted { updateHighlightPath() }
    }

    /// Reduce Transparency hides the blur; the character style hides it regardless.
    var blurHidden = false {
        didSet { blurView.isHidden = hitShape != .disc || blurHidden }
    }

    private func discRect(scale: CGFloat) -> NSRect {
        let side = discDiameter * scale
        let disc = NSRect(x: bounds.midX - side / 2, y: bounds.midY - side / 2, width: side, height: side)
        guard hitShape != .disc else { return disc }
        return hitShape.bounds(diameter: side).offsetBy(dx: bounds.midX, dy: bounds.midY)
    }

    private func discContains(_ point: NSPoint) -> Bool {
        hitShape.contains(dx: point.x - bounds.midX, dy: point.y - bounds.midY, diameter: discDiameter * hoverScale,
                          tolerance: Self.hitTolerance)
    }

    private func updateBlurFrame() {
        let frame = discRect(scale: hoverScale)
        if blurView.frame != frame { blurView.frame = frame }
    }
}
