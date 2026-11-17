import AppKit

/// Owns the panel's alpha: fade in/out for the visibility setting and "hide when idle", and the dimmed state of "fade
/// when idle".
@MainActor
final class OrbWindowFader {
    private weak var panel: NSWindow?
    private(set) var isShown = false
    private(set) var isDimmed = false
    /// An orb whose sources are all unavailable dims to 55 % (DESIGN 2b, Unavailable sources).
    private(set) var factor: CGFloat = 1
    private var generation = 0
    /// A window that fades with the panel at `followerFactor` of its alpha (the name plate, which also takes the orb's
    /// opacity).
    weak var follower: NSWindow? {
        didSet { follower?.alphaValue = targetAlpha * followerFactor }
    }
    var followerFactor: CGFloat = 1 {
        didSet {
            guard followerFactor != oldValue else { return }
            follower?.alphaValue = targetAlpha * followerFactor
        }
    }

    init(panel: NSWindow) {
        self.panel = panel
    }

    /// Fades in, or fades out and orders the panel out, then calls `orderedOut` (not when shown again first);
    /// `motion` defaults to 0.2 s ease-out.
    func setShown(_ shown: Bool, motion: IdleFade.Motion? = nil, orderedOut: (@MainActor () -> Void)? = nil) {
        guard shown != isShown, let panel else { return }
        isShown = shown
        let duration = motion?.duration ?? 0.2
        let timing = motion?.timing ?? .easeOut
        if shown {
            if !panel.isVisible {
                panel.alphaValue = 0
                panel.orderFrontRegardless()
            }
            fade(to: targetAlpha, duration: duration, timing: timing)
        } else {
            fade(to: 0, duration: duration, timing: timing) { [weak self] in
                guard let self, !self.isShown else { return }
                self.panel?.orderOut(nil)
                orderedOut?()
            }
        }
    }

    func setDimmed(_ dimmed: Bool, reduceMotion: Bool = false) {
        guard dimmed != isDimmed else { return }
        isDimmed = dimmed
        guard isShown else { return }
        let motion = IdleFade.motion(dimming: dimmed, reduceMotion: reduceMotion)
        fade(to: targetAlpha, duration: motion.duration, timing: motion.timing)
    }

    func setFactor(_ factor: CGFloat) {
        guard factor != self.factor else { return }
        self.factor = factor
        guard isShown else { return }
        fade(to: targetAlpha, duration: 0.2, timing: .easeOut)
    }

    /// Fades out and orders the panel out, then calls `completion` (removed and merged orbs).
    func fadeOut(duration: TimeInterval, completion: @escaping @MainActor () -> Void) {
        isShown = false
        fade(to: 0, duration: duration, timing: .easeOut) { [weak self] in
            self?.panel?.orderOut(nil)
            completion()
        }
    }

    /// Re-applies front ordering after a level or Spaces change.
    func reorderIfShown() {
        guard isShown, let panel else { return }
        panel.orderFrontRegardless()
    }

    private var targetAlpha: CGFloat { isShown ? (isDimmed ? IdleFade.alpha : 1) * factor : 0 }

    private func fade(
        to alpha: CGFloat,
        duration: TimeInterval,
        timing: CAMediaTimingFunctionName,
        completion: (@MainActor () -> Void)? = nil
    ) {
        guard let panel else { return }
        generation &+= 1
        let current = generation
        NSAnimationContext.runAnimationGroup { context in
            context.duration = duration
            context.timingFunction = CAMediaTimingFunction(name: timing)
            panel.animator().alphaValue = alpha
            follower?.animator().alphaValue = alpha * followerFactor
        } completionHandler: { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.generation == current else { return }
                completion?()
            }
        }
    }
}

/// Tells an orb or a board when it has been idle for 60 s (settings "fade when idle" and an orb's "hide when idle", per
/// window, from the window's own gauge; the rule is `IdleFade`). Event driven: one one-shot timer for the exact moment
/// it is due, recomputed whenever the tier or the pointer state changes, and on `refresh()` when a setting changes.
@MainActor
final class OrbWindowIdleDimmer {
    private let gauge: LiveGauge
    private let enabled: @MainActor () -> Bool
    private let interaction: OrbInteraction
    private let apply: (Bool) -> Void
    private let now: @MainActor () -> TimeInterval
    private var armedAt: TimeInterval
    private var pointerLeftAt: TimeInterval = -.infinity
    private var wasEngaged = false
    private var timer: Timer?
    private var loop: ObservationLoop?

    init(
        gauge: LiveGauge,
        enabled: @escaping @MainActor () -> Bool,
        interaction: OrbInteraction,
        now: @escaping @MainActor () -> TimeInterval = { Clock.now },
        apply: @escaping (Bool) -> Void
    ) {
        self.gauge = gauge
        self.enabled = enabled
        self.interaction = interaction
        self.now = now
        self.apply = apply
        armedAt = now()
        loop = ObservationLoop { [weak self] in self?.evaluate() }
    }

    func cancel() {
        loop?.cancel()
        timer?.invalidate()
        timer = nil
    }

    /// Decides again (the switch changed; the setting is not observed).
    func refresh() {
        evaluate()
    }

    /// Starts the 60 s again from now (a hidden orb shown again).
    func rearm() {
        armedAt = now()
        evaluate()
    }

    private func evaluate() {
        let enabled = enabled()
        let idle = gauge.tier == .idle
        let engaged = interaction.isHovered || interaction.isDragging
        let now = self.now()
        if engaged != wasEngaged {
            wasEngaged = engaged
            if !engaged { pointerLeftAt = now }
        }
        timer?.invalidate()
        timer = nil
        guard let due = IdleFade.due(enabled: enabled, idle: idle, engaged: engaged, lastActive: now - gauge.idleDuration(at: now),
                                     armedAt: armedAt, pointerLeftAt: pointerLeftAt) else {
            apply(false)
            return
        }
        if now >= due {
            apply(true)
            return
        }
        apply(false)
        let timer = Timer(timeInterval: due - now, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.evaluate() }
        }
        timer.tolerance = 0.1
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }
}
