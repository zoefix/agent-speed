import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation
import Testing
@testable import AgentSpeed

/// DESIGN 10, Intelligence readout: a larger intelligence line over a smaller thinking line, each centred on the orb on
/// its own, the score in its form's color, dotted while provisional and moving as DESIGN 12 says; the network readout keeps
/// its fixed cells.
@Suite("Orb line readout")
@MainActor
struct OrbLineReadoutTests {
    static let sizes: [CGFloat] = Array(stride(from: 80, through: 280, by: 4))

    // MARK: Glyphs

    @Test func glyphsSpellIntelligenceAndThinking() {
        let expected: [AppLanguage: [String]] = [.zhHant: ["性能", "思考"], .zhHans: ["性能", "思考"], .en: ["Perf", "TH"], .ja: ["性能", "思考"]]
        for (language, glyphs) in expected {
            let actual = OrbLineGlyphs(language)
            #expect([actual.intelligence, actual.thinking] == glyphs)
        }
    }

    // MARK: Baselines and stack shift

    @Test func aboveTheValueThinkingIsNearestAndIntelligenceSitsOnePitchAbove() {
        for placement in [OrbLinePlacement.led, .liquid, .minimal] {
            let t = placement.textSize(diameter: 148)
            let lines = placement.intelligenceBaselines(center: 200, diameter: 148)
            #expect(abs(lines.thinking - (200 + placement.nearestBaseline * 148)) < 1e-9)
            #expect(abs(lines.intelligence - (lines.thinking - 1.32 * t)) < 1e-9)
        }
    }

    @Test func reactorLowersTheThinkingLineSoTheIntelligenceLineClearsTheRim() {
        let placement = OrbLinePlacement.reactor
        let t = placement.textSize(diameter: 196)
        let lines = placement.intelligenceBaselines(center: 0, diameter: 196)
        #expect(abs(lines.thinking - -0.155 * 196) < 1e-9)
        #expect(abs(lines.intelligence - (lines.thinking - 1.32 * t)) < 1e-9)
        // The network readout keeps its baselines.
        #expect(abs(placement.baselines(center: 0, diameter: 196).nearest - -0.175 * 196) < 1e-9)
    }

    @Test func belowTheValueTheIntelligenceLineKeepsATextLinesCapTop() {
        let placement = OrbLinePlacement.needle
        for s in [148, 196] as [CGFloat] {
            let t = placement.textSize(diameter: s)
            let p = placement.scoreScale * t
            let lines = placement.intelligenceBaselines(center: 0, diameter: s)
            #expect(abs((lines.intelligence - 0.70 * p) - (0.108 * s - 0.70 * t)) < 1e-9)
            #expect(abs(lines.thinking - (lines.intelligence + 1.32 * t)) < 1e-9)
        }
    }

    @Test func stackShiftMeasuresTheBlockFromTheLargerIntelligenceLine() {
        for style in [OrbStyleKind.led, .liquid, .minimal] {
            var layout = OrbStyles.make(style).readoutLayout
            layout.dots = nil
            let placement = OrbLinePlacement.of(style)
            for s in [112, 148, 196] as [CGFloat] {
                let t = placement.textSize(diameter: s)
                let network = placement.stackShift(layout: layout, diameter: s, format: .traffic(bits: false))
                let intelligence = placement.stackShift(layout: layout, diameter: s, format: .intelligence)
                #expect(network > 0)
                #expect(abs(intelligence - network - 0.70 * 0.25 * t / 2) < 1e-9)
            }
        }
        for style in [OrbStyleKind.reactor, .needle] {
            let shift = OrbLinePlacement.of(style).stackShift(layout: OrbStyles.make(style).readoutLayout, diameter: 196,
                                                              format: .intelligence)
            #expect(shift == 0)
        }
    }

