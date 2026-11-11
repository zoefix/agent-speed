import Foundation
import Testing
@testable import AgentSpeedCharacter

/// A frame like a 148 pt orb's (D = 296, window 444): her outline a slim body from y 80 to 400 around x 222, with an
/// optional arm held out to one side at chest height.
private func frame(armOut: Double = 0, armSide: Double = 1) -> HudFrame {
    var s = HudSilhouette(top: 0, bottom: 444, step: 4)
    for y in stride(from: 80.0, through: 400, by: 2) {
        s.add(190, y)
        s.add(254, y)
    }
    if armOut > 0 {
        for y in stride(from: 150.0, through: 190, by: 2) { s.add(222 + armSide * armOut, y) }
    }
    return HudFrame(bounds: (0, 0, 444, 444), silhouette: s, footprint: 296, centerX: 222, headTop: 80, chest: 170,
                    shoulders: 140)
}

/// Her outline with wide hair at her left from her head to her knees: no room for the column there.
private func wideHairFrame() -> HudFrame {
    var f = frame()
    for y in stride(from: 80.0, through: 300, by: 2) { f.silhouette.add(60, y) }
    return f
}

/// Every card's size; 額度 (last) is empty unless a test gives it one.
private let sizes: [(w: Double, h: Double)] = [(80, 40), (90, 18), (78, 34), (70, 30), (74, 16), (86, 16), (96, 30), (0, 0)]

private func overlaps(_ p: HudWings.Place, _ size: (w: Double, h: Double), _ f: HudFrame) -> Bool {
    guard let e = f.silhouette.extent(p.y - size.h / 2, p.y + size.h / 2) else { return false }
    return p.x + size.w / 2 > e.left && p.x - size.w / 2 < e.right
}

@Suite struct HudWingsTests {
    @Test(arguments: [false, true])
    func cardsSitBesideHerNeverOverHer(balances: Bool) {
        let f = frame()
        var wings = HudWings()
        wings.still = true
        wings.balances = balances
        wings.update(1 / 60, time: 0, frame: f, sizes: sizes, shown: [Bool](repeating: true, count: HudCard.allCases.count))
        for card in [HudCard.speed] + HudCard.column {
            let p = wings.places[card.rawValue]
            #expect(!overlaps(p, sizes[card.rawValue], f), "\(card)")
            #expect(p.x - sizes[card.rawValue].w / 2 >= 0 && p.x + sizes[card.rawValue].w / 2 <= 444)
        }
        #expect(wings.places[HudCard.speed.rawValue].x > 254)
        #expect(balances ? wings.split < HudCard.column.count : wings.split == HudCard.column.count)
        for card in HudCard.column {
            #expect(wings.isAtHerRight(card) ? wings.places[card.rawValue].x > 254 : wings.places[card.rawValue].x < 190)
        }
        let model = wings.places[HudCard.model.rawValue]
        #expect(abs(model.x - 222) < 1e-9 && model.y + sizes[1].h / 2 < 80)
        let ys = HudCard.column.map { wings.places[$0.rawValue].y }
        #expect(ys == ys.sorted())
    }

    @Test func theSpeedMovesOffAnArmHeldOutToItsSide() {
        let f = frame(armOut: 150)
        var wings = HudWings()
        wings.still = true
        for n in 0..<240 { wings.update(1 / 60, time: Double(n) / 60, frame: f, sizes: sizes, shown: [Bool](repeating: true, count: HudCard.allCases.count)) }
        let p = wings.places[HudCard.speed.rawValue]
        #expect(!overlaps(p, sizes[0], f))
    }

    @Test func aHiddenCardFadesAndTheColumnClosesUp() {
        let f = frame()
        var wings = HudWings()
        wings.still = true
        var shown = [Bool](repeating: true, count: HudCard.allCases.count)
        wings.update(1 / 60, time: 0, frame: f, sizes: sizes, shown: shown)
        let before = wings.places[HudCard.cost.rawValue].y
        shown[HudCard.network.rawValue] = false
        for n in 1...180 { wings.update(1 / 60, time: Double(n) / 60, frame: f, sizes: sizes, shown: shown) }
        #expect(wings.places[HudCard.network.rawValue].alpha == 0)
        #expect(wings.places[HudCard.cost.rawValue].y < before - 25)
    }

