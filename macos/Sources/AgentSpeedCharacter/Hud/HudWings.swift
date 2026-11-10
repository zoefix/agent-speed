import Foundation

/// The cards of the character's floating readout (DESIGN 2a.6, *Readouts*): the speed on her right with the running
/// models under it, the model above her head, and the column of facts on her left, top to bottom.
public enum HudCard: Int, Sendable, CaseIterable {
    case speed, model, performance, network, cost, agents, models

    /// The left column, top to bottom.
    public static let column: [HudCard] = [.performance, .network, .cost, .agents]
}

/// Her outline this frame as horizontal bands of `step` points from `top`: each band's leftmost and rightmost x of her
/// drawn parts. Built by the renderer from the skinned vertices, so the cards clear an arm held out, a raised staff or
/// open wings as they move.
public struct HudSilhouette: Sendable {
    public let top: Double
    public let step: Double
    public private(set) var left: [Double]
    public private(set) var right: [Double]

    public init(top: Double, bottom: Double, step: Double) {
        self.top = top
        self.step = max(step, 1)
        let count = max(1, Int(((bottom - top) / self.step).rounded(.up)))
        left = [Double](repeating: .infinity, count: count)
        right = [Double](repeating: -.infinity, count: count)
    }

    /// Empties every band, keeping the storage.
    public mutating func clear() {
        for i in left.indices {
            left[i] = .infinity
            right[i] = -.infinity
        }
    }

    /// Adds a point of her outline (any vertex of a drawn part).
    public mutating func add(_ x: Double, _ y: Double) {
        let i = Int(((y - top) / step).rounded(.down))
        guard i >= 0, i < left.count else { return }
        if x < left[i] { left[i] = x }
        if x > right[i] { right[i] = x }
    }

    /// Adds a segment, so a long triangle edge marks every band it crosses.
    public mutating func add(_ x0: Double, _ y0: Double, _ x1: Double, _ y1: Double) {
        let n = max(1, Int((abs(y1 - y0) / step).rounded(.up)))
        for k in 0...n {
            let t = Double(k) / Double(n)
            add(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t)
        }
    }

    /// Her leftmost and rightmost x over the bands that `y0…y1` touches, nil when she has nothing there.
    public func extent(_ y0: Double, _ y1: Double) -> (left: Double, right: Double)? {
        let a = max(0, Int(((min(y0, y1) - top) / step).rounded(.down)))
        let b = min(left.count - 1, Int(((max(y0, y1) - top) / step).rounded(.down)))
        guard a <= b else { return nil }
        var l = Double.infinity, r = -Double.infinity
        for i in a...b {
            l = min(l, left[i])
            r = max(r, right[i])
        }
        return l <= r ? (l, r) : nil
    }
}

/// What the cards are laid out around this frame, in window points (y down).
public struct HudFrame: Sendable {
    /// The area the cards stay inside (the window less its margin).
    public var bounds: (minX: Double, minY: Double, maxX: Double, maxY: Double)
    public var silhouette: HudSilhouette
    /// The footprint `D` (2 S): every distance of the layout is a share of it.
    public var footprint: Double
    /// Her center line, the top of her head and her chest (the speed's row) and shoulders (the column's top).
    public var centerX: Double
    public var headTop: Double
    public var chest: Double
    public var shoulders: Double

    public init(bounds: (Double, Double, Double, Double), silhouette: HudSilhouette, footprint: Double,
                centerX: Double, headTop: Double, chest: Double, shoulders: Double) {
        self.bounds = bounds
        self.silhouette = silhouette
        self.footprint = footprint
        self.centerX = centerX
        self.headTop = headTop
        self.chest = chest
        self.shoulders = shoulders
    }

    public init(bounds: (Double, Double, Double, Double), silhouette: HudSilhouette, footprint: Double, anchors: HudAnchors) {
        self.init(bounds: bounds, silhouette: silhouette, footprint: footprint, centerX: anchors.centerX,
                  headTop: anchors.headTop, chest: anchors.chest, shoulders: anchors.shoulders)
    }
}