    @Test func stackShiftStillStopsAboveTheAgentDots() {
        let layout = OrbStyles.make(.led).readoutLayout
        for s in Self.sizes {
            let shift = OrbLinePlacement.led.stackShift(layout: layout, diameter: s, format: .intelligence)
            let titleBottom = layout.titleOffset * s + shift + 0.22 * max(7, layout.titleSize * s)
            let dotsTop = (layout.dots ?? 1) * s - OrbAgentDotsLayout.dotDiameter(s) / 2
            #expect(shift == 0 || titleBottom <= dotsTop - 0.05 * s + 1e-9)
        }
    }

    // MARK: Thinking hidden at zero

    @Test func aloneTheIntelligenceLineSitsOnTheNearestBaselineInEveryStyle() {
        for style in OrbStyleKind.allCases {
            let placement = OrbLinePlacement.of(style)
            for s in [112, 148, 196] as [CGFloat] {
                let t = placement.textSize(diameter: s)
                let p = placement.scoreScale * t
                let pair = placement.intelligenceBaselines(center: 300, diameter: s)
                let alone = placement.intelligenceBaselines(center: 300, diameter: s, single: 1)
                let half = placement.intelligenceBaselines(center: 300, diameter: s, single: 0.5)
                #expect(alone.thinking == pair.thinking)
                switch style {
                case .reactor:
                    #expect(abs(alone.intelligence - (300 - 0.155 * s)) < 1e-9)
                case .needle:
                    #expect(abs(alone.intelligence - (300 + 0.108 * s + 0.70 * (p - t))) < 1e-9)
                    #expect(alone.intelligence == pair.intelligence)
                default:
                    #expect(abs(alone.intelligence - (300 + placement.nearestBaseline * s)) < 1e-9)
                }
                // Where the line next to the value would be: the gap to the value is unchanged.
                #expect(abs(alone.intelligence - (placement.below ? pair.intelligence : pair.thinking)) < 1e-9)
                #expect(abs(half.intelligence - (pair.intelligence + alone.intelligence) / 2) < 1e-9)
            }
        }
    }

    @Test func theStackShiftFollowsTheSingleLinesTop() {
        for style in [OrbStyleKind.led, .liquid, .minimal] {
            var layout = OrbStyles.make(style).readoutLayout
            layout.dots = nil
            let placement = OrbLinePlacement.of(style)
            for s in [112, 148, 196] as [CGFloat] {
                let t = placement.textSize(diameter: s)
                let p = placement.scoreScale * t
                let pair = placement.stackShift(layout: layout, diameter: s, format: .intelligence)
                let alone = placement.stackShift(layout: layout, diameter: s, format: .intelligence, single: 1)
                let half = placement.stackShift(layout: layout, diameter: s, format: .intelligence, single: 0.5)
                // The block's top is the single line's cap top, nearest − 0.70 p.
                let valueTop = (layout.valueBaseline - 0.70 * layout.valueSize) * s
                #expect(abs(alone - max(0, (valueTop - (placement.nearestBaseline * s - 0.70 * p)) / 2)) < 1e-9)
                #expect(alone < pair)
                #expect(abs(pair - alone - 1.32 * t / 2) < 1e-9 || alone == 0)
                #expect(abs(half - (pair + alone) / 2) < 1e-9 || alone == 0)
            }
        }
        for style in [OrbStyleKind.reactor, .needle] {
            let placement = OrbLinePlacement.of(style)
            #expect(placement.stackShift(layout: OrbStyles.make(style).readoutLayout, diameter: 196, format: .intelligence, single: 1) == 0)
        }
    }

    @Test func theLocalizedWordsReadTheIntelligenceAlone() {
        let expected: [LanguagePreference: String] = [.zhHant: "性能 1,286", .zhHans: "性能 1,286", .en: "performance 1,286", .ja: "性能 1,286"]
        for (language, text) in expected {
            #expect(Localizer(preference: language)("a11y.orb.intel.only", "1,286") == text)
        }
    }

    // MARK: Fit