    @Test func cardsFloatUnlessStill() {
        let f = frame()
        var moving = HudWings()
        var still = HudWings()
        still.still = true
        var spread = 0.0, tilt = 0.0
        var first: Double?
        for n in 0..<600 {
            let t = Double(n) / 60
            moving.update(1 / 60, time: t, frame: f, sizes: sizes, shown: [Bool](repeating: true, count: HudCard.allCases.count))
            still.update(1 / 60, time: t, frame: f, sizes: sizes, shown: [Bool](repeating: true, count: HudCard.allCases.count))
            let p = moving.places[HudCard.network.rawValue]
            if first == nil { first = p.y }
            spread = max(spread, abs(p.y - first!))
            tilt = max(tilt, abs(p.spin))
            #expect(still.places[HudCard.network.rawValue].spin == 0)
        }
        #expect(spread > 4 && spread < 15)
        #expect(tilt > 0.02 && tilt < 0.06)
    }

    @Test func theColumnContinuesAtHerRightWhenHerLeftIsFull() {
        let f = wideHairFrame()
        var wings = HudWings()
        let shown = [Bool](repeating: true, count: HudCard.allCases.count)
        for n in 0..<240 { wings.update(1 / 60, time: Double(n) / 60, frame: f, sizes: sizes, shown: shown) }
        #expect(wings.split < HudCard.column.count)
        let speed = wings.places[HudCard.speed.rawValue]
        for card in HudCard.column {
            let p = wings.places[card.rawValue]
            #expect(p.alpha > 0.9, "\(card)")
            let w = sizes[card.rawValue].w, h = sizes[card.rawValue].h
            guard let e = f.silhouette.extent(p.y - h / 2 + 3, p.y + h / 2 - 3) else { continue }
            #expect(p.x + w / 2 < e.left + 6 || p.x - w / 2 > e.right - 6, "\(card) overlaps her")
            #expect(p.x - w / 2 >= 0 && p.x + w / 2 <= 444)
        }
        let moved = HudCard.column.filter { wings.places[$0.rawValue].x > 222 }
        #expect(!moved.isEmpty && moved.allSatisfy { wings.places[$0.rawValue].y > speed.y })
    }

