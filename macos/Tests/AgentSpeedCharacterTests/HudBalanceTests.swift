import Foundation
import Testing
@testable import AgentSpeedCharacter

/// A 148 pt orb's frame (D = 296, window 444): her outline a slim body from y 80 to 400 around x 222.
private func body() -> HudFrame {
    var s = HudSilhouette(top: 0, bottom: 444, step: 4)
    for y in stride(from: 80.0, through: 400, by: 2) {
        s.add(190, y)
        s.add(254, y)
    }
    return HudFrame(bounds: (0, 0, 444, 444), silhouette: s, footprint: 296, centerX: 222, headTop: 80, chest: 170,
                    shoulders: 140)
}

private let d = 296.0
private let gap = 0.022 * d

/// The owner's cards at S = 148 (t = 8): 性能 with 思考 and 首字, network, 費用, today, 額度 with two sources (Codex 7d,
/// Claude Code 5h and 7d) or three (and Claude Desktop 7d and Fable 7d), the models card with `rows` rows.
private func owner(sources: Int = 3, rows: Int = 1) -> [(w: Double, h: Double)] {
    let quota = sources == 3 ? 85.0 : sources == 2 ? 55.0 : 0
    return [(84, 62), (90, 18), (90, 38.5), (78, 26.4), (110, 18), (86, 26.4),
            (130, 6.56 + Double(rows) * 9.76), (150, quota)]
}

private func heights(_ sizes: [(w: Double, h: Double)], models: Bool = true) -> [Double] {
    var shown = [Bool](repeating: true, count: HudCard.allCases.count)
    shown[HudCard.model.rawValue] = false
    return HudBalance.heights(sizes, shown: shown, models: models)
}

private func split(moving cards: [HudCard]) -> Int {
    cards.map { HudCard.column.firstIndex(of: $0)! }.min() ?? HudCard.column.count
}

@Suite struct HudBalanceTests {
    @Test func theOwnersCardsMoveTheQuotaCardUnderTheModelsCard() {
        // Three sources: 額度 alone weighs about as much as the four cards above it.
        for rows in 1...4 {
            let order = HudBalance.ranking(heights(owner(sources: 3, rows: rows)), gap: gap)
            #expect(order.first == split(moving: [.quota]), "\(rows) model rows")
        }
        // Two sources: with one or two model rows today follows it; three or four rows weigh enough without it.
        #expect(HudBalance.ranking(heights(owner(sources: 2, rows: 1)), gap: gap).first == split(moving: [.agents, .quota]))
        #expect(HudBalance.ranking(heights(owner(sources: 2, rows: 2)), gap: gap).first == split(moving: [.agents, .quota]))
        #expect(HudBalance.ranking(heights(owner(sources: 2, rows: 4)), gap: gap).first == split(moving: [.quota]))
    }

    @Test func weightsAreHeightsPlusTheGapsBetweenCards() {
        let h = heights(owner(sources: 3, rows: 1))
        let w = HudBalance.weights(h, split: split(moving: [.quota]), gap: gap)
        #expect(abs(w.left - (38.5 + 26.4 + 18 + 26.4 + 3 * gap)) < 1e-9)
        #expect(abs(w.right - (16.32 + 85 + gap)) < 1e-9, "the models card and 額度; the speed does not count")
        let alone = HudBalance.weights(h, split: HudCard.column.count, gap: gap)
        #expect(abs(alone.right - 16.32) < 1e-9)
    }

    @Test func theRankingGoesFromTheMostEvenSplitToTheLeast() {
        let h = heights(owner(sources: 3, rows: 1))
        let order = HudBalance.ranking(h, gap: gap)
        let spreads = order.map { split -> Double in
            let w = HudBalance.weights(h, split: split, gap: gap)
            return abs(w.left - w.right)
        }
        #expect(spreads == spreads.sorted())
        #expect(Set(order).count == order.count && order.count == HudCard.column.count + 1)
    }

    @Test func noCardMovesWhileTheColumnIsNotHeavier() {
        var sizes = owner(sources: 0, rows: 4)
        for card in [HudCard.network, .cost, .agents] { sizes[card.rawValue] = (0, 0) }
        // 性能 alone (38.5) against four model rows (45.6).
        let order = HudBalance.ranking(heights(sizes), gap: gap)
        #expect(order.first == HudCard.column.count)
    }