    @Test func widestLinesCarryTheirWidestText() {
        let t: CGFloat = 9
        let full = OrbLineMetrics.widest(size: t, language: .zhHant)
        let bare = OrbLineMetrics.widest(size: t, language: .zhHant, unit: false)
        #expect(full.intelligence == bare.intelligence)
        #expect(full.thinking > bare.thinking + 0.16 * t + 1.5 * t)
        #expect(full.line == max(full.intelligence, full.thinking))
        // 思考 is wider than TH.
        #expect(full.thinking > OrbLineMetrics.widest(size: t, language: .en).thinking)
    }

    @Test func openStylesFitByWidthAlone() {
        for style in [OrbStyleKind.led, .liquid, .reactor, .minimal] {
            let placement = OrbLinePlacement.of(style)
            for language in AppLanguage.allCases {
                for s in Self.sizes {
                    let widest = OrbLineMetrics.widest(size: placement.textSize(diameter: s), language: language)
                    #expect(OrbLineMetrics.fits(placement, diameter: s, format: .intelligence, language: language)
                        == (widest.line <= placement.fit * s))
                }
            }
        }
    }

    @Test func needleKeepsBothLinesInsideItsHub() {
        let placement = OrbLinePlacement.needle
        for language in AppLanguage.allCases {
            for s in Self.sizes {
                let t = placement.textSize(diameter: s)
                let widest = OrbLineMetrics.widest(size: t, language: language, unit: false, scoreScale: placement.scoreScale)
                let lines = placement.intelligenceBaselines(center: 0, diameter: s)
                let inside = hypot(widest.intelligence / 2, lines.intelligence) <= 0.223 * s
                    && hypot(widest.thinking / 2, lines.thinking) <= 0.223 * s
                let fits = OrbLineMetrics.fits(placement, diameter: s, format: .intelligence, language: language)
                #expect(fits == (inside && widest.line <= 0.285 * s))
                // With `tok/s` the thinking line could never fit the hub in Chinese.
                if language == .zhHant {
                    #expect(OrbLineMetrics.widest(size: t, language: language).thinking > 0.285 * s)
                }
            }
        }
        // DESIGN 10: at 1.1 t the 性能 line with `8,888` shows from 134 pt in Chinese and 139 pt in English.
        #expect(!OrbLineMetrics.fits(placement, diameter: 133, format: .intelligence, language: .zhHant))
        #expect(OrbLineMetrics.fits(placement, diameter: 134, format: .intelligence, language: .zhHant))
        #expect(!OrbLineMetrics.fits(placement, diameter: 138, format: .intelligence, language: .en))
        #expect(OrbLineMetrics.fits(placement, diameter: 139, format: .intelligence, language: .en))
    }

    @Test func everyStyleShowsTheReadoutAtTheDefaultSizeAndTheSmallOnesWhereTheNetworkDoes() {
        for language in AppLanguage.allCases {
            for style in OrbStyleKind.allCases {
                #expect(OrbLineMetrics.fits(.of(style), diameter: 148, format: .intelligence, language: language))
            }
            for style in [OrbStyleKind.led, .liquid, .minimal] {
                #expect(OrbLineMetrics.fits(.of(style), diameter: 112, format: .intelligence, language: language))
            }
            for style in [OrbStyleKind.reactor, .needle] {
                #expect(!OrbLineMetrics.fits(.of(style), diameter: 112, format: .intelligence, language: language))
                #expect(!OrbLineMetrics.fits(.of(style), diameter: 112, format: .traffic(bits: false), language: language))
            }
        }
    }

    @Test func networkReadoutFitsByItsCells() {
        for style in OrbStyleKind.allCases {
            let placement = OrbLinePlacement.of(style)
            for bits in [false, true] {
                for s in Self.sizes {
                    let t = placement.textSize(diameter: s)
                    let cells = OrbLineMetrics.cells(size: t, bits: bits)
                    #expect(cells.lead == 0.50 * t)
                    #expect(OrbLineMetrics.fits(placement, diameter: s, format: .traffic(bits: bits), language: .zhHant)
                        == (cells.lineWidth(size: t) <= placement.fit * s))
                }
            }
        }
    }

    // MARK: Drawing

    struct Piece {
        var x: CGFloat
        var right: CGFloat
        var baseline: CGFloat
        var height: CGFloat
        var alpha: CGFloat
        var color: SIMD4<Float>
        var texture: OrbTextTexture
    }

    static func score(_ form: PerfValueForm, _ shown: Int?, provisional: Bool = false, count: Int = 1) -> PerfSetReading {
        PerfSetReading(form: form, shown: shown, provisional: provisional, count: count, agentId: form == .none ? nil : "nova")
    }

    static func live(_ shown: Int) -> PerfSetReading { score(.live, shown) }

    static func frame(_ style: OrbStyleKind, diameter s: CGFloat, language: AppLanguage, format: OrbLineFormat,
                              _ content: OrbLineContent, thinking: OrbThinkingLine = OrbThinkingLine(),
                              scoreMotion: OrbScoreMotion? = nil, reduceMotion: Bool = false,
                              increaseContrast: Bool = false) -> OrbFrame {
        var c = OrbContent.placeholder(diameter: s)
        c.style = style
        c.language = language
        c.increaseContrast = increaseContrast
        c.lines = OrbLines(format: format, reading: OrbLineReading(content: content, shownAt: -.infinity, quietAt: 1e9,
                                                                   thinking: thinking, scoreMotion: scoreMotion))
        let geometry = OrbGeometry(diameter: s, canvasSide: s * 1.5)
        return OrbFrame(time: 100, dt: 0, geometry: geometry, content: c,
                        effects: OrbEffects(level: .standard, reduceMotion: reduceMotion, surface: .floating), heat: 0, overdrive: 0,
                        rawHeat: 0, rawOverdrive: 0, displayHeat: 0, tier: .idle, redline: 400, tierChangedAt: -.infinity,
                        titleChangedAt: -.infinity, flash: 0, breath: 1, breathPhase: 0, redlineAmount: 0, activity: 0,
                        signOfLife: nil, peakAge: .infinity, alpha: 1)
    }

    /// The readout's text pieces and provisional marks (quads a few points tall), in drawing order.
    static func pieces(_ frame: OrbFrame, placement: OrbLinePlacement) -> (text: [Piece], marks: [Piece]) {
        var scene = OrbScene(capacity: 32)
        let palette = OrbLineReadout.Palette.standard(dark: true, increaseContrast: frame.content.increaseContrast)
        OrbLineReadout().add(into: &scene, frame, placement: placement, palette: palette)
        let pieces = scene.texts.map { draw in
            let rect = draw.instance.rect
            let x = CGFloat(rect.x) + draw.texture.padding
            return Piece(x: x, right: x + draw.texture.advance, baseline: CGFloat(rect.y) + draw.texture.baseline,
                         height: draw.texture.size.height, alpha: CGFloat(draw.instance.color.w), color: draw.instance.color,
                         texture: draw.texture)
        }
        return (pieces.filter { $0.height >= 5 }, pieces.filter { $0.height < 5 })
    }

    /// The readout's text pieces, grouped by baseline from top to bottom (provisional marks left out).
    static func lines(_ frame: OrbFrame, placement: OrbLinePlacement) -> [[Piece]] {
        let pieces = pieces(frame, placement: placement).text
        var lines: [[Piece]] = []
        for piece in pieces.sorted(by: { $0.baseline < $1.baseline }) {
            if let last = lines.last?.first, abs(last.baseline - piece.baseline) < 0.01 {
                lines[lines.count - 1].append(piece)
            } else {
                lines.append([piece])
            }
        }
        return lines.map { $0.sorted { $0.x < $1.x } }
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func eachIntelligenceLineIsCentredOnItsOwnOnDevicePixels() throws {
        let cases: [(OrbStyleKind, AppLanguage, Int, String)] = [
            (.led, .zhHant, 1286, "412"), (.liquid, .zhHant, 948, "9.8"), (.reactor, .en, 12_840, "10"),
            (.minimal, .ja, 1452, "0"), (.needle, .zhHant, 8888, "412"), (.needle, .en, 734, "0"),
        ]
        for (style, language, index, thinking) in cases {
            for s in [148, 196] as [CGFloat] {
                let placement = OrbLinePlacement.of(style)
                let frame = Self.frame(style, diameter: s, language: language, format: .intelligence,
                                       .intelligence(score: Self.live(index), thinking: thinking))
                let lines = Self.lines(frame, placement: placement)
                try #require(lines.count == 2)
                let (intelligence, thinkingLine) = (lines[0], lines[1])
                let expected = placement.intelligenceBaselines(center: frame.center.y, diameter: s)
                #expect(abs(intelligence[0].baseline - expected.intelligence) < 0.01)
                #expect(abs(thinkingLine[0].baseline - expected.thinking) < 0.01)
                // Glyph, the compact number's characters (and tok/s, except in needle).
                #expect(intelligence.count == 1 + PerfFormat.compact(index).count)
                #expect(thinkingLine.count == 1 + thinking.count + (placement.thinkingUnit ? 1 : 0))
                for line in lines {
                    let middle = (line.map(\.x).min()! + line.map(\.right).max()!) / 2
                    #expect(abs(middle - frame.center.x) <= 0.26, "\(style) \(s) \(language)")
                    for piece in line {
                        #expect(abs(piece.x * 2 - (piece.x * 2).rounded()) < 0.01)
                    }
                }
                // The 性能 digits are 1.25 × the thinking digits (needle's hub 1.1 ×).
                let big = intelligence[1].right - intelligence[1].x
                let small = thinkingLine[1].right - thinkingLine[1].x
                #expect(abs(big / small - placement.scoreScale) < 0.06)
            }
        }
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func aLineMovesOnlyWhenItsDigitCountChanges() throws {
        func left(_ index: Int, _ thinking: String) throws -> (CGFloat, CGFloat) {
            let frame = Self.frame(.led, diameter: 148, language: .zhHant, format: .intelligence,
                                   .intelligence(score: Self.live(index), thinking: thinking))
            let lines = Self.lines(frame, placement: .led)
            try #require(lines.count == 2)
            return (lines[0][0].x, lines[1][0].x)
        }
        #expect(try left(1286, "412") == left(1034, "987"))
        #expect(try left(948, "412").0 > left(1286, "412").0)
        #expect(try left(98, "412").0 > left(948, "412").0)
        #expect(try left(1286, "9.8").1 > left(1286, "412").1)
    }

    /// A thinking line at `0` since `zeroAt`, seen at the frame's time (100).
    private static func zero(since zeroAt: TimeInterval) -> OrbThinkingLine {
        var line = OrbThinkingLine()
        line.update(zero: true, at: zeroAt)
        return line
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func afterTheHoldOnlyTheIntelligenceLineIsDrawnAtTheSingleLinePosition() throws {
        for style in OrbStyleKind.allCases {
            for language in [AppLanguage.zhHant, .en] {
                for s in [148, 196] as [CGFloat] {
                    let placement = OrbLinePlacement.of(style)
                    let content = OrbLineContent.intelligence(score: Self.score(.resting, 100), thinking: "0")
                    // Within the hold both lines show, the zero included.
                    let holding = Self.lines(Self.frame(style, diameter: s, language: language, format: .intelligence, content,
                                                        thinking: Self.zero(since: 97.5)), placement: placement)
                    #expect(holding.count == 2)
                    let frame = Self.frame(style, diameter: s, language: language, format: .intelligence, content,
                                           thinking: Self.zero(since: 90))
                    let lines = Self.lines(frame, placement: placement)
                    try #require(lines.count == 1)
                    let expected = placement.intelligenceBaselines(center: frame.center.y, diameter: s, single: 1)
                    #expect(abs(lines[0][0].baseline - expected.intelligence) < 0.01, "\(style) \(s) \(language)")
                    #expect(lines[0].count == 1 + 3)
                    let middle = (lines[0].map(\.x).min()! + lines[0].map(\.right).max()!) / 2
                    #expect(abs(middle - frame.center.x) <= 0.26)
                }
            }
        }
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func midGlideTheThinkingLineFadesAsTheIntelligenceLineEasesDown() throws {
        let placement = OrbLinePlacement.led
        // 0.1 s into the fade and the glide: thinking at 60 % of its ink, the glide at smoothstep(1/3).
        let frame = Self.frame(.led, diameter: 148, language: .zhHant, format: .intelligence,
                               .intelligence(score: Self.live(1286), thinking: "0"), thinking: Self.zero(since: 96.9))
        let steady = Self.frame(.led, diameter: 148, language: .zhHant, format: .intelligence,
                                .intelligence(score: Self.live(1286), thinking: "0"))
        let lines = Self.lines(frame, placement: placement)
        let full = Self.lines(steady, placement: placement)
        try #require(lines.count == 2 && full.count == 2)
        let eased = CGFloat(1.0 / 3 * 1.0 / 3 * (3 - 2.0 / 3))
        let expected = placement.intelligenceBaselines(center: frame.center.y, diameter: 148, single: eased)
        #expect(abs(lines[0][0].baseline - expected.intelligence) < 0.01)
        #expect(abs(lines[1][0].baseline - expected.thinking) < 0.01)
        #expect(abs(lines[0][0].alpha - full[0][0].alpha) < 1e-6)
        #expect(abs(lines[1][0].alpha - 0.6 * full[1][0].alpha) < 1e-4)
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func hidingTheThinkingLineNeverChangesWhetherTheReadoutFits() {
        for style in [OrbStyleKind.reactor, .needle] {
            let frame = Self.frame(style, diameter: 112, language: .zhHant, format: .intelligence,
                                   .intelligence(score: Self.score(.resting, 72), thinking: "0"), thinking: Self.zero(since: 0))
            #expect(Self.lines(frame, placement: .of(style)).isEmpty)
        }
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func nothingIsDrawnWhereTheLinesDoNotFit() {
        for style in [OrbStyleKind.reactor, .needle] {
            let frame = Self.frame(style, diameter: 112, language: .zhHant, format: .intelligence,
                                   .intelligence(score: Self.score(.live, 1048, provisional: true), thinking: "412"))
            #expect(Self.lines(frame, placement: .of(style)).isEmpty)
        }
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func networkLinesShareTheirLeftEdgeInFixedCells() throws {
        let rx = TrafficFormat.rateParts(12_400_000)
        let tx = TrafficFormat.rateParts(186_000)
        for style in OrbStyleKind.allCases {
            let placement = OrbLinePlacement.of(style)
            let frame = Self.frame(style, diameter: 196, language: .zhHant, format: .traffic(bits: false), .traffic(rx: rx, tx: tx))
            let lines = Self.lines(frame, placement: placement)
            try #require(lines.count == 2)
            let t = placement.textSize(diameter: 196)
            let cells = OrbLineMetrics.cells(size: t, bits: false)
            let left = frame.center.x - cells.lineWidth(size: t) / 2
            let numberEnd = left + cells.lead + 0.16 * t + cells.number
            for line in lines {
                #expect(abs(line[0].x - left) < 0.01)
                #expect(abs(line[line.count - 2].right - numberEnd) < 0.01)
                #expect(abs(line[line.count - 1].x - (numberEnd + 0.16 * t)) < 0.01)
            }
            // Upload is nearest above the value, download nearest below it.
            let baselines = placement.baselines(center: frame.center.y, diameter: 196)
            let (top, bottom) = placement.below ? (baselines.nearest, baselines.other) : (baselines.other, baselines.nearest)
            #expect(abs(lines[0][0].baseline - top) < 0.01)
            #expect(abs(lines[1][0].baseline - bottom) < 0.01)
        }
    }
}