/// Places the readout cards beside her (DESIGN 2a.6): each has its own anchor — the speed at her right at chest
/// height, the model above her head, the facts in a column at her left from her shoulders down — kept clear of her
/// outline, never behind her. Anchors trail her on soft springs, and every card floats on its own slow figure of
/// eight, bobs, tilts and breathes, so the readout drifts like moons beside her while each value stays where the
/// eye left it. A card that shows or hides fades and rises or sinks; the column closes up around it. When her left
/// side has no room for the whole column (wide hair, a step to the left), its last cards continue under the speed on
/// her right; a card changing sides fades out and back in at its new place instead of crossing her. The models card
/// sits directly under the speed on its left edge (further right only where her outline under the speed needs it),
/// the spill-over under it; when her right side is crowded there it moves below the spill-over (up to 0.3 D down)
/// before it hides.
public struct HudWings: Sendable {
    /// Where the models card sits: directly under the speed, further down below the spill-over, or nowhere.
    public enum ModelsPlace: Int, Sendable {
        case under, below, hidden
    }

    /// Where a card is drawn this frame: its center, scale, tilt (radians, clockwise on screen) and opacity.
    public struct Place: Sendable, Equatable {
        public var x, y, scale, spin, alpha: Double

        public init(x: Double, y: Double, scale: Double, spin: Double, alpha: Double) {
            self.x = x
            self.y = y
            self.scale = scale
            self.spin = spin
            self.alpha = alpha
        }
    }

    /// Shares of the footprint `D`.
    public static let gap = 0.035
    public static let margin = 0.025
    public static let columnGap = 0.022
    public static let headGap = 0.03
    /// Float amplitudes (x, y) of the speed and of the other cards.
    public static let speedFloat = (0.008, 0.011)
    public static let cardFloat = (0.013, 0.017)
    /// Seconds to fade in and out, and to fade out and back in when a card changes sides.
    public static let fadeIn = 0.35
    public static let fadeOut = 0.5
    public static let hopOut = 0.18
    public static let hopIn = 0.3
    /// Seconds the column must overflow before cards move to her right, and fit again with room to spare before they
    /// come back, so a passing gesture moves nothing.
    public static let spillAfter = 0.6
    public static let returnAfter = 4.0
    /// Within `settleWindow` s of a change, cards wait `settledReturn` s before coming back, so a dance that keeps
    /// reaching out does not send them back and forth.
    public static let settleWindow = 30.0
    public static let settledReturn = 12.0
    /// Time constant (s) of a card getting out of her way.
    static let dodge = 0.06
    /// How far (× D) the column may move from her shoulders: before spilling, and at most.
    static let nearReach = 0.12
    static let reach = 0.3
    /// Vertical offsets tried, in shares of `D`, when a card or the column does not fit beside her at its row.
    static let shifts: [Double] = [0, -0.04, 0.04, -0.08, 0.08, -0.12, 0.12, 0.17, -0.17, 0.23, 0.3]

    public private(set) var places: [Place]
    /// Shown share of each card, 0…1 (eased into the opacity and the column's spacing).
    public private(set) var presence: [Double]
    /// Each card's size this frame, eased toward the size it was given so a plate grows and shrinks instead of
    /// jumping when its text changes (points).
    public private(set) var sizes: [(w: Double, h: Double)]
    /// No float, tilt or breathing (Reduce Motion, effects Off): the cards still keep clear of her.
    public var still = false

    /// A card is fading in or out or changing sides (frames are needed even when nothing else moves).
    public var isFading: Bool {
        presence.contains { $0 > 0 && $0 < 1 } || hop.contains { $0 < 1 }
    }

    /// How many cards of the column (in its order) stay at her left; the rest continue under the speed.
    public private(set) var split = HudCard.column.count
    public private(set) var modelsPlace = ModelsPlace.under

    /// The card is drawn at her right (the speed, the models card and column cards that spilled over).
    public func isAtHerRight(_ card: HudCard) -> Bool { card == .speed || card == .models || right[card.rawValue] }

