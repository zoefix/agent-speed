import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation
import Testing
@testable import AgentSpeed

/// DESIGN 12 on the orb readout (DESIGN 10, Intelligence readout): live in the number token at 80 %, resting and `–` in
/// the unit token, never `0` for none; the provisional mark under a provisional score; the score's motion as drawn in the
/// compact form; the score words read by VoiceOver.
@Suite("Orb score readout")
@MainActor
struct OrbScoreReadoutTests {
    typealias Lines = OrbLineReadoutTests

    static let palette = OrbLineReadout.Palette.standard(dark: true, increaseContrast: false)

    static func near(_ color: SIMD4<Float>, _ expected: RGBColor) -> Bool {
        let difference = color - expected.simd
        return max(abs(difference.x), abs(difference.y), abs(difference.z), abs(difference.w)) < 1e-4
    }

    static func frame(_ score: PerfSetReading, thinking: String = "128", style: OrbStyleKind = .led, motion: OrbScoreMotion? = nil,
                      reduceMotion: Bool = false, increaseContrast: Bool = false) -> OrbFrame {
        Lines.frame(style, diameter: 148, language: .zhHant, format: .intelligence, .intelligence(score: score, thinking: thinking),
                    scoreMotion: motion, reduceMotion: reduceMotion, increaseContrast: increaseContrast)
    }

    /// The intelligence line: its glyph, then the score's pieces.
    static func scoreLine(_ frame: OrbFrame, style: OrbStyleKind = .led) throws -> [OrbLineReadoutTests.Piece] {
        let lines = Lines.lines(frame, placement: .of(style))
        return try #require(lines.first)
    }

    // MARK: Forms

