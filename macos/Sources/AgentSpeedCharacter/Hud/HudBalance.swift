import Foundation

/// How the column is split between her two sides (DESIGN 2a.6, *Balance*): the left keeps the column's first shown
/// cards, the last ones continue under the models card on her right, so that both sides weigh about the same. A side's
/// weight is its cards' heights plus the gaps between them; the speed and its title chip do not count. The split is
/// chosen again only when a card appears or leaves or a side's weight changes by more than `threshold` × D, and a new
/// split must hold for a while before cards change sides (`HudWings` keeps the timing).
public struct HudBalance: Sendable {
    /// A side's weight change (× D) that chooses the split again.
    public static let threshold = 0.06

    /// The heights the split was last chosen with (0: not shown), indexed by `HudCard.rawValue`; nil chooses again.
    var reference: [Double]?
    /// The splits from the most even to the least, as chosen with `reference`.
    var order: [Int] = []
    /// A new split waiting to have held for `HudWings.spillAfter`, and for how long it has.
    var pending: Int?
    var held = 0.0
    /// Seconds between the last two changes of the split.
    var lastGap = Double.infinity
    /// Whether the order was made by weight (false below S = 96: the most cards at her left first).
    var balanced = true
    /// Last frame's heights: a card still growing or shrinking (a source fading into the 額度 card) is weighed again
    /// once it settles.
    var previous: [Double] = []

    public init() {}

    /// Each card's height as the balance weighs it: the column's shown cards, and the models card while it shows at her
    /// right; 0 for the rest.
    public static func heights(_ sizes: [(w: Double, h: Double)], shown: [Bool], models: Bool) -> [Double] {
        var heights = [Double](repeating: 0, count: HudCard.allCases.count)
        weigh(sizes, shown: shown, models: models, into: &heights)
        return heights
    }

    /// `heights` written in place (`into` holds one value per card), so a warm frame allocates nothing.
    public static func weigh(_ sizes: [(w: Double, h: Double)], shown: [Bool], models: Bool, into heights: inout [Double]) {
        for card in HudCard.allCases {
            let i = card.rawValue
            let weighed = card == .models ? models : HudCard.column.contains(card)
            heights[i] = weighed && shown[i] && sizes[i].w > 0 ? sizes[i].h : 0
        }
    }

    /// Each side's weight with the column's cards before `split` at her left and the rest under the models card.
    public static func weights(_ heights: [Double], split: Int, gap: Double) -> (left: Double, right: Double) {
        var left = 0.0, right = 0.0
        var lefts = 0, rights = 0
        for (k, card) in HudCard.column.enumerated() where heights[card.rawValue] > 0 {
            if k < split {
                left += heights[card.rawValue]
                lefts += 1
            } else {
                right += heights[card.rawValue]
                rights += 1
            }
        }
        let models = heights[HudCard.models.rawValue]
        if models > 0 {
            right += models
            rights += 1
        }
        return (left + Double(max(0, lefts - 1)) * gap, right + Double(max(0, rights - 1)) * gap)
    }

    /// The distinct splits from the most even to the least: `k` shown cards moved (the split at the first of them),
    /// ordered by `|left − right|`, a tie (within half a point) to the smaller `k`. With the column not heavier than
    /// the right side, `k = 0` comes first.
    public static func ranking(_ heights: [Double], gap: Double) -> [Int] {
        let column = HudCard.column
        var splits = [column.count]
        for k in stride(from: column.count - 1, through: 0, by: -1) where heights[column[k].rawValue] > 0 {
            splits.append(k)
        }
        let scored = splits.enumerated().map { moved, split -> (split: Int, moved: Int, spread: Double) in
            let w = weights(heights, split: split, gap: gap)
            return (split, moved, (abs(w.left - w.right) / 0.5).rounded())
        }
        return scored.sorted { ($0.spread, $0.moved) < ($1.spread, $1.moved) }.map(\.split)
    }

    /// Whether a card appeared or left, or a side's weight with `split` changed by more than `threshold` points, since
    /// the split was chosen.
    public func isStale(_ heights: [Double], split: Int, gap: Double, threshold: Double) -> Bool {
        guard let reference else { return true }
        for i in heights.indices where (reference[i] > 0) != (heights[i] > 0) { return true }
        let now = Self.weights(heights, split: split, gap: gap)
        let then = Self.weights(reference, split: split, gap: gap)
        return abs(now.left - then.left) > threshold || abs(now.right - then.right) > threshold
    }

    /// Records this frame's heights; true while any changed since the last frame.
    mutating func moving(_ heights: [Double]) -> Bool {
        guard previous.count == heights.count else {
            previous = Array(heights)
            return false
        }
        var changed = false
        for i in heights.indices {
            if abs(previous[i] - heights[i]) > 0.01 { changed = true }
            previous[i] = heights[i]
        }
        return changed
    }

    /// The split was chosen with these heights: nothing waits.
    mutating func settle(_ heights: [Double]) {
        reference = heights
        pending = nil
        held = 0
    }
}