    @Test func cardsComeBackOnlyAfterHerLeftStaysClear() {
        var wings = HudWings()
        wings.still = false
        wings.balances = false
        let shown = [Bool](repeating: true, count: HudCard.allCases.count)
        var t = 0.0
        for _ in 0..<120 {
            wings.update(1 / 60, time: t, frame: wideHairFrame(), sizes: sizes, shown: shown)
            t += 1 / 60
        }
        let spilled = wings.split
        #expect(spilled < HudCard.column.count)
        // A moment of room changes nothing; staying clear brings the cards back.
        for _ in 0..<30 {
            wings.update(1 / 60, time: t, frame: frame(), sizes: sizes, shown: shown)
            t += 1 / 60
        }
        #expect(wings.split == spilled)
        for _ in 0..<600 {
            wings.update(1 / 60, time: t, frame: frame(), sizes: sizes, shown: shown)
            t += 1 / 60
        }
        #expect(wings.split == HudCard.column.count)
        for card in HudCard.column { #expect(wings.places[card.rawValue].x < 190) }
    }

    @Test(arguments: [false, true])
    func aPassingGestureMovesNoCard(balances: Bool) {
        var wings = HudWings()
        wings.balances = balances
        let shown = [Bool](repeating: true, count: HudCard.allCases.count)
        var t = 0.0
        var first: Int?
        for n in 0..<300 {
            // The wide hair for 0.2 s (12 frames) of every 2 s.
            let f = n % 120 >= 60 && n % 120 < 72 ? wideHairFrame() : frame()
            wings.update(1 / 60, time: t, frame: f, sizes: sizes, shown: shown)
            t += 1 / 60
            if first == nil { first = wings.split }
            #expect(wings.split == first)
        }
        #expect(balances || first == HudCard.column.count)
    }

    /// Her outline with something held out to her right from `from` to `to` (y), reaching `reach` past her center.
    private func rightReach(from: Double, to: Double, reach: Double = 150) -> HudFrame {
        var f = frame()
        for y in stride(from: from, through: to, by: 2) { f.silhouette.add(222 + reach, y) }
        return f
    }

    @Test func theModelsCardSitsUnderTheSpeedOnItsLeftEdge() {
        let f = frame()
        var wings = HudWings()
        wings.still = true
        wings.update(1 / 60, time: 0, frame: f, sizes: sizes, shown: [Bool](repeating: true, count: HudCard.allCases.count))
        let speed = wings.places[HudCard.speed.rawValue], models = wings.places[HudCard.models.rawValue]
        let s = sizes[HudCard.speed.rawValue], m = sizes[HudCard.models.rawValue]
        #expect(wings.modelsPlace == .under && wings.isAtHerRight(.models))
        #expect(abs((models.x - m.w / 2) - (speed.x - s.w / 2)) < 1e-6)
        #expect(abs((models.y - m.h / 2) - (speed.y + s.h / 2 + 0.022 * 296)) < 1e-6)
        #expect(!overlaps(models, m, f) && models.alpha == 1)
    }

    @Test func theSpillOverContinuesUnderTheModelsCard() {
        let f = wideHairFrame()
        var wings = HudWings()
        wings.still = true
        wings.update(1 / 60, time: 0, frame: f, sizes: sizes, shown: [Bool](repeating: true, count: HudCard.allCases.count))
        #expect(wings.split < HudCard.column.count)
        let models = wings.places[HudCard.models.rawValue]
        let m = sizes[HudCard.models.rawValue]
        for card in HudCard.column[wings.split...] {
            let p = wings.places[card.rawValue]
            #expect(p.y - sizes[card.rawValue].h / 2 >= models.y + m.h / 2 + 0.022 * 296 - 1e-6, "\(card)")
        }
    }

    @Test func aCrowdedRightSideSendsTheModelsCardDownThenHidesIt() {
        let shown = [Bool](repeating: true, count: HudCard.allCases.count)
        var wings = HudWings()
        var t = 0.0
        func run(_ f: HudFrame, _ seconds: Double) {
            for _ in 0..<Int(seconds * 60) {
                wings.update(1 / 60, time: t, frame: f, sizes: sizes, shown: shown)
                t += 1 / 60
            }
        }
        run(frame(), 1)
        #expect(wings.modelsPlace == .under)
        // A staff held out under the speed: a moment changes nothing, staying sends the card below it.
        let staff = rightReach(from: 196, to: 222)
        run(staff, 0.3)
        #expect(wings.modelsPlace == .under)
        run(staff, 2)
        #expect(wings.modelsPlace == .below)
        let m = sizes[HudCard.models.rawValue]
        let target = wings.targets[HudCard.models.rawValue]
        #expect(target.y - m.h / 2 > 222 && target.y - m.h / 2 < 222 + 0.3 * 296)
        // Wings spread down her whole right side: nowhere fits, so it fades out.
        run(rightReach(from: 150, to: 444), 3)
        #expect(wings.modelsPlace == .hidden && wings.places[HudCard.models.rawValue].alpha == 0)
        #expect(wings.places[HudCard.speed.rawValue].alpha > 0.9, "the speed always shows")
        // Clear again: back under the speed once it has fitted with room to spare for 1.2 s.
        run(frame(), 1)
        #expect(wings.modelsPlace == .hidden)
        run(frame(), 0.4)
        #expect(wings.modelsPlace == .under)
        run(frame(), 1)
        #expect(wings.places[HudCard.models.rawValue].alpha > 0.9)
    }

    @Test func theModelsCardMovesBackUpFromBelowAfterFourSeconds() {
        let shown = [Bool](repeating: true, count: HudCard.allCases.count)
        var wings = HudWings()
        var t = 0.0
        func run(_ f: HudFrame, _ seconds: Double) {
            for _ in 0..<Int(seconds * 60) {
                wings.update(1 / 60, time: t, frame: f, sizes: sizes, shown: shown)
                t += 1 / 60
            }
        }
        run(frame(), 1)
        run(rightReach(from: 196, to: 222), 1)
        #expect(wings.modelsPlace == .below)
        run(frame(), 3.5)
        #expect(wings.modelsPlace == .below, "a moment of room changes nothing")
        run(frame(), 1)
        #expect(wings.modelsPlace == .under)
    }

    @Test func itsNarrowestFormDecidesWhetherItFits() {
        // A staff held out under the speed leaves 60 pt to the orb's edge: the 96 pt card does not fit, a 60 pt form
        // of it (no names) does, so it stays under the speed and reports the room its names narrow to.
        let f = rightReach(from: 196, to: 222, reach: 444 - 0.025 * 296 - 60 - 0.035 * 296 - 222)
        var wings = HudWings()
        wings.still = true
        wings.modelsNarrowest = 60
        wings.update(1 / 60, time: 0, frame: f, sizes: sizes, shown: [Bool](repeating: true, count: HudCard.allCases.count))
        #expect(wings.modelsPlace == .under)
        #expect(wings.modelsRoom < 61 && wings.modelsRoom > 59)
        var wide = HudWings()
        wide.still = true
        wide.update(1 / 60, time: 0, frame: f, sizes: sizes, shown: [Bool](repeating: true, count: HudCard.allCases.count))
        #expect(wide.modelsPlace != .under, "its own 96 pt do not fit")
    }

    @Test func namesNarrowToTheRoomThenDrop() {
        let t = 8.0
        // Rest 60 pt: names in full (50 pt) while they fit, else half-t steps down to 4 t, else none.
        #expect(HudNameWidth.best(room: 200, rest: 60, natural: 50, t: t) == 50)
        #expect(HudNameWidth.best(room: 200, rest: 60, natural: 120, t: t) == 72, "at most 9 t")
        #expect(HudNameWidth.best(room: 105, rest: 60, natural: 120, t: t) == 44)
        #expect(HudNameWidth.best(room: 92, rest: 60, natural: 120, t: t) == 32, "4 t")
        #expect(HudNameWidth.best(room: 91, rest: 60, natural: 120, t: t) == nil, "below 4 t the names go")
        #expect(HudNameWidth.best(room: 85, rest: 60, natural: 22, t: t) == 22, "short names in full")
        #expect(HudNameWidth.best(room: 80, rest: 60, natural: 22, t: t) == nil)
    }

    @Test func namesNarrowAtOnceAndWidenAfterTheyHaveFitted() {
        let t = 8.0, spare = 11.84
        var names = HudNameWidth()
        names.update(1 / 60, room: 200, rest: 60, natural: 72, t: t, spare: spare, snap: true)
        #expect(names.width == 72)
        names.update(1 / 60, room: 110, rest: 60, natural: 72, t: t, spare: spare, snap: false)
        #expect(names.width == 48, "narrows at once")
        names.update(1 / 60, room: 80, rest: 60, natural: 72, t: t, spare: spare, snap: false)
        #expect(names.width == nil, "dots and speeds only")
        for _ in 0..<60 { names.update(1 / 60, room: 200, rest: 60, natural: 72, t: t, spare: spare, snap: false) }
        #expect(names.width == nil, "a second of room is not enough")
        for _ in 0..<15 { names.update(1 / 60, room: 200, rest: 60, natural: 72, t: t, spare: spare, snap: false) }
        #expect(names.width == 72)
        // The long name's model stops: the names take their new width at once.
        names.update(1 / 60, room: 200, rest: 60, natural: 40, t: t, spare: spare, snap: false)
        #expect(names.width == 40)
    }

    @Test func theModelsCardStepsRightOfHerOutlineUnderTheSpeed() {
        // Her sleeve just past her side under the speed: the card moves right to clear it and stays under the speed.
        var f = frame()
        for y in stride(from: 196.0, through: 226, by: 2) { f.silhouette.add(222 + 40, y) }
        var wings = HudWings()
        wings.still = true
        wings.update(1 / 60, time: 0, frame: f, sizes: sizes, shown: [Bool](repeating: true, count: HudCard.allCases.count))
        let models = wings.places[HudCard.models.rawValue]
        #expect(wings.modelsPlace == .under)
        #expect(abs(models.x - sizes[HudCard.models.rawValue].w / 2 - (262 + 0.035 * 296)) < 1e-6)
    }

    @Test func theSpillOverFadesToItsNewPlaceWhenTheModelsCardLeavesTheSpeed() {
        let shown = [Bool](repeating: true, count: HudCard.allCases.count)
        var wide = sizes
        wide[HudCard.models.rawValue] = (130, 30)
        var wings = HudWings()
        var t = 0.0
        var dimmed = false
        let crowded = wideHairFrame()
        // A sleeve held out under the speed: the spill-over still clears it, the wide models card does not.
        var f = crowded
        for y in stride(from: 196.0, through: 226, by: 2) { f.silhouette.add(222 + 90, y) }
        for n in 0..<360 {
            wings.update(1 / 60, time: t, frame: n < 120 ? crowded : f, sizes: wide, shown: shown)
            t += 1 / 60
            if n >= 120, let card = HudCard.column[wings.split...].first, wings.places[card.rawValue].alpha < 0.2 {
                dimmed = true
            }
        }
        #expect(wings.split < HudCard.column.count && wings.modelsPlace == .below)
        #expect(dimmed, "the spill-over fades out and back in instead of sliding through the models card")
        let models = wings.places[HudCard.models.rawValue]
        for card in HudCard.column[wings.split...] {
            let p = wings.places[card.rawValue]
            #expect(p.y + wide[card.rawValue].h / 2 <= models.y - wide[HudCard.models.rawValue].h / 2 + 8, "\(card)")
        }
    }

    @Test func rowsGlideToANewOrderAndCloseUpAsOneFades() {
        var rows = HudRows()
        rows.update(1 / 60, ids: ["a", "b", "c"])
        #expect(rows.rows.map(\.slot) == [0, 1, 2] && rows.rows.allSatisfy { $0.presence == 1 })
        rows.update(0.15, ids: ["c", "a", "b"])
        let c = rows.rows.first { $0.id == "c" }!
        #expect(c.slot > 0.2 && c.slot < 1.8, "c glides up")
        rows.update(0.2, ids: ["c", "a", "b"])
        #expect(rows.rows.map(\.id) == ["c", "a", "b"] && rows.rows.map(\.slot) == [0, 1, 2])
        // b stops running: it fades out of its row while c and a stay.
        rows.update(0.1, ids: ["c", "a"])
        let b = rows.rows.first { $0.id == "b" }!
        #expect(!b.wanted && b.presence > 0.5 && b.presence < 0.7)
        #expect(abs(rows.extent - (2 + b.presence)) < 1e-9)
        rows.update(0.2, ids: ["c", "a"])
        #expect(rows.rows.map(\.id) == ["c", "a"] && !rows.isMoving)
        // A new model fades in at its place and pushes the rows under it down with its presence.
        rows.update(0.1, ids: ["d", "c", "a"])
        #expect(rows.rows.first!.id == "d" && rows.rows.first!.presence < 0.5)
        #expect(abs(rows.rows[1].slot - rows.rows[0].presence) < 1e-9)
    }

    @Test func reduceMotionReordersAtOnceButStillFades() {
        var rows = HudRows()
        rows.update(1 / 60, ids: ["a", "b"])
        rows.update(1 / 60, ids: ["b", "a"], instant: true)
        #expect(rows.rows.map(\.id) == ["b", "a"] && rows.rows.map(\.slot) == [0, 1])
        rows.update(1 / 60, ids: ["b"], instant: true)
        #expect(rows.rows.count == 2 && rows.rows[1].presence < 1)
    }

    @Test func floatsDifferPerCard() {
        let a = HudWings.float(.performance, time: 3)
        let b = HudWings.float(.network, time: 3)
        #expect(a.dx != b.dx && a.dy != b.dy)
    }
}

@Suite struct HudTraceTests {
    @Test func traceMarksEveryBandOfHerDrawnParts() throws {
        // The fixture rig: a 10 px square (y 20–30) over a 20 px face (y 40–60), feet at y 90, drawn at 2 pt per px.
        let rig = try TinyPack.rigJSON()
        var performer = CharacterPerformer(pack: try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig])))
        performer.step(1.0 / 60, level: 0)
        var silhouette = HudSilhouette(top: 0, bottom: 800, step: 4)
        var scratch: [Double] = []
        let anchors = performer.trace(into: &silhouette, feetOnScreen: (300, 600), pointsPerPixel: 2, scratch: &scratch)
        func marked(_ y0: Double, _ y1: Double) -> Bool {
            stride(from: y0, to: y1, by: 4).allSatisfy { silhouette.extent($0, $0 + 1) != nil }
        }
        #expect(marked(464, 476) && marked(504, 536))
        #expect(silhouette.extent(486, 494) == nil)
        // Every vertex, skinned as the GPU skins it, lies inside its band.
        let active = performer.activeRig
        let k = 2 * performer.activePlacement.scale
        var skinned: [Double] = []
        for part in active.parts.indices {
            active.skin(part: part, world: performer.world, into: &skinned)
            for v in stride(from: 0, to: skinned.count, by: 2) {
                let x = 300 + (skinned[v] - performer.activePlacement.feet.x) * k
                let y = 600 + (skinned[v + 1] - performer.activePlacement.feet.y) * k
                let e = try #require(silhouette.extent(y, y))
                #expect(e.left <= x + 1e-9 && x <= e.right + 1e-9)
            }
        }
        #expect(abs(anchors.headTop - 500) < 6)
        #expect(anchors.shoulders < anchors.chest && anchors.chest < 600)
        let warm = scratch.count
        _ = performer.trace(into: &silhouette, feetOnScreen: (300, 600), pointsPerPixel: 2, scratch: &scratch)
        #expect(scratch.count == warm)
    }
}