    @Test(.enabled(if: OrbGPU.shared != nil))
    func liveIsInTheNumberColorRestingAndAbsentInTheUnitColor() throws {
        let cases: [(PerfSetReading, RGBColor, Int)] = [
            (Lines.live(1286), Self.palette.number, 5),
            (Lines.score(.resting, 948), Self.palette.secondary, 3),
            (.none, Self.palette.secondary, 1),
        ]
        for (score, color, digits) in cases {
            let line = try Self.scoreLine(Self.frame(score))
            try #require(line.count == 1 + digits)
            #expect(Self.near(line[0].color, Self.palette.secondary))
            for digit in line.dropFirst() { #expect(Self.near(digit.color, color), "\(score)") }
        }
        // Light, and the network readout's Increase Contrast tokens.
        #expect(OrbLineReadout.Palette.standard(dark: false, increaseContrast: false).secondary == RGBColor.black.opacity(0.5))
        #expect(OrbLineReadout.Palette.standard(dark: true, increaseContrast: true).number == RGBColor.white.opacity(0.92))
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func noneIsTheAbsentMarkNeverZero() throws {
        let none = try Self.scoreLine(Self.frame(.none, thinking: "22"))
        let zero = try Self.scoreLine(Self.frame(Lines.live(0)))
        try #require(none.count == 2 && zero.count == 2)
        #expect(none[1].texture !== zero[1].texture)
        // Centred by what it shows: the line is narrower than with four digits.
        let four = try Self.scoreLine(Self.frame(Lines.live(1286)))
        #expect(none[0].x > four[0].x)
    }

    // MARK: Provisional mark

    @Test(.enabled(if: OrbGPU.shared != nil))
    func onlyAProvisionalScoreSitsOnTheMark() {
        for style in OrbStyleKind.allCases {
            for provisional in [true, false] {
                for form in [PerfValueForm.live, .resting] {
                    let frame = Self.frame(Lines.score(form, 948, provisional: provisional), style: style)
                    #expect(Lines.pieces(frame, placement: .of(style)).marks.count == (provisional ? 1 : 0),
                            "\(style) \(form) \(provisional)")
                }
            }
            #expect(Lines.pieces(Self.frame(.none, style: style), placement: .of(style)).marks.isEmpty)
        }
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func theMarkSpansTheDigitsBelowTheBaselineAndTakesNoRoom() throws {
        for style in OrbStyleKind.allCases {
            let placement = OrbLinePlacement.of(style)
            let t = placement.textSize(diameter: 148)
            let p = placement.scoreScale * t
            let frame = Self.frame(Lines.score(.live, 948, provisional: true), style: style)
            let (text, marks) = Lines.pieces(frame, placement: placement)
            let mark = try #require(marks.first)
            let line = try Self.scoreLine(frame, style: style)
            let digits = Array(line.dropFirst())
            try #require(digits.count == 3)
            // From the left edge of the number's advance to its right edge.
            #expect(abs(mark.x - digits[0].x) < 0.01)
            #expect(abs((mark.right - mark.x) - (digits[2].right - digits[0].x)) <= 0.5)
            // Centred 0.22 p below the baseline, on a device pixel; clear of the thinking line's cap top.
            let diameter = ProvisionalMark.dotDiameter(textSize: p, scale: 2, increasedContrast: false)
            let centre = mark.baseline + diameter / 2
            #expect(abs(centre - (digits[0].baseline + 0.22 * p)) <= 0.26, "\(style)")
            let thinking = try #require(Lines.lines(frame, placement: placement).last)
            #expect(thinking[0].baseline - 0.70 * t - centre >= 0.34 * t - 0.26)
            // The number's color (its 70 % is in the dots).
            #expect(mark.color == digits[0].color)
            // No room taken: the line is laid out exactly as a settled score.
            let verified = Lines.pieces(Self.frame(Lines.score(.live, 948), style: style), placement: placement).text
            #expect(text.map(\.x) == verified.map(\.x))
            #expect(text.map(\.baseline) == verified.map(\.baseline))
        }
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func aRestingMarkIsPaleAndIncreaseContrastMakesTheDotsLarger() throws {
        let resting = Lines.pieces(Self.frame(Lines.score(.resting, 948, provisional: true)), placement: .led)
        let restingMark = try #require(resting.marks.first)
        #expect(Self.near(restingMark.color, Self.palette.secondary))
        let normal = try #require(Lines.pieces(Self.frame(Lines.score(.live, 948, provisional: true)), placement: .led).marks.first)
        let contrast = try #require(Lines.pieces(Self.frame(Lines.score(.live, 948, provisional: true), increaseContrast: true),
                                                 placement: .led).marks.first)
        #expect(contrast.height > normal.height)
        #expect(contrast.texture !== normal.texture)
    }

    // MARK: Motion as drawn

    @Test(.enabled(if: OrbGPU.shared != nil))
    func aCountDrawsTheIntegerItHasReached() throws {
        var motion = OrbScoreMotion(Lines.live(902))
        motion.update(Lines.live(1286), at: 100 - PerfMotion.countSeconds(from: 902, to: 1286) / 2)
        let counting = try Self.scoreLine(Self.frame(Lines.live(1286), motion: motion))
        let still = try Self.scoreLine(Self.frame(Lines.live(1238)))
        try #require(counting.count == 6 && still.count == 6)
        #expect(zip(counting.dropFirst(), still.dropFirst()).allSatisfy { $0.texture === $1.texture })
        // Reduce Motion: its crossfade ended long ago.
        let reduced = try Self.scoreLine(Self.frame(Lines.live(1286), motion: motion, reduceMotion: true))
        let landed = try Self.scoreLine(Self.frame(Lines.live(1286)))
        #expect(zip(reduced.dropFirst(), landed.dropFirst()).allSatisfy { $0.texture === $1.texture })
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func reduceMotionCrossfadesBothNumbersInPlace() throws {
        var motion = OrbScoreMotion(Lines.live(1286))
        motion.update(Lines.live(948), at: 99.9625)
        let line = try Self.scoreLine(Self.frame(Lines.live(948), motion: motion, reduceMotion: true))
        try #require(line.count == 1 + 3 + 5)
        let full = try Self.scoreLine(Self.frame(Lines.live(948)))[1].alpha
        let incoming = line.dropFirst().filter { abs($0.alpha / full - 0.25) < 1e-3 }
        let outgoing = line.dropFirst().filter { abs($0.alpha / full - 0.75) < 1e-3 }
        #expect(incoming.count == 3 && outgoing.count == 5)
        // Both start at the number's left edge.
        #expect(incoming.map(\.x).min() == outgoing.map(\.x).min())
    }

    @Test(.enabled(if: OrbGPU.shared != nil))
    func goingIdleCrossfadesTheColor() throws {
        var motion = OrbScoreMotion(Lines.live(948))
        motion.update(Lines.score(.resting, 948), at: 99.9)
        let line = try Self.scoreLine(Self.frame(Lines.score(.resting, 948), motion: motion))
        try #require(line.count == 4)
        #expect(Self.near(line[1].color, Self.palette.number.mix(Self.palette.secondary, 0.5)))
    }

    // MARK: Runtime

    @Test func theRuntimeFeedsTheSetsHighestScoreWithItsCount() throws {
        let suite = "OrbScoreReadoutTests"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        let settings = AppSettings(defaults: try #require(UserDefaults(suiteName: suite)))
        defer { UserDefaults.standard.removePersistentDomain(forName: suite) }
        let runtime = OrbRuntime(config: OrbConfig(id: "orb", traffic: .intelligence))
        let snapshot = PreviewData.snapshot(speed: 120, redline: 400)
        runtime.ingest(snapshot, settings: settings, cost: nil, isPreviewing: false, now: 10)
        let lines = try #require(runtime.feed.lines)
        #expect(lines.format == .intelligence)
        let score = try #require(lines.reading?.content.score)
        let live = snapshot.agents.filter { $0.intelligence.score.form == .live }
        #expect(score.form == .live)
        #expect(score.shown == live.compactMap(\.intelligence.score.shown).max())
        #expect(score.count == live.count && score.count >= 2)
        #expect(score == PerfSetReading(score: snapshot.intelligence.score, agents: snapshot.agents))
        // A network orb never carries a score.
        let network = OrbRuntime(config: OrbConfig(id: "net", traffic: .machine))
        network.ingest(snapshot, settings: settings, cost: nil, isPreviewing: false, now: 10)
        #expect(network.feed.lines == nil)
    }

    // MARK: Accessibility

    @Test func theLabelCarriesTheScoreWords() {
        let zh = Localizer(preference: .zhHant)
        func sentence(_ score: PerfSetReading, _ thinking: String?, _ l10n: Localizer) -> String {
            OrbAccessibilityReporter.intelligenceSentence(score, thinking: thinking, l10n)
        }
        #expect(sentence(Lines.score(.live, 1286, count: 4), "128", zh) == "性能 1,286，4 個 agent 中最高，思考每秒 128 個 token")
        #expect(sentence(Lines.score(.live, 1048, provisional: true), "64", zh) == "性能 1,048，初步（樣本少），思考每秒 64 個 token")
        #expect(sentence(Lines.score(.live, 12_840), "41", zh) == "性能 12,840，思考每秒 41 個 token")
        #expect(sentence(Lines.score(.resting, 1452, count: 2), nil, zh) == "性能 1,452，閒置中，2 個 agent 中最高")
        #expect(sentence(Lines.score(.resting, 948, provisional: true, count: 3), nil, zh)
            == "性能 948，初步（樣本少），閒置中，3 個 agent 中最高")
        #expect(sentence(.none, "22", zh) == "性能：尚無分數，思考每秒 22 個 token")
        #expect(sentence(.none, nil, zh) == "性能：尚無分數")
        let en = Localizer(preference: .en)
        #expect(sentence(Lines.score(.live, 1286, count: 4), "128", en)
            == "performance 1,286, the highest of 4 agents, thinking 128 tokens per second")
        #expect(sentence(.none, "22", en) == "performance: no score yet, thinking 22 tokens per second")
        for preference in [LanguagePreference.zhHans, .ja] {
            let l10n = Localizer(preference: preference)
            let words = sentence(.none, "22", l10n)
            #expect(words.hasPrefix(l10n("intel.none")))
            #expect(words.hasSuffix(String(l10n("a11y.orb.intel", "86", "22").dropFirst(l10n("a11y.orb.intel.only", "86").count))))
        }
    }

    // MARK: Timing

    /// The orb's frame policy and the app's surfaces share DESIGN 12's timings through `PerfMotion`.
    @Test func theOrbsTimingsAreTheSharedMotions() {
        #expect(PerfMotion.provisional == ProvisionalMark.fadeSeconds)
        #expect(PerfMotion.crossfade == 0.15 && PerfMotion.form == 0.2)
        for (old, new) in [(902, 1286), (1286, 902), (1000, 1009), (nil, 948), (948, nil)] as [(Int?, Int?)] {
            for reduceMotion in [false, true] {
                var motion = OrbScoreMotion(Lines.score(old == nil ? .none : .live, old))
                motion.update(Lines.score(new == nil ? .none : .live, new), at: 1)
                #expect(motion.change(reduceMotion: reduceMotion)
                    == PerfMotion.change(from: old, to: new, reduceMotion: reduceMotion))
            }
        }
    }
}