    var anchors: [(x: SecondOrder, y: SecondOrder)]
    var growth: [(w: SecondOrder, h: SecondOrder)]
    var targets: [(x: Double, y: Double)]
    /// Each card's side as drawn (true: her right) and its opacity while it changes sides (1 settled).
    var right: [Bool]
    var hop: [Double]
    var strain = 0.0
    var ease = 0.0
    var sinceChange = Double.infinity
    /// No split fits with one edge: each card hugs her outline.
    var hugging = false
    var placed = false
    /// Seconds the models card's place has not fitted, or a better place has; its top below the spill-over; and
    /// where the right side's cards end (the bottom of the last one plus the gap), from the last layout.
    var modelsStrain = 0.0
    var modelsEase = 0.0
    var modelsBelow: Double?
    var rightEnd = 0.0
    /// Cards fading out to reappear at a new place on the same side (the models card and the spill-over when the
    /// models card comes or goes under the speed), so they never slide through each other.
    var jumping: [Bool]

    public init() {
        let count = HudCard.allCases.count
        places = [Place](repeating: Place(x: 0, y: 0, scale: 1, spin: 0, alpha: 0), count: count)
        presence = [Double](repeating: 0, count: count)
        sizes = [(w: Double, h: Double)](repeating: (0, 0), count: count)
        anchors = (0..<count).map { _ in (SecondOrder(0.9, 0.78, 0), SecondOrder(0.9, 0.78, 0)) }
        growth = (0..<count).map { _ in (SecondOrder(3.2, 1, 0), SecondOrder(3.2, 1, 0)) }
        targets = [(x: Double, y: Double)](repeating: (0, 0), count: count)
        right = [Bool](repeating: false, count: count)
        hop = [Double](repeating: 1, count: count)
        jumping = [Bool](repeating: false, count: count)
    }

    /// Advances the layout one frame. `sizes` are the cards' current sizes in points (width, height) and `shown` which
    /// cards should show (switched on and not quiet), both indexed by `HudCard.rawValue`.
    public mutating func update(_ dt: Double, time: Double, frame f: HudFrame, sizes given: [(w: Double, h: Double)],
                                shown: [Bool]) {
        for i in presence.indices {
            let hidden = i == HudCard.models.rawValue && modelsPlace == .hidden
            let target = shown[i] && !hidden ? 1.0 : 0.0
            let rate = dt / (target > presence[i] ? HudWings.fadeIn : HudWings.fadeOut)
            presence[i] = target > presence[i] ? min(target, presence[i] + rate) : max(target, presence[i] - rate)
            if !placed { presence[i] = target }
            // A card coming in takes its size at once; a shown one eases to a new size.
            if !placed || presence[i] == 0 || still {
                growth[i].w.snap(given[i].w)
                growth[i].h.snap(given[i].h)
            }
            sizes[i] = (growth[i].w.update(dt, given[i].w), growth[i].h.update(dt, given[i].h))
        }
        targets[HudCard.speed.rawValue] = speedAnchor(f, sizes[HudCard.speed.rawValue])
        targets[HudCard.model.rawValue] = modelAnchor(f, sizes[HudCard.model.rawValue])
        columnAnchors(dt, f, sizes)
        modelsAnchor(dt, f, sizes, shown: shown[HudCard.models.rawValue])

        let d = f.footprint
        for card in HudCard.allCases {
            let i = card.rawValue
            if !placed || presence[i] == 0 && (!shown[i] || card == .models && modelsPlace == .hidden) {
                anchors[i].x.snap(targets[i].x)
                anchors[i].y.snap(targets[i].y)
            }
            var ax = anchors[i].x.update(dt, targets[i].x)
            var ay = anchors[i].y.update(dt, targets[i].y)
            // A card she reaches into gets out of her way at once; it drifts back on the slow spring.
            if card != .model, placed, intrudes(card, x: ax, y: ay, f) {
                let k = 1 - exp(-dt / HudWings.dodge)
                ax += (targets[i].x - ax) * k
                ay += (targets[i].y - ay) * k
                anchors[i].x.snap(ax)
                anchors[i].y.snap(ay)
            }
            let shownShare = presence[i] * presence[i] * (3 - 2 * presence[i])
            let hopShare = hop[i] * hop[i] * (3 - 2 * hop[i])
            let ease = shownShare * hopShare
            var place = Place(x: ax, y: ay + (1 - shownShare) * 0.02 * d, scale: 0.96 + 0.04 * ease, spin: 0, alpha: ease)
            if !still {
                let float = HudWings.float(card, time: time)
                place.x += d * float.dx
                place.y += d * float.dy
                place.spin = float.spin
                place.scale *= float.scale
            }
            places[i] = place
        }
        placed = true
    }