    @Test func aTieGoesToTheSmallerK() {
        // Two 20 pt cards against a 20 pt models card: k = 0 leaves 40 + gap against 20, k = 1 20 against 40 + gap.
        var h = [Double](repeating: 0, count: HudCard.allCases.count)
        h[HudCard.performance.rawValue] = 20
        h[HudCard.network.rawValue] = 20
        h[HudCard.models.rawValue] = 20
        let order = HudBalance.ranking(h, gap: gap)
        let spread = { (s: Int) -> Double in
            let w = HudBalance.weights(h, split: s, gap: gap)
            return abs(w.left - w.right)
        }
        #expect(abs(spread(HudCard.column.count) - spread(1)) < 0.5)
        #expect(order.first == HudCard.column.count, "the tie keeps the cards at her left")
    }

    @Test func hiddenCardsDoNotCountAndTheSplitSitsAtTheFirstMovedCard() {
        var sizes = owner(sources: 3, rows: 1)
        sizes[HudCard.agents.rawValue] = (0, 0)
        let h = heights(sizes)
        #expect(h[HudCard.agents.rawValue] == 0)
        let order = HudBalance.ranking(h, gap: gap)
        #expect(order.contains(HudCard.column.firstIndex(of: .quota)!))
        #expect(!order.contains(HudCard.column.firstIndex(of: .agents)!), "a hidden card adds no split of its own")
    }

    @Test func staleOnlyWhenACardComesOrGoesOrASideChangesEnough() {
        var balance = HudBalance()
        let h = heights(owner(sources: 3, rows: 1))
        let s = split(moving: [.quota])
        #expect(balance.isStale(h, split: s, gap: gap, threshold: 0.06 * d), "nothing chosen yet")
        balance.settle(h)
        #expect(!balance.isStale(h, split: s, gap: gap, threshold: 0.06 * d))
        var grown = h
        grown[HudCard.performance.rawValue] += 10
        #expect(!balance.isStale(grown, split: s, gap: gap, threshold: 0.06 * d), "10 pt is under 0.06 D")
        grown[HudCard.quota.rawValue] += 30
        #expect(balance.isStale(grown, split: s, gap: gap, threshold: 0.06 * d), "a source joined 額度")
        var gone = h
        gone[HudCard.network.rawValue] = 0
        #expect(balance.isStale(gone, split: s, gap: gap, threshold: 0.06 * d))
        var noModels = h
        noModels[HudCard.models.rawValue] = 0
        #expect(balance.isStale(noModels, split: s, gap: gap, threshold: 0.06 * d))
    }
}

@Suite struct HudWingsBalanceTests {
    private let shown: [Bool] = HudCard.allCases.map { $0 != .model }

    private struct Run {
        var wings = HudWings()
        var t = 0.0
        var changes: [Double] = []

        mutating func run(_ seconds: Double, _ f: HudFrame, _ sizes: [(w: Double, h: Double)], _ shown: [Bool]) {
            for _ in 0..<Int((seconds * 60).rounded()) {
                let before = wings.split
                wings.update(1 / 60, time: t, frame: f, sizes: sizes, shown: shown)
                // The first frame's choice is not a change.
                if t > 0, wings.split != before { changes.append(t) }
                t += 1 / 60
            }
        }
    }

    @Test func theQuotaCardContinuesUnderTheModelsCardWithItsLeftEdgeAndAccentTowardHer() {
        let f = body()
        var wings = HudWings()
        wings.still = true
        let sizes = owner(sources: 3, rows: 1)
        wings.update(1 / 60, time: 0, frame: f, sizes: sizes, shown: shown)
        #expect(wings.split == split(moving: [.quota]))
        #expect(wings.isAtHerRight(.quota) && !wings.isAtHerRight(.agents))
        let models = wings.places[HudCard.models.rawValue], quota = wings.places[HudCard.quota.rawValue]
        let m = sizes[HudCard.models.rawValue], q = sizes[HudCard.quota.rawValue]
        #expect(abs((quota.y - q.h / 2) - (models.y + m.h / 2 + gap)) < 1e-6, "directly under the models card")
        #expect(abs((quota.x - q.w / 2) - (models.x - m.w / 2)) < 1e-6, "one left edge on her right")
        for card in HudCard.column + [.speed, .models] {
            let p = wings.places[card.rawValue], s = sizes[card.rawValue]
            guard let e = f.silhouette.extent(p.y - s.h / 2, p.y + s.h / 2) else { continue }
            #expect(p.x + s.w / 2 <= e.left || p.x - s.w / 2 >= e.right, "\(card) covers her")
            #expect(p.x - s.w / 2 >= 0 && p.x + s.w / 2 <= 444 && p.y + s.h / 2 <= 444)
        }
    }

