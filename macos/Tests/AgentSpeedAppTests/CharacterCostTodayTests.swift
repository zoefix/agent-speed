@testable import AgentSpeed
import AgentSpeedCharacter
import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation
import Testing

/// The 費用 card's 今日 row (DESIGN 2a.6; user's choice, 2026-11-11): today's cost of the billable sources under the live
/// rate; the card stays while there is a cost today, and the rate row leaves it once the money stops.
@Suite("Character readouts: the 費用 card's 今日 row")
@MainActor
struct CharacterCostTodayTests {
    private static func display() throws -> CostDisplay {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("cost-today-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let suite = "cost-today-\(UUID().uuidString)"
        return CostDisplay(
            settings: AppSettings(defaults: UserDefaults(suiteName: suite)!), l10n: Localizer(preference: .en),
            rates: ExchangeRateController(userAgent: "tests", appDataDirectory: folder),
            prices: PriceSyncController(book: PriceBook(), userAgent: "tests", appDataDirectory: folder)
        )
    }

    @Test func todayFollowsTheBillableTally() throws {
        let display = try Self.display()
        func today(_ micro: Int64, unpriced: Int = 0, requests: Int = 3) -> CostToday? {
            display.today(CostReading(today: CostTally(costMicroUsd: micro, unpricedRequests: unpriced), todayRequests: requests))
        }
        let priced = try #require(today(12_340_000))
        #expect(priced.amount.hasSuffix("12.34") && !priced.atLeast && !priced.unknown)
        #expect(priced.spoken.contains("12.34"))
        let partial = try #require(today(12_340_000, unpriced: 1))
        #expect(partial.atLeast && partial.amount.hasSuffix("12.34"))
        let unknown = try #require(today(0, unpriced: 3))
        #expect(unknown.unknown && unknown.amount == "–" && !unknown.atLeast)
        #expect(today(0, requests: 0) == nil, "no request today: no row")
    }

    private static func content(loud: Bool, today: CostToday?, money: LiveMoney? = CharacterCardsTests.money(4200))
        -> OrbCharacterContent? {
        let readings = OrbCharacterReadings(cost: money, costToday: today, costLoud: loud)
        return CharacterCardsTests.frame(readings).content.character
    }

    private static let today = CostToday(amount: "$12.34", atLeast: false, unknown: false, spoken: "today's cost 12.34")

    @Test func theRateRowLeavesOnlyWhileTheTodayRowStays() {
        var dynamics = CharacterCardDynamics()
        dynamics.update(1 / 60, character: Self.content(loud: true, today: Self.today), small: false, instant: false)
        #expect(dynamics.costRatePresence == 1)
        for _ in 0..<12 { dynamics.update(0.05, character: Self.content(loud: false, today: Self.today), small: false, instant: false) }
        #expect(dynamics.costRatePresence == 0 && dynamics.costRateShare == 0 && dynamics.costRateAlpha == 0)
        // Back to loud: the card opens, then the rate fades in.
        dynamics.update(0.2, character: Self.content(loud: true, today: Self.today), small: false, instant: false)
        #expect(dynamics.costRateAlpha == 0 && dynamics.costRateShare > 0.5)
        dynamics.update(0.4, character: Self.content(loud: true, today: Self.today), small: false, instant: false)
        #expect(dynamics.costRatePresence == 1)
        // Without a 今日 row the whole card fades instead, the rate row kept whole.
        var alone = CharacterCardDynamics()
        alone.update(1 / 60, character: Self.content(loud: true, today: nil), small: false, instant: false)
        for _ in 0..<12 { alone.update(0.05, character: Self.content(loud: false, today: nil), small: false, instant: false) }
        #expect(alone.costRatePresence == 1)
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func theCardClosesUpToItsTodayRow() {
        func size(_ loud: Bool, _ today: CostToday?, money: LiveMoney? = CharacterCardsTests.money(4200), settle: Bool = true)
            -> (w: Double, h: Double) {
            var dynamics = CharacterCardDynamics()
            let character = Self.content(loud: loud, today: today, money: money)
            for _ in 0..<(settle ? 1 : 0) { dynamics.update(1 / 60, character: character, small: false, instant: false) }
            let frame = CharacterCardsTests.frame(character?.readings)
            return CharacterCards(frame: frame, character: character, backing: 2, dynamics: dynamics).size(.cost, model: nil)
        }
        let both = size(true, Self.today)
        let rateOnly = size(true, nil)
        let todayOnly = size(false, Self.today)
        #expect(both.h > rateOnly.h + 8 && both.h > todayOnly.h + 8, "two rows")
        #expect(todayOnly.w > 20 && todayOnly.h > 12, "the 今日 row alone")
        #expect(size(false, Self.today, money: nil).h == todayOnly.h, "before any money the card is its 今日 row")
        #expect(size(false, nil, money: nil).w == 0)
    }
}
