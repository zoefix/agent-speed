import AgentSpeedOrbModel
import AppKit

/// The frames of a 性能 count-up on a board (DESIGN 2c *Frame policy*: the roll class, 60 fps for its duration, at most
/// 0.8 s, only when the score rises by 3 or more): the shared frame clock asks for one layout per frame until the
/// number lands, and nothing runs otherwise.
@MainActor
final class BoardClimbFrames: OrbFrameClient {
    private weak var window: NSWindow?
    private let onFrame: () -> Void
    private var until: TimeInterval?
    private var registered = false

    init(window: NSWindow, onFrame: @escaping () -> Void) {
        self.window = window
        self.onFrame = onFrame
    }

    var isRunning: Bool { until != nil }

    var frameRequest: OrbFrameRequest {
        guard until != nil, window?.isVisible == true else { return .none }
        return OrbFrameRequest(frameClass: .fps60, countsForCrowd: false)
    }

    var frameScreen: NSScreen? { window?.screen }

    /// Frames until `until` (a later climb extends the run).
    func run(until: TimeInterval) {
        self.until = max(until, self.until ?? until)
        if registered {
            OrbFrameClock.shared.update()
        } else {
            registered = true
            OrbFrameClock.shared.register(self)
        }
    }

    func stop() {
        until = nil
        guard registered else { return }
        registered = false
        OrbFrameClock.shared.unregister(self)
    }

    func renderFrame(at time: TimeInterval) {
        guard let until else { return }
        onFrame()
        // The landing frame has drawn the final number: leave the clock after this tick.
        if Clock.now >= until {
            DispatchQueue.main.async { [weak self] in
                MainActor.assumeIsolated {
                    guard let self, let end = self.until, Clock.now >= end else { return }
                    self.stop()
                }
            }
        }
    }
}
