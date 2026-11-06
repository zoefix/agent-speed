import QuartzCore

/// "Fade when idle" (DESIGN 2), one rule for every orb and board window: after 60 s idle the window fades to 45 % of
/// its opacity over 0.6 s ease-in-out and comes back in 0.12 s on activity, hover or a drag; with Reduce Motion both
/// are cuts.
enum IdleFade {
    static let alpha: CGFloat = 0.45
    static let idleDelay: TimeInterval = 60
    /// Fades again this long after the pointer leaves a window that has been idle for a while.
    static let leaveDelay: TimeInterval = 1.5

    struct Motion: Equatable {
        let duration: TimeInterval
        let timing: CAMediaTimingFunctionName
    }

    static func motion(dimming: Bool, reduceMotion: Bool) -> Motion {
        if reduceMotion { return Motion(duration: 0, timing: .linear) }
        return dimming ? Motion(duration: 0.6, timing: .easeInEaseOut) : Motion(duration: 0.12, timing: .easeOut)
    }

    /// When the window fades: nil while it must not (switched off, active, or the pointer on it), else the moment it is
    /// due — 60 s after the last activity and after the window was set up, and 1.5 s after the pointer left.
    static func due(
        enabled: Bool,
        idle: Bool,
        engaged: Bool,
        lastActive: TimeInterval,
        armedAt: TimeInterval,
        pointerLeftAt: TimeInterval
    ) -> TimeInterval? {
        guard enabled, idle, !engaged else { return nil }
        return max(lastActive + idleDelay, armedAt + idleDelay, pointerLeftAt + leaveDelay)
    }
}