    /// Whether the card at `x, y` reaches into her outline (closer than half the gap on the side facing her).
    func intrudes(_ card: HudCard, x: Double, y: Double, _ f: HudFrame) -> Bool {
        let (w, h) = sizes[card.rawValue]
        guard w > 0, let e = f.silhouette.extent(y - h / 2, y + h / 2) else { return false }
        let room = 0.5 * HudWings.gap * f.footprint
        return isAtHerRight(card) ? x - w / 2 < e.right + room : x + w / 2 > e.left - room
    }

    /// `sin(2π t / period + phase)`.
    static func wave(_ t: Double, _ period: Double, _ phase: Double) -> Double {
        sin(2 * Double.pi * t / period + phase)
    }

    /// A card's float at time `t`: offset in shares of `D`, tilt in radians and breathing scale. Each card has its
    /// own periods and phase, so no two move in step; two sines per axis trace a slow figure of eight.
    public static func float(_ card: HudCard, time t: Double) -> (dx: Double, dy: Double, spin: Double, scale: Double) {
        let k = Double(card.rawValue)
        let phase: Double = 1.7 * k + 0.4
        let (ax, ay) = card == .speed ? speedFloat : cardFloat
        let x1: Double = 0.65 * wave(t, 7.3 + 0.9 * k, phase)
        let x2: Double = 0.35 * wave(t, 3.1 + 0.4 * k, 2.1 * phase)
        let y1: Double = 0.7 * wave(t, 5.9 + 0.7 * k, 1.3 * phase)
        let y2: Double = 0.3 * wave(t, 2.6 + 0.3 * k, 0.6 * phase)
        let tilt: Double = card == .speed ? 0.6 : 1.0
        let s1: Double = 0.032 * wave(t, 11.0 + k, phase)
        let s2: Double = 0.014 * wave(t, 4.7 + 0.5 * k, 2 * phase)
        let breath: Double = 0.012 * wave(t, 6.1 + 0.6 * k, 0.8 * phase)
        return (ax * (x1 + x2), ay * (y1 + y2), tilt * (s1 + s2), 1 + breath)
    }

    /// The speed: its left edge clear of her right side at chest height (or the nearest row where it fits), its right
    /// edge inside the bounds.
    func speedAnchor(_ f: HudFrame, _ size: (w: Double, h: Double)) -> (x: Double, y: Double) {
        let d = f.footprint
        func at(_ shift: Double) -> (x: Double, y: Double, over: Double) {
            let y = f.chest + shift * d
            let side = f.silhouette.extent(y - size.h / 2, y + size.h / 2)?.right ?? f.centerX
            let x = max(side + HudWings.gap * d, f.centerX + 0.04 * d) + size.w / 2
            return (x, y, max(0, x + size.w / 2 - (f.bounds.maxX - HudWings.margin * d)))
        }
        // The chest row unless the speed would overlap her there; then the nearest row where it overlaps least.
        var best = at(0)
        for shift in HudWings.shifts.dropFirst() where best.over > 0.5 {
            let candidate = at(shift)
            if candidate.over < best.over - 0.5 { best = candidate }
        }
        let x = min(best.x, f.bounds.maxX - HudWings.margin * d - size.w / 2)
        let y = min(max(best.y, f.bounds.minY + HudWings.margin * d + size.h / 2),
                    f.bounds.maxY - HudWings.margin * d - size.h / 2)
        return (x, y)
    }

