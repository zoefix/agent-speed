import Foundation

/// The rows of a card whose list changes order (DESIGN 2a.6, *Models*): a row glides to its new place when the order
/// changes and fades in or out when it comes or goes, and the rows under a fading one close up with it. Rows are
/// identified by id; a row that leaves keeps its place among the others while it fades.
public struct HudRows: Sendable {
    public struct Row: Sendable, Equatable {
        public var id: String
        /// Its top in rows from the card's first row: fractional while it glides or a row above it fades.
        public var slot: Double
        /// 0…1: its opacity and its share of its room.
        public var presence: Double
        /// It is in the list (not fading out).
        public var wanted: Bool
    }

    /// Seconds a reorder glides and a row fades.
    public static let glide = 0.3
    public static let fade = 0.25

    /// The rows in their order on the card.
    public private(set) var rows: [Row] = []
    private var from: [String: Double] = [:]
    private var progress = 1.0
    private var last: [String] = []

    public init() {}

    /// The rows' room, in rows.
    public var extent: Double { rows.reduce(0) { $0 + $1.presence } }

    /// A row fades or glides (frames are needed even when nothing else moves).
    public var isMoving: Bool { progress < 1 || rows.contains { $0.presence < 1 } }

    /// Advances one frame toward `ids`, in their order. `instant` reorders at once (Reduce Motion); fades stay. Rows of
    /// a list that was empty appear at once, since their card fades in with them.
    public mutating func update(_ dt: Double, ids: [String], instant: Bool = false) {
        // At rest with the same list there is nothing to do (and nothing to allocate).
        if ids == last, !isMoving { return }
        last = ids
        let wanted = Set(ids)
        let appearing = rows.isEmpty
        let before = rows.filter(\.wanted).map(\.id)
        var merged: [Row] = ids.map { id in
            if var row = rows.first(where: { $0.id == id }) {
                row.wanted = true
                return row
            }
            return Row(id: id, slot: 0, presence: appearing ? 1 : 0, wanted: true)
        }
        // A row that leaves stays where it was among the others while it fades.
        for (index, row) in rows.enumerated() where !wanted.contains(row.id) {
            var leaving = row
            leaving.wanted = false
            merged.insert(leaving, at: min(index, merged.count))
        }
        let step = dt / Self.fade
        for k in merged.indices {
            merged[k].presence = merged[k].wanted ? min(1, merged[k].presence + step) : max(0, merged[k].presence - step)
        }
        merged.removeAll { !$0.wanted && $0.presence <= 0 }

        let staying = before.filter(wanted.contains)
        if staying != ids.filter(Set(before).contains) {
            from = Dictionary(uniqueKeysWithValues: rows.map { ($0.id, $0.slot) })
            progress = 0
        }
        progress = instant ? 1 : min(1, progress + dt / Self.glide)
        let ease = progress * progress * (3 - 2 * progress)
        var top = 0.0
        for k in merged.indices {
            let target = top
            if progress < 1, let start = from[merged[k].id] {
                merged[k].slot = start + (target - start) * ease
            } else {
                merged[k].slot = target
            }
            top += merged[k].presence
        }
        rows = merged
    }
}
