@testable import AgentSpeed
import AgentSpeedCharacter
import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import Foundation
import Testing

/// The 首字 row and the models card of the `character` orb (DESIGN 2a.6, Readouts; user's choice, 2026-11-10): the
/// latency's units, the models card's rows and quiet rule, what accessibility reads, and the readout menu's checks.
@Suite("Character readouts: 首字 and the models card")
@MainActor
struct CharacterReadoutsTests {
    // MARK: 首字

    @Test func latencyUnitsFollowTheRoundedValue() {
        func text(_ ms: Double, _ language: AppLanguage = .en) -> String {
            let latency = CharacterReadoutText.latency(ms: ms, language: language)
            return latency.number + " " + latency.unit
        }
        #expect(text(847) == "850 ms")
        #expect(text(850) == "850 ms")
        #expect(text(4) == "0 ms")
        #expect(text(994) == "990 ms")
        #expect(text(995) == "1.0 s", "rounds up into seconds")
        #expect(text(1050) == "1.1 s", "half away from zero")
        #expect(text(1234) == "1.2 s")
        #expect(text(9949) == "9.9 s")
        #expect(text(9950) == "10 s")
        #expect(text(14_200) == "14 s")
        #expect(text(75_000) == "75 s", "whole seconds from 10 s, no minutes")
    }

    @Test func latencyUnitsAreLocalized() {
        let zh = CharacterReadoutText.latency(ms: 850, language: .zhHant)
        #expect(zh.number == "850" && zh.unit == "毫秒" && zh.spoken == "850 毫秒")
        let ja = CharacterReadoutText.latency(ms: 1234, language: .ja)
        #expect(ja.number == "1.2" && ja.unit == "秒")
        #expect(CharacterReadoutText.latency(ms: 640, language: .ja).unit == "ミリ秒")
        #expect(CharacterReadoutText.latency(ms: 14_000, language: .zhHans).spoken == "14 秒")
        #expect(Localizer.string("character.latency.glyph", language: .zhHant) == "首字")
        #expect(Localizer.string("character.latency.glyph", language: .en) == "TTFT")
    }

    // MARK: The models card

    static func lines(_ count: Int, speedless: Int = 0) -> [CharacterModelLine] {
        (0..<count).map { k in
            let has = k < count - speedless
            return CharacterModelLine(id: "model-\(k)", speed: has ? String(200 - 10 * k) : CharacterReadoutText.noSpeed,
                                      hasSpeed: has, accent: k == 0 ? RGBColor(hex: 0xD97757) : nil)
        }
    }

    static func content(_ lines: [CharacterModelLine], latency: Double? = 1234,
                        readouts: CharacterReadouts = .default) -> OrbCharacterContent {
        let frame = CharacterCardsTests.frame(CharacterCardsTests.readings(), readouts: readouts)
        var content = frame.content.character!
        content.models = lines
        content.latency = latency.map { CharacterReadoutText.latency(ms: $0, language: .zhHant) }
        return content
    }

    @Test func theCardShowsFourRowsThenAMoreLine() {
        var dynamics = CharacterCardDynamics()
        dynamics.update(1 / 60, character: Self.content(Self.lines(6)), small: false, instant: false)
        #expect(dynamics.modelRows.rows.map(\.id) == ["model-0", "model-1", "model-2", "model-3", CharacterCardDynamics.moreId])
        #expect(dynamics.moreCount == 2)
        #expect(CharacterReadoutText.more(2, language: .zhHant) == "+2 個模型")
        #expect(CharacterReadoutText.more(3, language: .en) == "+3 more")
        // Exactly four: no more line.
        var four = CharacterCardDynamics()
        four.update(1 / 60, character: Self.content(Self.lines(4)), small: false, instant: false)
        #expect(four.modelRows.rows.count == 4)
    }

    @Test func smallOrbsKeepTwoRows() {
        var dynamics = CharacterCardDynamics()
        dynamics.update(1 / 60, character: Self.content(Self.lines(3)), small: true, instant: false)
        #expect(dynamics.modelRows.rows.map(\.id) == ["model-0", "model-1", CharacterCardDynamics.moreId])
        #expect(dynamics.moreCount == 1)
        var two = CharacterCardDynamics()
        two.update(1 / 60, character: Self.content(Self.lines(2)), small: true, instant: false)
        #expect(two.modelRows.rows.count == 2)
    }