    /// The model: centered over her head, clear of its top (and of a raised hand or prop above it).
    func modelAnchor(_ f: HudFrame, _ size: (w: Double, h: Double)) -> (x: Double, y: Double) {
        let d = f.footprint
        var bottom = f.headTop - HudWings.headGap * d
        let x = min(max(f.centerX, f.bounds.minX + HudWings.margin * d + size.w / 2),
                    f.bounds.maxX - HudWings.margin * d - size.w / 2)
        // Whatever of her reaches above her head under the chip (a raised arm, a staff) pushes it up.
        if let e = f.silhouette.extent(bottom - size.h, bottom), e.right > x - size.w / 2, e.left < x + size.w / 2 {
            var y = bottom - size.h
            while y > f.bounds.minY, let r = f.silhouette.extent(y - size.h, y), r.right > x - size.w / 2,
                  r.left < x + size.w / 2 {
                y -= f.silhouette.step
            }
            bottom = y - HudWings.gap * d * 0.5
        }
        let y = max(bottom - size.h / 2, f.bounds.minY + HudWings.margin * d + size.h / 2)
        return (x, y)
    }

    /// The models card's top directly under the speed (its title chip), and its left edge: the speed's.
    func modelsSlot(_ f: HudFrame, _ sizes: [(w: Double, h: Double)]) -> (top: Double, left: Double) {
        let speed = targets[HudCard.speed.rawValue], size = sizes[HudCard.speed.rawValue]
        return (speed.y + size.h / 2 + HudWings.columnGap * f.footprint, speed.x - size.w / 2)
    }

    /// The models card's left edge with its top at `top`, nil where it does not fit. It keeps the speed's line, moving
    /// right only as far as her outline over its rows needs (the gap clear of it, as every card), and fits while it
    /// stays inside the bounds at least half the gap clear of her. `spare` widens the card for the check, as the
    /// column's room to spare.
    func modelsLeft(_ f: HudFrame, _ size: (w: Double, h: Double), top: Double, line: Double, spare: Double = 0) -> Double? {
        let d = f.footprint
        let maxX = f.bounds.maxX - HudWings.margin * d, maxY = f.bounds.maxY - HudWings.margin * d
        guard top + size.h <= maxY + 0.5 else { return nil }
        guard let e = f.silhouette.extent(top, top + size.h) else { return min(line, maxX - size.w) }
        let wanted = max(line, e.right + HudWings.gap * d)
        guard min(wanted, maxX - size.w - spare) >= e.right + 0.5 * HudWings.gap * d - 0.5 else { return nil }
        return min(wanted, maxX - size.w)
    }

    func modelsFit(_ f: HudFrame, _ size: (w: Double, h: Double), top: Double, left: Double, spare: Double = 0) -> Bool {
        modelsLeft(f, size, top: top, line: left, spare: spare) != nil
    }

    /// The nearest top below the spill-over (from `start`, within 0.3 D of the card's own place) where the models card
    /// fits; the current one while it still fits there.
    func modelsBelowTop(_ f: HudFrame, _ size: (w: Double, h: Double), slot: (top: Double, left: Double), start: Double,
                        spare: Double) -> Double? {
        let d = f.footprint
        let limit = start + HudWings.reach * d + 1e-9
        if spare == 0, let current = modelsBelow, current >= start - 0.5, current <= limit,
           modelsFit(f, size, top: current, left: slot.left) {
            return current
        }
        var top = start
        while top <= limit {
            if modelsFit(f, size, top: top, left: slot.left, spare: spare) { return top }
            top += 0.02 * d
        }
        return nil
    }

