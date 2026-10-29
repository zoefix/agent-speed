import AgentSpeedOrbModel
import AppKit
import Observation
import SwiftUI

/// What the card window shows; observed so content follows the engine tick without rebuilding the view (which would
/// reset the number's count).
@MainActor
@Observable
final class PerfCardContent {
    /// The agent (or group) the card belongs to: a change crossfades the whole card.
    var key: String = ""
    var model: PerfCardModel?
    var reduceMotion = false
    var fit = PerfCardFit.full

    @ObservationIgnored let l10n: Localizer

    init(l10n: Localizer) {
        self.l10n = l10n
    }
}

/// The card window (DESIGN 12, *Window*): a non-activating borderless panel, 320 pt wide and as tall as its content,
/// on the system popover material (Liquid Glass on macOS 26), radius 12, a 0.5 pt separator stroke and the system
/// shadow; a solid fill under Reduce Transparency. It never takes focus and ignores the mouse: it is a tooltip.
@MainActor
final class PerfCardPanel {
    static let fadeIn: TimeInterval = 0.15
    static let switchFade: TimeInterval = 0.12

    let content: PerfCardContent
    private let panel: CardWindow
    private let hosting: NSHostingView<PerfCardHost>
    private var reduceTransparency: Bool?
    /// The tallest the card may be on its screen (DESIGN 12, *Fit*).
    private var heightLimit: CGFloat = .greatestFiniteMagnitude
    private let transition = WindowTransition()
    private(set) var isShown = false

    init(l10n: Localizer) {
        content = PerfCardContent(l10n: l10n)
        hosting = NSHostingView(rootView: PerfCardHost(content: content))
        hosting.sizingOptions = [.intrinsicContentSize]
        panel = CardWindow(contentRect: CGRect(x: 0, y: 0, width: PerfCardLayout.width, height: 200),
                           styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        panel.isReleasedWhenClosed = false
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.animationBehavior = .none
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient, .ignoresCycle]
        panel.setAccessibilityElement(false)
        panel.alphaValue = 0
    }

    /// Shows `model` beside `anchor`, above `window`'s level. Already shown, it moves there at once and crossfades
    /// to the new row's card (0.12 s); otherwise it fades in (0.15 s) with a 2 pt slide away from the row (Reduce
    /// Motion: the fade only).
    func show(_ model: PerfCardModel, key: String, anchor: PerfCardAnchor, above window: NSWindow?,
              reduceMotion: Bool, reduceTransparency: Bool) {
        applySurface(reduceTransparency: reduceTransparency)
        let switching = isShown && content.key != key
        content.reduceMotion = reduceMotion
        if switching {
            withAnimation(.easeOut(duration: Self.switchFade)) {
                content.key = key
                content.model = model
            }
        } else {
            content.key = key
            content.model = model
        }
        let level = max(window?.level.rawValue ?? NSWindow.Level.floating.rawValue, NSWindow.Level.floating.rawValue) + 1
        if panel.level.rawValue != level { panel.level = NSWindow.Level(rawValue: level) }
        heightLimit = anchor.visibleFrame.height - PerfCardFit.margin
        let placement = PerfCardPlacement.place(size: fittedSize(), anchor: anchor)
        if isShown {
            transition.cancel()
            panel.alphaValue = 1
            panel.setFrame(placement.frame, display: true)
            panel.invalidateShadow()
            return
        }
        isShown = true
        let start = reduceMotion ? placement.frame : placement.start
        panel.setFrame(start, display: true)
        panel.orderFrontRegardless()
        panel.invalidateShadow()
        transition.run(panel, from: start.origin, to: placement.frame.origin, alpha: (panel.alphaValue, 1),
                       duration: Self.fadeIn)
    }

    /// Follows the engine tick: new content for the same row, the window resized from its top edge.
    func update(_ model: PerfCardModel) {
        guard isShown, model != content.model else { return }
        content.model = model
        let size = fittedSize()
        guard abs(size.height - panel.frame.height) > 0.5 else { return }
        var frame = panel.frame
        frame.origin.y = frame.maxY - size.height
        frame.size = size
        panel.setFrame(frame, display: true)
        panel.invalidateShadow()
    }

    func hide(animated: Bool = true) {
        guard isShown else { return }
        isShown = false
        guard animated else {
            transition.cancel()
            panel.alphaValue = 0
            panel.orderOut(nil)
            return
        }
        let origin = panel.frame.origin
        transition.run(panel, from: origin, to: origin, alpha: (panel.alphaValue, 0), duration: Self.fadeIn) { [weak self] in
            self?.panel.orderOut(nil)
        }
    }

    /// The surface that owned the card went away: AppKit keeps a window until it is closed.
    func close() {
        isShown = false
        transition.cancel()
        panel.alphaValue = 0
        panel.close()
    }

    var frame: CGRect { panel.frame }

    /// The card at the fullest form that fits its screen: meanings go first, then the footnote.
    private func fittedSize() -> CGSize {
        var size = CGSize.zero
        for fit in PerfCardFit.allCases {
            if content.fit != fit { content.fit = fit }
            size = fittingSize()
            if size.height <= heightLimit { break }
        }
        return size
    }

    private func fittingSize() -> CGSize {
        hosting.layoutSubtreeIfNeeded()
        let height = hosting.fittingSize.height
        return CGSize(width: PerfCardLayout.width, height: max(1, height.rounded(.up)))
    }

