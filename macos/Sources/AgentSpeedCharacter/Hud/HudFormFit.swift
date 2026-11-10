import Foundation

/// Which of a card's forms fits its room (DESIGN 2a.6, 額度 *Room*): forms go from the most wanted (index 0) to the
/// least; the first that fits is taken, the narrowest when none does. Like `HudNameWidth` it narrows at once and
/// widens only after the wider form has fitted with room to spare for `widenAfter`, so the card does not change form
/// with every sway of her hair.
public struct HudFormFit: Sendable {
    public static let widenAfter = 1.2

    public private(set) var form = 0
    private var ease = 0.0
    private var primed = false

    public init() {}

    /// The first form (in the forms' order) whose width fits `room` (half a point of slack), else the narrowest.
    public static func best(widths: [Double], room: Double) -> Int {
        widths.firstIndex { $0 <= room + 0.5 } ?? narrowest(widths)
    }

    /// The narrowest form, the later one of equal widths.
    public static func narrowest(_ widths: [Double]) -> Int {
        var best = 0
        for (index, width) in widths.enumerated() where width <= widths[best] { best = index }
        return best
    }

    /// Advances one frame. `snap` takes the best form at once (the card is not on screen).
    public mutating func update(_ dt: Double, widths: [Double], room: Double, spare: Double, snap: Bool) {
        guard !widths.isEmpty else {
            form = 0
            return
        }
        let fitting = Self.best(widths: widths, room: room)
        guard primed, !snap, form < widths.count else {
            form = fitting
            ease = 0
            primed = true
            return
        }
        if widths[form] > room + 0.5 {
            // Too wide for the room: the best form that fits, at once.
            form = fitting
            ease = 0
            return
        }
        let roomy = Self.best(widths: widths, room: room - spare)
        if roomy < form, widths[roomy] <= room - spare + 0.5 {
            ease += dt
            if ease >= Self.widenAfter {
                form = roomy
                ease = 0
            }
        } else {
            ease = 0
        }
    }
}