    /// The models card's place (DESIGN 2a.6, *Models*): directly under the speed while it fits there, else below the
    /// spill-over cards' room, as near as fits within 0.3 D, else hidden. Like the column, a worse place is taken once
    /// the current one has not fitted for `spillAfter`, and a better one once it has fitted with 0.04 D to spare for
    /// `returnAfter`, so a passing gesture moves nothing. A change under the speed fades the card and the spill-over
    /// out and back in at their new places. Writes its target.
    mutating func modelsAnchor(_ dt: Double, _ f: HudFrame, _ sizes: [(w: Double, h: Double)], shown: Bool) {
        let i = HudCard.models.rawValue
        let size = sizes[i]
        let d = f.footprint
        let slot = modelsSlot(f, sizes)
        guard size.w > 0, size.h > 0 else {
            modelsPlace = .under
            modelsStrain = 0
            modelsEase = 0
            targets[i] = (slot.left, slot.top)
            return
        }
        let gap = HudWings.columnGap * d
        let spare = 0.04 * d
        // Where the spill-over ends without the models card over it.
        let spillEnd = rightEnd - (modelsPlace == .under ? presence[i] * (size.h + gap) : 0)
        let start = max(slot.top, spillEnd)
        let fitsUnder = modelsFit(f, size, top: slot.top, left: slot.left)
        modelsBelow = modelsBelowTop(f, size, slot: slot, start: start, spare: 0)
        let now: ModelsPlace = fitsUnder ? .under : modelsBelow != nil ? .below : .hidden
        let previous = modelsPlace
        if !placed || still || !shown {
            modelsPlace = now
            modelsStrain = 0
            modelsEase = 0
        } else if now.rawValue > modelsPlace.rawValue {
            modelsEase = 0
            modelsStrain += dt
            if modelsStrain >= HudWings.spillAfter {
                modelsPlace = now
                modelsStrain = 0
            }
        } else if modelsPlace != .under {
            // Back only to a better place that fits with room to spare, after a while.
            let roomy: ModelsPlace? = modelsFit(f, size, top: slot.top, left: slot.left, spare: spare) ? .under
                : modelsPlace == .hidden && modelsBelowTop(f, size, slot: slot, start: start, spare: spare) != nil ? .below
                : nil
            modelsStrain = 0
            if let roomy {
                modelsEase += dt
                if modelsEase >= HudWings.returnAfter {
                    modelsPlace = roomy
                    modelsEase = 0
                }
            } else {
                modelsEase = 0
            }
        } else {
            modelsStrain = 0
            modelsEase = 0
        }
        if modelsPlace != previous, placed {
            if !still, shown, previous == .under || modelsPlace == .under {
                if previous != .hidden, modelsPlace != .hidden { jumping[i] = true }
                for card in HudCard.column[split...] where presence[card.rawValue] > 0 { jumping[card.rawValue] = true }
            }
        }
        if jumping[i] {
            hop[i] = max(0, hop[i] - dt / HudWings.hopOut)
        } else {
            hop[i] = still ? 1 : min(1, hop[i] + dt / HudWings.hopIn)
        }
        let maxX = f.bounds.maxX - HudWings.margin * d
        func x(_ top: Double) -> Double {
            (modelsLeft(f, size, top: top, line: slot.left) ?? min(slot.left, maxX - size.w)) + size.w / 2
        }
        switch modelsPlace {
        case .under: targets[i] = (x(slot.top), slot.top + size.h / 2)
        case .below:
            let top = modelsBelow ?? start
            targets[i] = (x(top), top + size.h / 2)
        case .hidden: break
        }
        if jumping[i], hop[i] == 0 {
            jumping[i] = false
            anchors[i].x.snap(targets[i].x)
            anchors[i].y.snap(targets[i].y)
        }
    }