    /// The material, rebuilt only when Reduce Transparency changes.
    private func applySurface(reduceTransparency: Bool) {
        guard self.reduceTransparency != reduceTransparency else { return }
        self.reduceTransparency = reduceTransparency
        hosting.removeFromSuperview()
        let surface: NSView
        if reduceTransparency {
            surface = SolidCardView(radius: PerfCardLayout.radius)
            surface.addSubview(hosting)
        } else if #available(macOS 26, *) {
            let glass = NSGlassEffectView()
            glass.cornerRadius = PerfCardLayout.radius
            glass.contentView = hosting
            surface = glass
        } else {
            let effect = NSVisualEffectView()
            effect.material = .popover
            effect.blendingMode = .behindWindow
            effect.state = .active
            effect.maskImage = Self.mask(radius: PerfCardLayout.radius)
            effect.addSubview(hosting)
            surface = effect
        }
        hosting.translatesAutoresizingMaskIntoConstraints = false
        if hosting.superview === surface {
            NSLayoutConstraint.activate([
                hosting.leadingAnchor.constraint(equalTo: surface.leadingAnchor),
                hosting.trailingAnchor.constraint(equalTo: surface.trailingAnchor),
                hosting.topAnchor.constraint(equalTo: surface.topAnchor),
                hosting.bottomAnchor.constraint(equalTo: surface.bottomAnchor),
            ])
        }
        panel.contentView = surface
    }

    private static func mask(radius: CGFloat) -> NSImage {
        let edge = 2 * radius + 1
        let image = NSImage(size: CGSize(width: edge, height: edge), flipped: false) { rect in
            NSColor.black.setFill()
            NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()
            return true
        }
        image.capInsets = NSEdgeInsets(top: radius, left: radius, bottom: radius, right: radius)
        image.resizingMode = .stretch
        return image
    }

    #if DEBUG
    var debugWindow: NSWindow { panel }
    #endif
}

/// The card's fade and slide, frame by frame on the window's display link (easeOut cubic), so the slide stays in step
/// with the fade; AppKit's animator does not move a borderless tooltip panel by itself.
@MainActor
private final class WindowTransition: NSObject {
    private struct Run {
        weak var window: NSWindow?
        var from: CGPoint
        var to: CGPoint
        var alpha: (from: CGFloat, to: CGFloat)
        var duration: TimeInterval
        var begin: CFTimeInterval
        var completion: (@MainActor () -> Void)?
    }

    private var current: Run?
    private var link: CADisplayLink?

    func run(_ window: NSWindow, from: CGPoint, to: CGPoint, alpha: (from: CGFloat, to: CGFloat), duration: TimeInterval,
             completion: (@MainActor () -> Void)? = nil) {
        cancel()
        window.setFrameOrigin(from)
        window.alphaValue = alpha.from
        current = Run(window: window, from: from, to: to, alpha: alpha, duration: duration, begin: CACurrentMediaTime(),
                  completion: completion)
        let link = window.displayLink(target: self, selector: #selector(step))
        link.add(to: .main, forMode: .common)
        self.link = link
    }

    func cancel() {
        link?.invalidate()
        link = nil
        current = nil
    }

    @objc private func step(_ link: CADisplayLink) {
        guard let run = current, let window = run.window else {
            cancel()
            return
        }
        let progress = min(1, (CACurrentMediaTime() - run.begin) / run.duration)
        let eased = CGFloat(PerfMotion.easeOutCubic(progress))
        window.setFrameOrigin(CGPoint(x: run.from.x + (run.to.x - run.from.x) * eased,
                                      y: run.from.y + (run.to.y - run.from.y) * eased))
        window.alphaValue = run.alpha.from + (run.alpha.to - run.alpha.from) * eased
        guard progress >= 1 else { return }
        let completion = run.completion
        cancel()
        completion?()
    }
}

/// Never key or main: the card is a tooltip.
private final class CardWindow: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// Reduce Transparency's solid card (dark #26272B, light white) with its rounded corners.
private final class SolidCardView: NSView {
    private let radius: CGFloat

    init(radius: CGFloat) {
        self.radius = radius
        super.init(frame: .zero)
        wantsLayer = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    override var wantsUpdateLayer: Bool { true }

    override func updateLayer() {
        guard let layer else { return }
        let dark = effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
        layer.backgroundColor = dark ? NSColor(srgbRed: 0x26 / 255, green: 0x27 / 255, blue: 0x2B / 255, alpha: 1).cgColor
            : NSColor.white.cgColor
        layer.cornerRadius = radius
        layer.cornerCurve = .continuous
        layer.masksToBounds = true
    }
}

/// The card inside the window: crossfades between rows, draws the 0.5 pt separator stroke over the material.
struct PerfCardHost: View {
    let content: PerfCardContent

    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        ZStack(alignment: .topLeading) {
            if let model = content.model {
                PerfCardView(model: model, l10n: content.l10n, fit: content.fit)
                    .id(content.key)
                    .transition(.opacity)
            }
        }
        .frame(width: PerfCardLayout.width, alignment: .topLeading)
        .overlay {
            RoundedRectangle(cornerRadius: PerfCardLayout.radius, style: .continuous)
                .strokeBorder(Color(nsColor: .separatorColor), lineWidth: contrast == .increased ? 1 : 0.5)
        }
        .environment(\.perfReduceMotion, content.reduceMotion)
        .environment(\.locale, content.l10n.locale)
    }
}