    @Test func aModelThatStopsFadesOutOfItsRow() {
        var dynamics = CharacterCardDynamics()
        dynamics.update(1 / 60, character: Self.content(Self.lines(3)), small: false, instant: false)
        dynamics.update(0.1, character: Self.content(Array(Self.lines(3).prefix(2))), small: false, instant: false)
        let leaving = dynamics.modelRows.rows.first { $0.id == "model-2" }
        #expect(leaving?.wanted == false && (leaving?.presence ?? 0) > 0)
        #expect(dynamics.modelLines["model-2"] != nil, "a fading row keeps its text")
        #expect(dynamics.isMoving)
        dynamics.update(0.3, character: Self.content(Array(Self.lines(3).prefix(2))), small: false, instant: false)
        #expect(dynamics.modelRows.rows.count == 2 && !dynamics.isMoving)
    }

    @Test func theLatencyRowFadesInAndOut() {
        var dynamics = CharacterCardDynamics()
        dynamics.update(1 / 60, character: Self.content([], latency: nil), small: false, instant: false)
        #expect(dynamics.latency == nil && dynamics.latencyPresence == 0)
        dynamics.update(0.1, character: Self.content([], latency: 850), small: false, instant: false)
        #expect(dynamics.latency?.number == "850" && dynamics.latencyPresence > 0.3 && dynamics.latencyPresence < 0.5)
        dynamics.update(0.2, character: Self.content([], latency: 850), small: false, instant: false)
        #expect(dynamics.latencyPresence == 1)
        // Gone: it fades with its last value, then leaves.
        dynamics.update(0.1, character: Self.content([], latency: nil), small: false, instant: false)
        #expect(dynamics.latency?.number == "850" && dynamics.latencyPresence < 1)
        dynamics.update(0.3, character: Self.content([], latency: nil), small: false, instant: false)
        #expect(dynamics.latency == nil)
        // Switched off: the row goes too.
        dynamics.update(1, character: Self.content([], latency: 850, readouts: CharacterReadouts(latency: false)),
                        small: false, instant: false)
        #expect(dynamics.latencyPresence == 0)
    }