    /// The column: cards stacked from her shoulders down at her left (the shown share of each card taking its room),
    /// their right edges on one line clear of her outline at every row the column spans; the column moves up or down
    /// to the offset where it fits best. Cards from `split` on stack under the speed at her right, their left edges on
    /// one line. The split follows the room with hysteresis (`spillAfter`, `returnAfter`); a card whose side changed
    /// fades out where it was, jumps, and fades back in. Writes the column's `targets`.
    mutating func columnAnchors(_ dt: Double, _ f: HudFrame, _ sizes: [(w: Double, h: Double)]) {
        let count = HudCard.column.count
        let before = split
        sinceChange += dt
        defer { if placed, split != before { sinceChange = 0 } }
        if !placed || still {
            (split, hugging) = best(f, sizes)
            strain = 0
            ease = 0
        } else if fit(f, sizes, split: split, room: 0, reach: HudWings.reach, aligned: true).over > 0.5 {
            ease = 0
            strain += dt
            if strain >= HudWings.spillAfter {
                (split, hugging) = best(f, sizes)
                strain = 0
            }
        } else if let back = roomier(f, sizes) {
            strain = 0
            ease += dt
            if ease >= (sinceChange < HudWings.settleWindow ? HudWings.settledReturn : HudWings.returnAfter) {
                split = back
                hugging = false
                ease = 0
            }
        } else {
            strain = 0
            ease = 0
            hugging = false
        }
        let chosen = fit(f, sizes, split: split, room: 0, reach: HudWings.reach, aligned: !hugging)
        layout(f, sizes, split: split, shift: chosen.shift, aligned: !hugging, room: 0, write: true)
        for (k, card) in HudCard.column.enumerated() {
            let i = card.rawValue
            let wanted = k >= split
            if jumping[i], wanted == right[i], !still {
                hop[i] = max(0, hop[i] - dt / HudWings.hopOut)
                if hop[i] == 0 {
                    jumping[i] = false
                    anchors[i].x.snap(targets[i].x)
                    anchors[i].y.snap(targets[i].y)
                }
            } else if wanted == right[i] {
                jumping[i] = false
                hop[i] = still ? 1 : min(1, hop[i] + dt / HudWings.hopIn)
            } else if presence[i] == 0 || still || !placed {
                right[i] = wanted
                jumping[i] = false
                hop[i] = 1
                anchors[i].x.snap(targets[i].x)
                anchors[i].y.snap(targets[i].y)
            } else {
                hop[i] = max(0, hop[i] - dt / HudWings.hopOut)
                if hop[i] == 0 {
                    right[i] = wanted
                    jumping[i] = false
                    anchors[i].x.snap(targets[i].x)
                    anchors[i].y.snap(targets[i].y)
                }
            }
        }
    }

    /// The most cards at her left, more than now, whose column fits near her shoulders with room to spare; nil when
    /// none does.
    mutating func roomier(_ f: HudFrame, _ sizes: [(w: Double, h: Double)]) -> Int? {
        guard split < HudCard.column.count else { return nil }
        for k in stride(from: HudCard.column.count, to: split, by: -1)
        where fit(f, sizes, split: k, room: 0.04 * f.footprint, reach: HudWings.nearReach, aligned: true).over == 0 {
            return k
        }
        return nil
    }

    /// The most cards at her left whose column fits with its right edges on one line, near her shoulders first
    /// (`nearReach`), then anywhere in `reach`; when none fits, each card hugging her outline where it overflows least.
    mutating func best(_ f: HudFrame, _ sizes: [(w: Double, h: Double)]) -> (split: Int, hugging: Bool) {
        let count = HudCard.column.count
        for reach in [HudWings.nearReach, HudWings.reach] {
            for split in stride(from: count, through: 0, by: -1)
            where fit(f, sizes, split: split, room: 0, reach: reach, aligned: true).over <= 0.5 {
                return (split, false)
            }
        }
        var best = (split: count, over: Double.infinity)
        for split in stride(from: count, through: 0, by: -1) {
            let over = fit(f, sizes, split: split, room: 0, reach: HudWings.reach, aligned: false).over
            if over < best.over - 0.5 { best = (split, over) }
        }
        return (best.split, true)
    }

    /// The shift (within `reach` × D of her shoulders) where the split's column overflows least, and that overflow.
    /// `room` widens every card for the check.
    mutating func fit(_ f: HudFrame, _ sizes: [(w: Double, h: Double)], split: Int, room: Double, reach: Double,
                      aligned: Bool) -> (shift: Double, over: Double) {
        var best = (shift: 0.0, over: Double.infinity)
        for shift in HudWings.shifts where abs(shift) <= reach + 1e-9 {
            let over = layout(f, sizes, split: split, shift: shift, aligned: aligned, room: room, write: false)
            if over < best.over - 0.5 { best = (shift, over) }
            if best.over <= 0.5 { break }
        }
        return best
    }