/// `AGENT_SPEED_CHARACTER_PACKS` (a folder of unpacked packs): every pack dances through the bands for 60 s at a 148 pt
/// orb while the cards follow her outline, with the owner's cards (性能 with 首字, network, 費用, today, 額度 with three
/// sources, one models row); prints how often cards changed sides and how many card-frames covered her.
@Suite struct HudWingsPackTests {
    @Test(.enabled(if: ProcessInfo.processInfo.environment["AGENT_SPEED_CHARACTER_PACKS"] != nil))
    func cardsStayBesideEveryCharacter() throws {
        let root = URL(fileURLWithPath: ProcessInfo.processInfo.environment["AGENT_SPEED_CHARACTER_PACKS"]!)
        let ids = try FileManager.default.contentsOfDirectory(atPath: root.path).filter {
            FileManager.default.fileExists(atPath: root.appendingPathComponent("\($0)/manifest.json").path)
        }.sorted().filter { id in
            ProcessInfo.processInfo.environment["AGENT_SPEED_CHARACTER_IDS"].map { $0.split(separator: ",").contains(Substring(id)) } ?? true
        }
        let d = 296.0, side = 444.0
        var sizes: [(w: Double, h: Double)] = [(84, 62), (90, 18), (90, 38.5), (78, 26.4), (110, 18), (86, 26.4), (130, 16.3),
                                               (150, 85)]
        // 額度's forms as the renderer fits them to its room: full, no resets, short bars, no headers.
        let quotaForms: [(w: Double, h: Double)] = [(150, 85), (130, 85), (115, 85), (104, 57)]
        let shown = HudCard.allCases.map { $0 != .model }
        let levels = [2, 5, 8, 11, 13, 9, 4, 1]
        for id in ids {
            var performer = CharacterPerformer(pack: try CharacterPack.load(from: root.appendingPathComponent(id)), seed: 11)
            var wings = HudWings()
            // `AGENT_SPEED_HUD_BALANCE=0`: the spill-over alone, for comparison.
            wings.balances = ProcessInfo.processInfo.environment["AGENT_SPEED_HUD_BALANCE"] != "0"
            // The renderer's narrowest forms: 額度 without headers, short bars and resets; the models rows without names.
            wings.quotaNarrowest = 104
            wings.modelsNarrowest = 62
            var form = HudFormFit()
            var silhouette = HudSilhouette(top: 0, bottom: side, step: 2)
            var scratch: [Double] = []
            let ppp = 0.86 * d / performer.idleHeight
            var changes = 0, last = -1, covered = 0
            var coveredBy: [HudCard: Int] = [:]
            for n in 0..<3600 {
                performer.step(1.0 / 60, level: levels[min(levels.count - 1, n / 450)])
                let anchors = performer.trace(into: &silhouette, feetOnScreen: (222, 222 + 0.45 * d), pointsPerPixel: ppp,
                                              scratch: &scratch)
                let f = HudFrame(bounds: (0, 0, side, side), silhouette: silhouette, footprint: d, anchors: anchors)
                form.update(1.0 / 60, widths: quotaForms.map(\.w), room: wings.quotaRoom, spare: 0.04 * d, snap: n == 0)
                sizes[HudCard.quota.rawValue] = quotaForms[form.form]
                // The models card's names narrow to its room, down to the dots and speeds.
                sizes[HudCard.models.rawValue].w = min(130, max(wings.modelsNarrowest, wings.modelsRoom))
                wings.update(1.0 / 60, time: Double(n) / 60, frame: f, sizes: sizes, shown: shown)
                if wings.split != last {
                    changes += last < 0 ? 0 : 1
                    if last >= 0 { print("hud \(id): split \(last) → \(wings.split) at \(String(format: "%.1f", Double(n) / 60)) s") }
                    last = wings.split
                }
                for card in HudCard.column + [.speed, .models] {
                    let p = wings.places[card.rawValue]
                    let w = wings.sizes[card.rawValue].w, h = wings.sizes[card.rawValue].h
                    guard p.alpha > 0.5, let e = silhouette.extent(p.y - h / 2 + 4, p.y + h / 2 - 4) else { continue }
                    let enters = p.x < anchors.centerX ? p.x + w / 2 - e.left : e.right - (p.x - w / 2)
                    if enters > 6 {
                        covered += 1
                        coveredBy[card, default: 0] += 1
                    }
                }
            }
            print("hud \(id): \(changes) side changes in 60 s, \(covered) covered card-frames \(coveredBy), split \(wings.split)")
        }
    }
}

