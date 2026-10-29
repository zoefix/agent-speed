import CoreGraphics

/// Where the score card goes (DESIGN 12, *Placement and timing*), in AppKit screen coordinates (y up).
struct PerfCardAnchor: Equatable, Sendable {
    enum Side: Sendable {
        /// The dashboard: toward the screen's centre (left of the menu bar popover).
        case towardScreenCenter
        /// Boards: the side with more room.
        case moreRoom
    }

    /// The row (or the hero's dial) the card belongs to: the card's top is level with its top.
    var row: CGRect
    /// The panel or board the card sits beside.
    var container: CGRect
    var visibleFrame: CGRect
    var side: Side
}

enum PerfCardPlacement {
    static let gap: CGFloat = 8
    static let inset: CGFloat = 8
    /// The card slides this far away from the row while it fades in.
    static let slide: CGFloat = 2

    struct Result: Equatable {
        var frame: CGRect
        /// +1 when the card sits right of the container, −1 left of it: the direction of its slide.
        var direction: CGFloat

        /// Where the slide starts: `slide` points back toward the row.
        var start: CGRect { frame.offsetBy(dx: -direction * PerfCardPlacement.slide, dy: 0) }
    }

    static func place(size: CGSize, anchor: PerfCardAnchor) -> Result {
        let visible = anchor.visibleFrame
        let container = anchor.container
        let roomLeft = container.minX - visible.minX
        let roomRight = visible.maxX - container.maxX
        let needed = size.width + gap + inset
        var right: Bool
        switch anchor.side {
        case .towardScreenCenter: right = container.midX < visible.midX
        case .moreRoom: right = roomRight >= roomLeft
        }
        // A side without room gives way to the other when that one has it.
        if right, roomRight < needed, roomLeft >= needed { right = false }
        if !right, roomLeft < needed, roomRight >= needed { right = true }
        var x = right ? container.maxX + gap : container.minX - gap - size.width
        var y = anchor.row.maxY - size.height
        x = clamp(x, visible.minX + inset, visible.maxX - inset - size.width)
        // A card taller than the screen keeps its top in view.
        y = min(visible.maxY - inset - size.height, max(y, visible.minY + inset))
        return Result(frame: CGRect(origin: CGPoint(x: x, y: y), size: size), direction: right ? 1 : -1)
    }

    /// Clamps into `[low, high]`, preferring `low` (the left edge) when the card is wider than the room.
    private static func clamp(_ value: CGFloat, _ low: CGFloat, _ high: CGFloat) -> CGFloat {
        max(low, min(value, high))
    }
}