    /// Lays the column out and returns how far its shown cards overflow the bounds (points, presence weighted);
    /// writes `targets` when `write`. The left column starts at her shoulders moved by `shift` (× D); the right one
    /// under the speed.
    @discardableResult
    mutating func layout(_ f: HudFrame, _ sizes: [(w: Double, h: Double)], split: Int, shift: Double, aligned: Bool,
                         room: Double, write: Bool) -> Double {
        let d = f.footprint
        let gap = HudWings.columnGap * d
        let cards = HudCard.column
        let minX = f.bounds.minX + HudWings.margin * d, maxX = f.bounds.maxX - HudWings.margin * d
        let maxY = f.bounds.maxY - HudWings.margin * d
        var over = 0.0

        // Her left: one right edge for the whole column.
        var total = -gap
        for k in 0..<split { total += presence[cards[k].rawValue] * (sizes[cards[k].rawValue].h + gap) }
        var top = f.shoulders + shift * d
        top = min(max(top, f.bounds.minY + HudWings.margin * d), maxY - max(0, total))
        var line = Double.infinity
        var y = top
        var edges = (0.0, 0.0, 0.0, 0.0)
        for k in 0..<split {
            let i = cards[k].rawValue
            let h = sizes[i].h
            let side = f.silhouette.extent(y, y + h)?.left ?? f.centerX
            let edge = min(side - HudWings.gap * d, f.centerX - 0.04 * d)
            HudWings.set(&edges, k, edge)
            if write { targets[i].y = y + h / 2 }
            if presence[i] > 0.01 { line = min(line, edge) }
            y += presence[i] * (h + gap)
        }
        for k in 0..<split {
            let i = cards[k].rawValue
            let w = sizes[i].w + room
            let x = (aligned && line.isFinite ? line : HudWings.get(edges, k)) - w / 2
            over += presence[i] * max(0, minX - (x - w / 2))
            if write { targets[i].x = max(x, minX + w / 2) }
        }

        // Her right, under the speed (and under the models card while it is there): one left edge.
        let speed = HudCard.speed.rawValue, models = HudCard.models.rawValue
        y = targets[speed].y + sizes[speed].h / 2 + gap
        if modelsPlace == .under, sizes[models].h > 0 { y += presence[models] * (sizes[models].h + gap) }
        if write { rightEnd = y }
        guard split < cards.count else { return over }
        var left = -Double.infinity
        for k in split..<cards.count {
            let i = cards[k].rawValue
            let h = sizes[i].h
            let side = f.silhouette.extent(y, y + h)?.right ?? f.centerX
            if presence[i] > 0.01 { left = max(left, max(side + HudWings.gap * d, f.centerX + 0.04 * d)) }
            if write { targets[i].y = y + h / 2 }
            over += presence[i] * max(0, y + h - maxY)
            y += presence[i] * (h + gap)
        }
        if write { rightEnd = y }
        if !left.isFinite { left = f.centerX + 0.04 * d }
        for k in split..<cards.count {
            let i = cards[k].rawValue
            let w = sizes[i].w + room
            over += presence[i] * max(0, left + w - maxX)
            if write { targets[i].x = min(left + w / 2, maxX - w / 2) }
        }
        return over
    }

    private static func set(_ t: inout (Double, Double, Double, Double), _ k: Int, _ v: Double) {
        switch k {
        case 0: t.0 = v
        case 1: t.1 = v
        case 2: t.2 = v
        default: t.3 = v
        }
    }

    private static func get(_ t: (Double, Double, Double, Double), _ k: Int) -> Double {
        switch k {
        case 0: t.0
        case 1: t.1
        case 2: t.2
        default: t.3
        }
    }
}