    @Test func belowS96TheColumnKeepsItsCardsAtHerLeft() {
        var wings = HudWings()
        wings.still = true
        wings.balances = false
        wings.update(1 / 60, time: 0, frame: body(), sizes: owner(sources: 3, rows: 1), shown: shown)
        #expect(wings.split == HudCard.column.count)
    }

    @Test func aSourceJoiningMovesCardsOnceTheNewSplitHasHeld() {
        var run = Run()
        let f = body()
        run.run(1, f, owner(sources: 2, rows: 1), shown)
        #expect(run.wings.split == split(moving: [.agents, .quota]))
        #expect(run.changes.isEmpty, "the first choice is not a change")
        // A third source joins 額度: today goes back to her left, 0.6 s later.
        run.run(0.5, f, owner(sources: 3, rows: 1), shown)
        #expect(run.wings.split == split(moving: [.agents, .quota]))
        run.run(0.2, f, owner(sources: 3, rows: 1), shown)
        #expect(run.wings.split == split(moving: [.quota]))
        // today fades out at her right and back in at her left.
        var dimmed = false
        for _ in 0..<40 {
            run.run(1 / 60, f, owner(sources: 3, rows: 1), shown)
            if run.wings.places[HudCard.agents.rawValue].alpha < 0.2 { dimmed = true }
        }
        #expect(dimmed && !run.wings.isAtHerRight(.agents))
        run.run(1, f, owner(sources: 3, rows: 1), shown)
        #expect(run.wings.places[HudCard.agents.rawValue].alpha > 0.95)
    }

    @Test func smallChangesMoveNothing() {
        var run = Run()
        let f = body()
        var sizes = owner(sources: 3, rows: 1)
        run.run(1, f, sizes, shown)
        let chosen = run.wings.split
        // 思考 hiding (about 10 pt) and a wider today line: under 0.06 D.
        sizes[HudCard.performance.rawValue].h -= 10.24
        sizes[HudCard.agents.rawValue].w += 30
        run.run(10, f, sizes, shown)
        #expect(run.wings.split == chosen && run.changes.isEmpty)
    }

    @Test func aCardAppearingChoosesAgain() {
        var run = Run()
        let f = body()
        var quiet = shown
        quiet[HudCard.network.rawValue] = false
        quiet[HudCard.performance.rawValue] = false
        run.run(1, f, owner(sources: 2, rows: 1), quiet)
        let before = run.wings.split
        // 性能 and network wake up: the left grows by two cards, so more of the column moves.
        run.run(0.3, f, owner(sources: 2, rows: 1), shown)
        #expect(run.wings.split == before)
        run.run(1, f, owner(sources: 2, rows: 1), shown)
        #expect(run.wings.split == split(moving: [.agents, .quota]))
        #expect(run.wings.split < before)
    }

    @Test func changesComeNoSoonerThanFourSecondsApartAndTwelveAfterABurst() {
        var run = Run()
        let f = body()
        let two = owner(sources: 2, rows: 1), three = owner(sources: 3, rows: 1)
        run.run(1, f, two, shown)
        run.run(1, f, three, shown)
        #expect(run.changes.count == 1)
        // The source leaves at once: the split waits until 4 s after the last change.
        run.run(2.5, f, two, shown)
        #expect(run.changes.count == 1)
        run.run(1.5, f, two, shown)
        #expect(run.changes.count == 2)
        #expect(run.changes[1] - run.changes[0] >= HudWings.returnAfter - 1e-9)
        // Two changes within 30 s: the next waits 12 s.
        run.run(10, f, three, shown)
        #expect(run.changes.count == 2)
        run.run(3, f, three, shown)
        #expect(run.changes.count == 3)
        #expect(run.changes[2] - run.changes[1] >= HudWings.settledReturn - 1e-9)
    }

