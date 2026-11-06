import Observation

/// Pointer and visibility state of a floating orb, written by `OrbWindowController` and read by `OrbView`; a board's
/// window keeps one for its fade when idle (`OrbWindowIdleDimmer`).
@MainActor
@Observable
final class OrbInteraction {
    var isHovered = false
    var isDragging = false
    /// False while the panel is hidden, off screen or fully occluded; the renderer pauses.
    var isVisibleOnScreen = true
}