    /// The cards with room for the names in full.
    static func cards(_ content: OrbCharacterContent, small: Bool = false) -> CharacterCards {
        var dynamics = CharacterCardDynamics()
        dynamics.update(1 / 60, character: content, small: small, instant: true)
        let frame = CharacterCardsTests.frame(CharacterCardsTests.readings(), readouts: content.readouts)
        let probe = CharacterCards(frame: frame, character: content, backing: 2, dynamics: dynamics)
        if let measure = probe.modelsMeasure() {
            dynamics.fitNames(1 / 60, room: 1000, measure: measure, t: probe.m.t, spare: 0, snap: true)
        }
        return CharacterCards(frame: frame, character: content, backing: 2, dynamics: dynamics)
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func theModelsPlateHoldsItsRowsAndShrinksAtSmallSizes() {
        let content = Self.content(Self.lines(6))
        let cards = Self.cards(content)
        let size = cards.size(.models, model: nil)
        let small = Self.cards(content, small: true).size(.models, model: nil)
        #expect(size.w > 60 && size.h > 50)
        let t = Double(cards.m.t)
        #expect(abs(size.h - small.h - 2 * (0.92 + 0.3) * t) < 0.01, "two rows and the more line instead of four")
        var scene = OrbScene(capacity: 128)
        cards.draw(.models, at: HudWings.Place(x: 200, y: 200, scale: 1, spin: 0, alpha: 1), size: size, model: nil,
                   right: true, into: &scene)
        #expect(scene.texts.count >= 5 * 2, "a name and a number per row, the unit and the more line")
        for text in scene.texts where text.texture.size.height >= 5 {
            let left = CGFloat(text.instance.rect.x) + text.texture.padding
            let right = left + text.texture.advance
            #expect(left >= 200 - size.w / 2 + 1 && right <= 200 + size.w / 2 - 1, "text leaves the plate")
        }
        #expect(Self.cards(Self.content([])).size(.models, model: nil).w == 0)
    }

    @Test func displayNamesDropATrailingDate() {
        #expect(CharacterReadoutText.displayName("claude-sonnet-4-5-20250929") == "claude-sonnet-4-5")
        #expect(CharacterReadoutText.displayName("gpt-4o-2024-08-06") == "gpt-4o")
        #expect(CharacterReadoutText.displayName("o3-20250416") == "o3")
        #expect(CharacterReadoutText.displayName("gpt-6.1-sol") == "gpt-6.1-sol")
        #expect(CharacterReadoutText.displayName("claude-opus-4-5") == "claude-opus-4-5")
        #expect(CharacterReadoutText.displayName("qwen3-coder-480b-a35b") == "qwen3-coder-480b-a35b")
        #expect(CharacterReadoutText.displayName("model-202509") == "model-202509", "not a date")
        #expect(CharacterReadoutText.displayName("model-2025-0929") == "model-2025-0929")
        #expect(CharacterReadoutText.displayName("model_20250929") == "model_20250929")
        #expect(CharacterReadoutText.displayName("-20250929") == "-20250929", "nothing left without the date")
        #expect(CharacterReadoutText.displayName("20250929") == "20250929")
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func longNamesAreCutAtTheirEnd() {
        let id = "accounts/fireworks/models/qwen3-coder-480b-a35b-instruct-20250731"
        let cards = Self.cards(Self.content([CharacterModelLine(id: id, speed: "88", hasSpeed: true, accent: nil)]))
        let width = 9 * cards.m.t
        let shown = cards.modelsName(id, width: width)
        #expect(shown.hasSuffix("…") && shown.hasPrefix("accounts/fire") && !shown.contains("2025"))
        #expect(CharacterTextFit.measure(shown, style: cards.m.label(cards.m.t), language: .zhHant) <= width)
        #expect(cards.modelsName("claude-sonnet-4-5-20250929", width: 20 * cards.m.t) == "claude-sonnet-4-5")
        #expect(cards.modelsName("gpt-6.1-sol", width: width) == "gpt-6.1-sol")
        #expect(cards.modelsName("claude-sonnet-4-5-20250929", width: 4 * cards.m.t).hasPrefix("cla"))
    }

    /// The models card fitted to `room` (points from its left edge to the orb's edge).
    static func fitted(_ lines: [CharacterModelLine], room: Double, small: Bool = false) -> CharacterCards {
        let content = Self.content(lines)
        var dynamics = CharacterCardDynamics()
        dynamics.update(1 / 60, character: content, small: small, instant: true)
        let frame = CharacterCardsTests.frame(CharacterCardsTests.readings(), readouts: content.readouts)
        let probe = CharacterCards(frame: frame, character: content, backing: 2, dynamics: dynamics)
        if let measure = probe.modelsMeasure() {
            dynamics.fitNames(1 / 60, room: room, measure: measure, t: probe.m.t, spare: 11.84, snap: true)
        }
        return CharacterCards(frame: frame, character: content, backing: 2, dynamics: dynamics)
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func theNamesNarrowFromNineToFourTThenTheRowsKeepTheDotsAndSpeeds() throws {
        let lines = [CharacterModelLine(id: "claude-sonnet-4-5-20250929", speed: "142", hasSpeed: true, accent: nil),
                     CharacterModelLine(id: "deepseek-v4-pro-max-preview", speed: "74", hasSpeed: true, accent: nil)]
        let wide = Self.fitted(lines, room: 400)
        let t = wide.m.t
        let measure = try #require(wide.modelsMeasure())
        #expect(wide.dynamics.nameWidth == min(9 * t, measure.natural))
        let full = wide.size(.models, model: nil).w
        #expect(abs(full - (Double(measure.rest) + Double(wide.dynamics.nameWidth ?? 0))) < 0.01)
        // Room for names of 5 t: they narrow in half-t steps.
        let narrow = Self.fitted(lines, room: Double(measure.rest + 5.2 * t))
        #expect(narrow.dynamics.nameWidth == 5 * t)
        #expect(narrow.size(.models, model: nil).w < full - 20)
        // Less than 4 t: no names, the dots and the speeds.
        let dots = Self.fitted(lines, room: Double(measure.rest + 3.9 * t))
        #expect(dots.dynamics.nameWidth == nil)
        let size = dots.size(.models, model: nil)
        #expect(abs(size.w - Double(measure.narrowest)) < 0.01)
        var scene = OrbScene(capacity: 64)
        dots.draw(.models, at: HudWings.Place(x: 200, y: 200, scale: 1, spin: 0, alpha: 1), size: size, model: nil,
                  right: true, into: &scene)
        #expect(scene.texts.count == 3 + 2 + 1, "the digits of 142 and 74 and the unit once; no names")
        for text in scene.texts where text.texture.size.height >= 5 {
            let left = CGFloat(text.instance.rect.x) + text.texture.padding
            #expect(left >= 200 - size.w / 2 + 1 && left + text.texture.advance <= 200 + size.w / 2 - 1)
        }
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func increaseContrastMarksEveryModelInTheHighlightColor() {
        func dots(contrast: Bool) -> (colors: [SIMD4<Float>], dark: Bool) {
            let content = Self.content(Self.lines(2))
            var dynamics = CharacterCardDynamics()
            dynamics.update(1 / 60, character: content, small: false, instant: true)
            let frame = CharacterCardsTests.frame(CharacterCardsTests.readings(), contrast: contrast)
            let probe = CharacterCards(frame: frame, character: content, backing: 2, dynamics: dynamics)
            if let measure = probe.modelsMeasure() {
                dynamics.fitNames(1 / 60, room: 1000, measure: measure, t: probe.m.t, spare: 0, snap: true)
            }
            let cards = CharacterCards(frame: frame, character: content, backing: 2, dynamics: dynamics)
            var scene = OrbScene(capacity: 64)
            let size = cards.size(.models, model: nil)
            cards.draw(.models, at: HudWings.Place(x: 200, y: 200, scale: 1, spin: 0, alpha: 1), size: size, model: nil,
                       right: true, into: &scene)
            let circles = scene.instances.filter { $0.a.w == OrbInstance.Shape.circle.rawValue }.map(\.color)
            return (circles, frame.dark)
        }
        func same(_ a: SIMD4<Float>, _ b: SIMD4<Float>) -> Bool {
            abs(a.x - b.x) < 0.01 && abs(a.y - b.y) < 0.01 && abs(a.z - b.z) < 0.01
        }
        let plain = dots(contrast: false)
        #expect(plain.colors.count == 2)
        #expect(same(plain.colors[0], RGBColor(hex: 0xD97757).simd), "the vendor's theme color")
        let high = dots(contrast: true)
        let highlight = CharacterHighlight.color(dark: high.dark).simd
        #expect(high.colors.count == 2 && high.colors.allSatisfy { same($0, highlight) })
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func thePerformanceCardGainsTheLatencyRow() {
        let with = Self.cards(Self.content([], latency: 1234)).size(.performance, model: nil)
        let without = Self.cards(Self.content([], latency: nil)).size(.performance, model: nil)
        #expect(with.h > without.h + 8)
        let alone = Self.cards(Self.content([], latency: 1234,
                                            readouts: CharacterReadouts(intelligence: false, thinking: false)))
        #expect(alone.size(.performance, model: nil).h > 12, "首字 alone still has its card")
    }

    // MARK: Quiet rule and engine values

    @Test func theModelsCardKeepsItsLastRowsWhileQuiet() {
        var rule = OrbModelsQuietRule()
        let rows = [OrbModelReading(model: "claude-opus-5-5", speed: 120, vendor: "anthropic")]
        rule.apply(rows, at: 0)
        #expect(rule.isLoud(at: 0))
        rule.apply([], at: 5)
        #expect(rule.rows == rows && rule.isLoud(at: 9.9) && !rule.isLoud(at: 10.1))
        rule.apply([], at: 10.2)
        #expect(rule.rows == rows, "kept while the card fades")
        rule.apply([], at: 11)
        #expect(rule.rows.isEmpty)
    }

    @Test func rowsNameTheirVendor() {
        func agent(_ id: String, _ source: SourceKind, _ model: String, _ speed: Double?) -> AgentStatus {
            AgentStatus(id: id, source: source, project: "p", model: model, phase: .generating, isSubagent: false,
                        live: .zero, recentSpeed: speed, lastActivity: Date())
        }
        let rows = OrbRuntime.models(of: [agent("a", .claudeCode, "claude-opus-5-5", 80.4),
                                          agent("b", .codex, "gpt-6.1-sol", 140.5),
                                          agent("c", .opencode, "meta-llama/Llama-4", nil)], prices: nil)
        #expect(rows == [OrbModelReading(model: "gpt-6.1-sol", speed: 141, vendor: "openai"),
                         OrbModelReading(model: "claude-opus-5-5", speed: 80, vendor: "anthropic"),
                         OrbModelReading(model: "meta-llama/Llama-4", speed: nil, vendor: "meta")])
    }

    // MARK: Accessibility

    static func readings(models: [OrbModelReading], latency: Double?) -> OrbCharacterReadings {
        var readings = CharacterCardsTests.readings()
        readings.latencyMs = latency
        readings.models = models
        readings.modelsLoud = !models.isEmpty
        return readings
    }

    @Test func accessibilityReadsTheLatencyAfterPerformanceAndTheModelsLast() {
        let l10n = Localizer(preference: .en)
        let models = [OrbModelReading(model: "gpt-6.1-sol", speed: 106, vendor: nil),
                      OrbModelReading(model: "gemini-4-argon", speed: nil, vendor: nil)]
        let parts = OrbAccessibilityReporter.characterParts(Self.readings(models: models, latency: 1050),
                                                            currency: "US dollars", l10n, now: 100)
        #expect(parts.count == 5)
        #expect(parts[0].hasPrefix("performance"))
        #expect(parts[1] == "first-token latency 1.1 s")
        #expect(parts[4] == "running models: gpt-6.1-sol at 106 tokens per second, gemini-4-argon")
        var off = Self.readings(models: models, latency: 1050)
        off.readouts = CharacterReadouts(latency: false, models: false)
        let quiet = OrbAccessibilityReporter.characterParts(off, currency: "US dollars", l10n, now: 100)
        #expect(quiet.count == 3 && !quiet.contains { $0.contains("latency") || $0.contains("running") })
    }

    @Test func accessibilityReadsEveryRunningModelByItsFullId() {
        let l10n = Localizer(preference: .zhHant)
        var models = (0..<5).map { OrbModelReading(model: "m\($0)", speed: 100 - $0, vendor: nil) }
        models[0].model = "claude-sonnet-4-5-20250929"
        models[4].speed = nil
        #expect(CharacterReadoutText.modelsSentence(models, l10n)
            == "執行中的模型：claude-sonnet-4-5-20250929 每秒 100 個 token、m1 每秒 99 個 token、m2 每秒 98 個 token、"
            + "m3 每秒 97 個 token、m4")
        #expect(CharacterReadoutText.modelsSentence([], l10n) == nil)
    }

    // MARK: Menus

    private static func model() -> AppModel {
        _ = NSApplication.shared
        let suite = "CharacterReadoutsTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        defaults.removePersistentDomain(forName: suite)
        let model = AppModel(
            settings: AppSettings(defaults: defaults),
            updates: UpdateController(configuration: .current(environment: [:]), store: UpdateStore(defaults: defaults))
        )
        model.settings.language = .zhHant
        model.l10n.apply(.zhHant)
        return model
    }

    @Test func theReadoutMenuChecksEveryCardInOrder() throws {
        let model = Self.model()
        let id = try #require(model.orbs.orbs.first?.id)
        model.orbs.setStyle(.character, of: id)
        let orb = try #require(model.orbs.orb(id))
        let menu = try #require(OrbMenus.characterReadoutsItem(model: model, orb: orb).submenu)
        let titles = menu.items.prefix(9).map(\.title)
        #expect(titles == CharacterReadout.allCases.map { model.l10n($0.titleKey) })
        #expect(titles[2] == "首字延遲" && titles[6] == "執行中的模型" && titles[7] == "額度")
        #expect(menu.items.prefix(9).map(\.state) == [.on, .on, .on, .on, .on, .on, .on, .on, .off])
        #expect(menu.items[9].isSeparatorItem)
        // Unchecking 首字 and the models card saves at once.
        let latency = menu.items[2], models = menu.items[6]
        _ = latency.target?.perform(latency.action, with: latency)
        _ = models.target?.perform(models.action, with: models)
        #expect(model.orbs.orb(id)?.characterReadouts == CharacterReadouts(latency: false, models: false))
        let again = try #require(OrbMenus.characterReadoutsItem(model: model, orb: model.orbs.orb(id)!).submenu)
        #expect(again.items[2].state == .off && again.items[6].state == .off)
    }
}
