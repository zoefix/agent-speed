@testable import AgentSpeed
import AgentSpeedCharacter
import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation
import Testing

/// The `character` orb's cards (DESIGN 2a.6, Readouts): the cost card's quiet rule, and plates that always fit their
/// text.
@Suite("Character cards")
@MainActor
struct CharacterCardsTests {
    static func money(_ value: Double, number: String = "0.0042", unknown: Bool = false) -> LiveMoney {
        LiveMoney(number: number, unit: "US$/s", inline: "$" + number, atLeast: false, unknown: unknown, value: value,
                  hoverLine: "", spokenAmount: number)
    }

    @Test func costIsLoudWhileMoneyFlowsThenHoldsAfterItsFade() {
        var rule = OrbCostQuietRule()
        rule.apply(Self.money(0, number: "0"), generating: false, at: 0)
        #expect(!rule.isLoud(at: 0))
        rule.apply(Self.money(4200), generating: true, at: 1)
        #expect(rule.isLoud(at: 1) && rule.isLoud(at: 10.9) && !rule.isLoud(at: 11.1))
        rule.apply(Self.money(0, number: "0"), generating: false, at: 5)
        #expect(rule.money?.number == "0")
        rule.apply(Self.money(0, number: "0.0000"), generating: false, at: 30)
        #expect(rule.money?.number == "0", "a faded card keeps its last value")
        rule.apply(Self.money(0, number: "–", unknown: true), generating: true, at: 40)
        #expect(rule.isLoud(at: 40), "a generating set without a price shows –")
        rule.apply(nil, generating: false, at: 41)
        #expect(rule.money == nil && !rule.isLoud(at: 41))
    }

    static func frame(_ readings: OrbCharacterReadings?, readouts: CharacterReadouts = .default, time: TimeInterval = 100,
                      contrast: Bool = false) -> OrbFrame {
        var c = OrbContent.placeholder(diameter: 296)
        c.style = .character
        c.increaseContrast = contrast
        c.language = .zhHant
        c.character = OrbCharacterContent(pack: nil, modelName: "claude-opus", vendorName: nil, accent: nil,
                                          readouts: readouts, readings: readings, todayLine: "今日 1.24M",
                                          agentsLine: "3 個 agent", costGlyph: "費用", emptyLabel: "")
        let geometry = OrbGeometry(diameter: 296, canvasSide: 444)
        return OrbFrame(time: time, dt: 0, geometry: geometry, content: c,
                        effects: OrbEffects(level: .standard, reduceMotion: false, surface: .floating), heat: 0, overdrive: 0,
                        rawHeat: 0, rawOverdrive: 0, displayHeat: 0, tier: .idle, redline: 400, tierChangedAt: -.infinity,
                        titleChangedAt: -.infinity, flash: 0, breath: 1, breathPhase: 0, redlineAmount: 0, activity: 0,
                        signOfLife: nil, peakAge: .infinity, alpha: 1)
    }

    static func readings(thinking: OrbThinkingLine = OrbThinkingLine()) -> OrbCharacterReadings {
        let score = PerfSetReading(form: .live, shown: 1286, provisional: false, count: 1, agentId: nil)
        let perf = OrbLineReading(content: .intelligence(score: score, thinking: "412"), shownAt: 0, quietAt: 1e9,
                                  thinking: thinking)
        let traffic = OrbLineReading(content: .traffic(rx: TrafficFormat.rateParts(12_400, bits: false),
                                                       tx: TrafficFormat.rateParts(2_300, bits: false)),
                                     shownAt: 0, quietAt: 1e9)
        return OrbCharacterReadings(network: traffic, performance: perf, cost: money(4200), networkLoud: true,
                                    performanceLoud: true, costLoud: true)
    }

    @Test func cardsWithoutValuesHaveNoSize() {
        let cards = CharacterCards(frame: Self.frame(nil), character: Self.frame(nil).content.character, backing: 2)
        for card in [HudCard.performance, .network, .cost] { #expect(cards.size(card, model: nil).w == 0) }
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func everyPlateHoldsItsText() {
        let frame = Self.frame(Self.readings())
        let cards = CharacterCards(frame: frame, character: frame.content.character, backing: 2)
        for card in [HudCard.performance, .network, .cost, .agents, .model] {
            let size = cards.size(card, model: "claude-opus")
            #expect(size.w > 30 && size.h > 12, "\(card)")
            var scene = OrbScene(capacity: 64)
            let place = HudWings.Place(x: 200, y: 200, scale: 1, spin: 0, alpha: 1)
            cards.draw(card, at: place, size: size, model: "claude-opus", right: false, into: &scene)
            #expect(!scene.texts.isEmpty, "\(card)")
            for text in scene.texts where text.texture.size.height >= 5 {
                let left = CGFloat(text.instance.rect.x) + text.texture.padding
                let right = left + text.texture.advance
                #expect(left >= 200 - size.w / 2 + 1 && right <= 200 + size.w / 2 - 1, "\(card) text leaves its plate")
            }
        }
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func thePerformanceCardClosesUpWhenThinkingHides() {
        var hidden = OrbThinkingLine()
        hidden.update(zero: true, at: 0)
        let shown = Self.frame(Self.readings())
        let gone = Self.frame(Self.readings(thinking: hidden), time: 100)
        let a = CharacterCards(frame: shown, character: shown.content.character, backing: 2).size(.performance, model: nil)
        let b = CharacterCards(frame: gone, character: gone.content.character, backing: 2).size(.performance, model: nil)
        #expect(b.h < a.h - 8)
        // Alone, 思考 never hides.
        let alone = Self.frame(Self.readings(thinking: hidden), readouts: CharacterReadouts(intelligence: false))
        let c = CharacterCards(frame: alone, character: alone.content.character, backing: 2).size(.performance, model: nil)
        #expect(c.h > 12)
    }
}