/// 額度 (DESIGN 2a.6): the column's last card, its room to the orb's edge, its narrowest form deciding a spill, and
/// its forms' fit with the names' hysteresis.
@Suite struct HudQuotaCardTests {
    private func sized(quota: (w: Double, h: Double)) -> [(w: Double, h: Double)] {
        var all = sizes
        all[HudCard.quota.rawValue] = quota
        return all
    }

    @Test func quotaIsTheColumnsLastCard() {
        #expect(HudCard.column.last == .quota)
        #expect(HudCard.allCases.last == .quota, "new cards go last, so the others keep their float phases")
        let f = frame()
        var wings = HudWings()
        wings.still = true
        wings.balances = false
        let all = sized(quota: (120, 60))
        wings.update(1 / 60, time: 0, frame: f, sizes: all, shown: [Bool](repeating: true, count: HudCard.allCases.count))
        let quota = wings.places[HudCard.quota.rawValue], agents = wings.places[HudCard.agents.rawValue]
        #expect(quota.y > agents.y)
        #expect(!wings.isAtHerRight(.quota))
        // Its right edge on the column's line, the room from there to the orb's edge.
        let right = quota.x + 60
        #expect(abs(right - (agents.x + all[HudCard.agents.rawValue].w / 2)) < 1e-6)
        #expect(abs(wings.quotaRoom - (right - 0.025 * 296)) < 1e-6)
        #expect(!overlaps(quota, all[HudCard.quota.rawValue], f))
    }