    @Test func spillOverWaitsForTheChangeLimitToo() {
        // Her arm reaches over the column right after the split changed: 費用 has no room, but the cards hug her where
        // they are until the split may change again.
        var arm = body()
        for y in stride(from: 130.0, through: 260, by: 2) { arm.silhouette.add(222 - 100, y) }
        let f = body(), two = owner(sources: 2, rows: 2), three = owner(sources: 3, rows: 2)
        var run = Run()
        run.run(1, f, two, shown)
        run.run(1, f, three, shown)
        #expect(run.changes.count == 1)
        run.run(2, arm, three, shown)
        #expect(run.changes.count == 1)
        run.run(2, arm, three, shown)
        #expect(run.changes.count == 2)
        #expect(run.changes[1] - run.changes[0] >= HudWings.returnAfter - 1e-9)
        #expect(run.wings.split <= split(moving: [.cost, .agents, .quota]))
        // After two changes within 30 s, the spill-over waits 12 s.
        var burst = Run()
        burst.run(1, f, two, shown)
        burst.run(1, f, three, shown)
        burst.run(4.5, f, two, shown)
        #expect(burst.changes.count == 2)
        burst.run(10, arm, two, shown)
        #expect(burst.changes.count == 2)
        burst.run(2, arm, two, shown)
        #expect(burst.changes.count == 3)
        #expect(burst.changes[2] - burst.changes[1] >= HudWings.settledReturn - 1e-9)
    }

    @Test func aSplitThatDoesNotFitGivesWayToTheNextBest() {
        // Her wings spread at her right under the speed: no card fits there, so the column stays at her left.
        var f = body()
        for y in stride(from: 205.0, through: 444, by: 2) { f.silhouette.add(430, y) }
        var wings = HudWings()
        wings.still = true
        wings.update(1 / 60, time: 0, frame: f, sizes: owner(sources: 3, rows: 1), shown: shown)
        #expect(wings.split == HudCard.column.count)
        // A short window: today and 額度 (the most even) run past its bottom, 額度 alone (the next best) does not.
        let sizes = owner(sources: 2, rows: 1)
        #expect(HudBalance.ranking(heights(sizes), gap: gap).prefix(2) == [split(moving: [.agents, .quota]),
                                                                           split(moving: [.quota])])
        var short = body()
        short.bounds = (0, 0, 444, 307.4)
        var next = HudWings()
        next.still = true
        next.update(1 / 60, time: 0, frame: short, sizes: sizes, shown: shown)
        #expect(next.split == split(moving: [.quota]))
        let quota = next.places[HudCard.quota.rawValue]
        #expect(quota.y + sizes[HudCard.quota.rawValue].h / 2 <= 307.4 - 0.025 * d + 1e-6)
    }

    @Test func whenNoSplitFitsTheSpillOverDecides() {
        // Wide hair at her left and wings at her right: nothing fits near her shoulders, so the column moves or hugs
        // her as it does without balance.
        var f = body()
        for y in stride(from: 80.0, through: 300, by: 2) { f.silhouette.add(60, y) }
        for y in stride(from: 205.0, through: 444, by: 2) { f.silhouette.add(430, y) }
        var balanced = HudWings(), alone = HudWings()
        balanced.still = true
        alone.still = true
        alone.balances = false
        balanced.update(1 / 60, time: 0, frame: f, sizes: owner(sources: 3, rows: 1), shown: shown)
        alone.update(1 / 60, time: 0, frame: f, sizes: owner(sources: 3, rows: 1), shown: shown)
        #expect(balanced.split == alone.split && balanced.hugging == alone.hugging)
        #expect(balanced.places.map(\.x) == alone.places.map(\.x))
    }

    @Test func aDanceDoesNotSendCardsBackAndForth() {
        // Her arm swings out to her left over the column for 1.2 s of most 2.5 s for a minute: 費用 has no room.
        var arm = body()
        for y in stride(from: 130.0, through: 260, by: 2) { arm.silhouette.add(222 - 100, y) }
        var run = Run()
        for n in 0..<24 {
            run.run(1.3, body(), owner(sources: 3, rows: 2), shown)
            run.run(1.2, n % 3 == 2 ? body() : arm, owner(sources: 3, rows: 2), shown)
        }
        #expect(!run.changes.isEmpty && run.changes.count <= 2, "\(run.changes)")
        #expect(run.wings.split <= split(moving: [.cost, .agents, .quota]), "費用 waits at her right")
        // Calm again: back to the most even split.
        run.run(15, body(), owner(sources: 3, rows: 2), shown)
        #expect(run.wings.split == split(moving: [.quota]))
    }
}
