import Foundation

/// How wide the models card's names are (DESIGN 2a.6, *Place and width*): their own width up to `9 t`, narrowed in
/// half-`t` steps as far as `4 t` to fit the room to the orb's edge, else none — the rows keep the dot and the speed.
/// It narrows as soon as the card no longer fits and widens again once the wider names have fitted with room to spare
/// for `widenAfter`, so the names do not change with every sway of her hair.
public struct HudNameWidth: Sendable {
    /// Shares of `t`.
    public static let widest = 9.0
    public static let narrowest = 4.0
    public static let step = 0.5
    public static let widenAfter = 1.2

    /// The names' width in points; nil drops them.
    public private(set) var width: Double?
    private var ease = 0.0
    private var primed = false

    public init() {}

    /// The widest names that fit: `room` from the card's left edge to the orb's edge, `rest` the card's width without
    /// its names, `natural` the widest name in full; nil when even `4 t` (or the names in full, if shorter) does not fit.
    public static func best(room: Double, rest: Double, natural: Double, t: Double) -> Double? {
        let full = min(natural, widest * t)
        guard full > 0 else { return nil }
        if rest + full <= room + 0.5 { return full }
        let steps = ((room - rest) / (step * t)).rounded(.down)
        let width = steps * step * t
        return width >= narrowest * t - 1e-9 ? min(width, full) : nil
    }

    /// Advances one frame. `snap` takes the best width at once (the card is not on screen).
    public mutating func update(_ dt: Double, room: Double, rest: Double, natural: Double, t: Double, spare: Double,
                                snap: Bool) {
        let fitting = Self.best(room: room, rest: rest, natural: natural, t: t)
        guard primed, !snap else {
            width = fitting
            ease = 0
            primed = true
            return
        }
        let full = min(natural, Self.widest * t)
        if let current = width, current > full + 1e-9 || rest + current > room + 0.5 {
            // Too wide for the room, or wider than its names now need: narrow at once.
            width = fitting
            ease = 0
            return
        }
        let roomy = Self.best(room: room - spare, rest: rest, natural: natural, t: t)
        if let roomy, roomy > (width ?? 0) + 1e-9 {
            ease += dt
            if ease >= Self.widenAfter {
                width = roomy
                ease = 0
            }
        } else {
            ease = 0
        }
    }
}