    @Test func anEmptyQuotaCardTakesNoRoom() {
        let f = frame()
        var with = HudWings(), without = HudWings()
        with.still = true
        without.still = true
        let shown = [Bool](repeating: true, count: HudCard.allCases.count)
        with.update(1 / 60, time: 0, frame: f, sizes: sizes, shown: shown)
        var hidden = shown
        hidden[HudCard.quota.rawValue] = false
        without.update(1 / 60, time: 0, frame: f, sizes: sizes, shown: hidden)
        for card in HudCard.column.dropLast() { #expect(with.places[card.rawValue].y == without.places[card.rawValue].y) }
    }

    @Test func itsNarrowestFormDecidesWhetherTheColumnSpills() {
        // Her left has room for 150 pt; the card's full form is 170 pt, its narrowest 120.
        var f = frame()
        for y in stride(from: 80.0, through: 400, by: 2) { f.silhouette.add(165, y) }
        let all = sized(quota: (170, 40))
        let shown = [Bool](repeating: true, count: HudCard.allCases.count)
        var wide = HudWings()
        wide.still = true
        wide.balances = false
        wide.update(1 / 60, time: 0, frame: f, sizes: all, shown: shown)
        #expect(wide.split < HudCard.column.count, "without a narrower form it spills")
        var narrow = HudWings()
        narrow.still = true
        narrow.balances = false
        narrow.quotaNarrowest = 120
        narrow.update(1 / 60, time: 0, frame: f, sizes: all, shown: shown)
        #expect(narrow.split == HudCard.column.count)
        #expect(narrow.quotaRoom < 170 && narrow.quotaRoom >= 120)
    }

    @Test func aSpilledQuotaCardMeasuresItsRoomToTheRightEdge() {
        let f = wideHairFrame()
        var wings = HudWings()
        wings.still = true
        let all = sized(quota: (110, 44))
        wings.update(1 / 60, time: 0, frame: f, sizes: all, shown: [Bool](repeating: true, count: HudCard.allCases.count))
        #expect(wings.isAtHerRight(.quota))
        let left = wings.places[HudCard.quota.rawValue].x - 55
        #expect(abs(wings.quotaRoom - (444 - 0.025 * 296 - left)) < 1e-6)
    }

    @Test func formsNarrowAtOnceAndWidenAfterFittingWithRoomToSpare() {
        let widths = [140.0, 120, 100, 104]
        var fit = HudFormFit()
        fit.update(1 / 60, widths: widths, room: 200, spare: 12, snap: false)
        #expect(fit.form == 0)
        fit.update(1 / 60, widths: widths, room: 125, spare: 12, snap: false)
        #expect(fit.form == 1, "narrows at once")
        // Room for the full form without the spare changes nothing; with it, after 1.2 s.
        for _ in 0..<120 { fit.update(1 / 60, widths: widths, room: 145, spare: 12, snap: false) }
        #expect(fit.form == 1)
        for _ in 0..<60 { fit.update(1 / 60, widths: widths, room: 160, spare: 12, snap: false) }
        #expect(fit.form == 1)
        for _ in 0..<20 { fit.update(1 / 60, widths: widths, room: 160, spare: 12, snap: false) }
        #expect(fit.form == 0)
        // Nothing fits: the narrowest, not the last.
        fit.update(1 / 60, widths: widths, room: 50, spare: 12, snap: false)
        #expect(fit.form == 2)
        #expect(HudFormFit.best(widths: [.infinity, 120, 100, 100], room: 110) == 2)
        #expect(HudFormFit.narrowest([.infinity, 120, 100, 100]) == 3)
        fit.update(1 / 60, widths: widths, room: 300, spare: 12, snap: true)
        #expect(fit.form == 0, "a card off screen takes its best form at once")
    }
}
