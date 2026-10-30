import AgentSpeedCore
import AppKit
import Foundation
import Testing
@testable import AgentSpeed

/// DESIGN 10, Menu bar and taskbar, and DESIGN 12: the intelligence block's fixed cells, the performance score's forms
/// (live, resting pale, none `–`) in compact form, the provisional mark under a provisional score, the score words, the
/// quiet state, the thinking hold, the `settings.bar.intelligence` switch with its one-time migration, and both blocks
/// side by side.
@Suite("Menu bar intelligence block")
@MainActor
struct StatusItemIntelligenceTests {
    /// The sums of all enabled sources: `score` in `form` from agent `a` (nil: none), `output` tok/s generated in the
    /// window (`Σ w`), `thinking` of it reasoning.
    static func sums(score: Double?, form: PerfValueForm = .live, provisional: Bool = false, thinking: Double,
                     output: Double? = nil) -> IntelligenceSums {
        var sums = IntelligenceSums()
        sums.output = output ?? max(thinking * 2, 40)
        sums.thinking = thinking
        sums.thinkingBase = sums.output
        if let score {
            sums.score = PerfSetValue(form: form, score: score, agentId: "a", lastLineMs: nil, provisional: provisional)
        }
        return sums
    }

    /// An agent with `score` in `form` (nil: fewer than three timed requests).
    static func agent(_ id: String, _ form: PerfValueForm, _ score: Double?, provisional: Bool = false,
                      project: String = "nova", lastLineMs: Int64? = nil) -> AgentStatus {
        var intelligence = IntelligenceSums()
        intelligence.score = PerfSetValue(form: form, score: score, agentId: id, lastLineMs: lastLineMs,
                                          provisional: provisional)
        return AgentStatus(id: id, source: .codex, project: project, model: "gpt-5.5", phase: form == .live ? .generating : .idle,
                           isSubagent: false, live: Rate(), recentSpeed: nil, lastActivity: Date(), intelligence: intelligence)
    }

    /// The block over `agents`, with their highest score and `thinking` tok/s (`output` generated).
    static func block(_ agents: [AgentStatus], thinking: Double, output: Double? = nil, language: LanguagePreference = .zhHant,
                      thinkingHidden: Bool = false) -> StatusItemIntelligence {
        var sums = Self.sums(score: nil, thinking: thinking, output: output)
        sums.score = agents.reduce(PerfSetValue.empty) { $0.merged(with: $1.intelligence.score) }
        return StatusItemIntelligence(sums, agents: agents, l10n: Localizer(preference: language), thinkingHidden: thinkingHidden)
    }

    // MARK: Cells

    @Test func theBlockKeepsOneWidthPerLanguageWhateverTheNumbersAndForms() {
        let readings: [(Double?, PerfValueForm, Double)] = [
            (nil, .live, 0), (0, .live, 0), (948, .live, 4.2), (8888, .live, 1_280), (12_840, .live, 12_800),
            (999_499, .live, 9.8), (1452, .resting, 0), (99_960, .resting, 412),
        ]
        var widths: [LanguagePreference: CGFloat] = [:]
        for language in [LanguagePreference.zhHant, .zhHans, .ja, .en] {
            let l10n = Localizer(preference: language)
            let measured = Set(readings.map { score, form, thinking in
                StatusItemIntelligence(Self.sums(score: score, form: form, thinking: thinking), l10n: l10n).width
            })
            #expect(measured.count == 1, "\(language) \(measured)")
            widths[language] = measured.first
        }
        #expect(widths[.zhHant] == widths[.zhHans] && widths[.zhHant] == widths[.ja])
    }

    @Test func numbersShareOneCellThatOnlyAMillionWidens() {
        let l10n = Localizer(preference: .zhHant)
        let base = StatusItemIntelligence(.zero, l10n: l10n).cell
        for text in ["–", "0", "948", "1,286", "8,888", "12.8k", "99.9k", "999k", "4.2", "128", "1.28k", "8.88k"] {
            #expect(base >= Self.textWidth(text), "\(text)")
        }
        #expect(base == 27, "DESIGN 12, *Number formats*: `8,888` in SF Pro 9 pt medium is 26.7")
        let million = StatusItemIntelligence(Self.sums(score: 1286, thinking: 1_200_000), l10n: l10n)
        #expect(million.thinking.number == "1.20M")
        #expect(million.cell > base)
        let millionScore = StatusItemIntelligence(Self.sums(score: 1_284_000, thinking: 12), l10n: l10n)
        #expect(millionScore.intelligence.number == "1.28M" && millionScore.cell > base)
    }

    private static func textWidth(_ text: String) -> CGFloat {
        ceil((text as NSString).size(withAttributes: [.font: StatusItemIntelligence.font]).width)
    }

    // MARK: Forms

    @Test func aLiveScoreTakesTheNumberColorWithTheThinkingRate() {
        let block = StatusItemIntelligence(Self.sums(score: 1286.4, thinking: 10), l10n: Localizer(preference: .zhHant))
        #expect(block.intelligence == .init(label: "性能", number: "1,286", dim: false))
        #expect(block.thinking == .init(label: "思考", number: "10", dim: false))
        #expect(block.unit == "tok/s")
        #expect(block.spoken == "性能 1,286，思考每秒 10 個 token")
        let english = StatusItemIntelligence(Self.sums(score: 12_840, thinking: 1_280), l10n: Localizer(preference: .en))
        #expect([english.intelligence.label, english.thinking.label] == ["Perf", "TH"])
        #expect(english.intelligence.number == "12.8k", "compact from 10,000")
        #expect(english.spoken == "performance 12,840, thinking 1.28k tokens per second", "spoken in full")
    }

    @Test func aRestingScoreIsPaleAndSaysIdle() {
        let l10n = Localizer(preference: .zhHant)
        let resting = StatusItemIntelligence(Self.sums(score: 1452, form: .resting, thinking: 0, output: 0), l10n: l10n)
        #expect(resting.intelligence == .init(label: "性能", number: "1,452", dim: true))
        #expect(resting.spoken == "性能 1,452，閒置中，思考每秒 0 個 token")
        let alone = StatusItemIntelligence(Self.sums(score: 1452, form: .resting, thinking: 0, output: 0), l10n: l10n,
                                           thinkingHidden: true)
        #expect(alone.alone)
        #expect(alone.intelligence.dim)
        #expect(alone.spoken == "性能 1,452，閒置中")
        #expect(StatusItemIntelligence(Self.sums(score: 1452, form: .resting, thinking: 0, output: 0), l10n: Localizer(preference: .en),
                                       thinkingHidden: true).spoken == "performance 1,452, idle")
    }

    @Test func noneIsADashNeverAZero() {
        let l10n = Localizer(preference: .zhHant)
        let none = StatusItemIntelligence(.zero, l10n: l10n)
        #expect(none.intelligence == .init(label: "性能", number: "–", dim: true))
        #expect(none.intelligence.number.unicodeScalars.map(\.value) == [0x2013])
        // `intel.none` replaces only the 性能 words; the thinking words stay, as on the orb.
        #expect(none.spoken == "性能：尚無分數，思考每秒 0 個 token")
        #expect(StatusItemIntelligence(.zero, l10n: Localizer(preference: .en)).spoken
            == "performance: no score yet, thinking 0 tokens per second")
        // 0 is a real score.
        let zero = StatusItemIntelligence(Self.sums(score: 0, thinking: 4.2), l10n: l10n)
        #expect(zero.intelligence == .init(label: "性能", number: "0", dim: false))
        #expect(zero.spoken == "性能 0，思考每秒 4.2 個 token")
    }

    @Test func quietTheBlockStaysWithDashes() {
        let block = StatusItemIntelligence(.zero, l10n: Localizer(preference: .zhHant))
        #expect(block.intelligence == .init(label: "性能", number: "–", dim: true))
        #expect(block.thinking == .init(label: "思考", number: "–", dim: true))
        #expect(block.unit == "tok/s")
        #expect(!block.alone)
    }

    @Test func theThinkingNumberIsADashOnlyWhileNothingIsGenerated() {
        let l10n = Localizer(preference: .zhHant)
        // Agents think before they have three timed requests: 性能 `–`, 思考 its rate; `intel.none` and the rate.
        let reading = StatusItemIntelligence(Self.sums(score: nil, thinking: 128, output: 300), l10n: l10n)
        #expect(reading.intelligence.number == "–")
        #expect(reading.thinking == .init(label: "思考", number: "128", dim: false))
        #expect(reading.spoken == "性能：尚無分數，思考每秒 128 個 token")
        // A tool call running under a live score generates nothing (`Σ w = 0`).
        let testing = StatusItemIntelligence(Self.sums(score: 948, thinking: 0, output: 0), l10n: l10n)
        #expect(testing.intelligence == .init(label: "性能", number: "948", dim: false))
        #expect(testing.thinking == .init(label: "思考", number: "–", dim: true))
        // Generated without reasoning: a dimmed 0.
        let zero = StatusItemIntelligence(Self.sums(score: 948, thinking: 0, output: 40), l10n: l10n)
        #expect(zero.thinking == .init(label: "思考", number: "0", dim: true))
    }

    // MARK: Provisional mark and words

    @Test func theProvisionalMarkFollowsTheSupplyingAgent() {
        for provisional in [true, false] {
            for form in [PerfValueForm.live, .resting] {
                let block = Self.block([Self.agent("a", form, 948, provisional: provisional)], thinking: 4.2)
                #expect(block.intelligence.provisional == provisional, "\(provisional) \(form)")
                #expect(block.intelligence.dim == (form == .resting))
                #expect(!block.thinking.provisional)
            }
        }
        // A lower provisional agent does not mark a settled top; a provisional top is marked over a settled one.
        #expect(!Self.block([Self.agent("a", .live, 1286), Self.agent("b", .live, 948, provisional: true)], thinking: 64)
            .intelligence.provisional)
        #expect(Self.block([Self.agent("a", .live, 948), Self.agent("b", .live, 1286, provisional: true)], thinking: 64)
            .intelligence.provisional)
        // A live score beats a higher resting one, and so does its mark.
        let mixed = Self.block([Self.agent("a", .resting, 1452), Self.agent("b", .live, 702, provisional: true)], thinking: 64)
        #expect(mixed.intelligence.number == "702")
        #expect(mixed.intelligence.provisional)
        #expect(!mixed.intelligence.dim)
        // None never carries the mark.
        #expect(!Self.block([Self.agent("a", .live, nil, provisional: true)], thinking: 64).intelligence.provisional)
    }

    @Test func theScoreWordsWrapProvisionalThenIdleThenTopOfN() {
        let provisional = [Self.agent("a", .live, 1048.2, provisional: true), Self.agent("b", .live, 702)]
        #expect(Self.block(provisional, thinking: 64).spoken == "性能 1,048，初步（樣本少），2 個 agent 中最高，思考每秒 64 個 token")
        #expect(Self.block(provisional, thinking: 64, language: .en).spoken
            == "performance 1,048, provisional (few samples), the highest of 2 agents, thinking 64 tokens per second")
        let crew = [Self.agent("a", .live, 1286.4), Self.agent("b", .live, 1123),
                    Self.agent("c", .live, 942.7), Self.agent("d", .live, 702.1, provisional: true)]
        #expect(Self.block(crew, thinking: 128).spoken == "性能 1,286，4 個 agent 中最高，思考每秒 128 個 token")
        // N counts the agents with a score in the shown form only.
        let resting = [Self.agent("a", .resting, 1452.3), Self.agent("b", .resting, 893),
                       Self.agent("c", .live, nil)]
        #expect(Self.block(resting, thinking: 0, output: 0, thinkingHidden: true).spoken == "性能 1,452，閒置中，2 個 agent 中最高")
        let pier = [Self.agent("a", .resting, 893, provisional: true)]
        #expect(Self.block(pier, thinking: 0, output: 0, thinkingHidden: true).spoken == "性能 893，初步（樣本少），閒置中")
        #expect(Self.block(pier, thinking: 0, output: 0, language: .ja, thinkingHidden: true).spoken
            == "性能 893、暫定（サンプルが少ない）、待機中")
    }

    @Test func theTooltipNamesTheSupplyingAgentInFullForm() {
        let l10n = Localizer(preference: .zhHant)
        let agents = [Self.agent("a", .live, 948, project: "orbit"), Self.agent("b", .live, 12_840, project: "nova")]
        let score = agents.reduce(PerfSetValue.empty) { $0.merged(with: $1.intelligence.score) }
        #expect(StatusItemIntelligence.topAgent(score, agents: agents, l10n: l10n) == "性能 12,840 · 最高：nova")
        #expect(StatusItemIntelligence.topAgent(score, agents: agents, l10n: Localizer(preference: .en)) == "Perf 12,840 · top: nova")
        // Without a project the source names it; nothing while none.
        let unnamed = [Self.agent("a", .resting, 893, project: "")]
        #expect(StatusItemIntelligence.topAgent(unnamed[0].intelligence.score, agents: unnamed, l10n: l10n) == "性能 893 · 最高：Codex")
        #expect(StatusItemIntelligence.topAgent(.empty, agents: agents, l10n: l10n) == nil)
    }

    // MARK: Drawing

    /// The block drawn on a dark bar into a 2× bitmap, as `StatusItemArtist` draws the item.
    private static func pixels(_ block: StatusItemIntelligence, height: CGFloat = 22, increasedContrast: Bool = false) -> [UInt8] {
        let scale: CGFloat = 2
        let width = Int(ceil(block.width * scale)), rows = Int(height * scale)
        var bytes = [UInt8](repeating: 0, count: width * rows * 4)
        bytes.withUnsafeMutableBytes { buffer in
            guard let context = CGContext(data: buffer.baseAddress, width: width, height: rows, bitsPerComponent: 8,
                                          bytesPerRow: width * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
            context.translateBy(x: 0, y: CGFloat(rows))
            context.scaleBy(x: scale, y: -scale)
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
            block.draw(at: 0, height: height, palette: StatusItemPalette(isDark: true, increasedContrast: increasedContrast))
            NSGraphicsContext.restoreGraphicsState()
        }
        return bytes
    }

    /// The pixel rows and columns where two drawings of the same block differ.
    private static func difference(_ a: [UInt8], _ b: [UInt8], width: Int) -> (rows: Set<Int>, columns: ClosedRange<Int>?) {
        var rows = Set<Int>(), columns: [Int] = []
        for index in stride(from: 0, to: a.count, by: 4) where a[index..<index + 4] != b[index..<index + 4] {
            rows.insert(index / 4 / width)
            columns.append(index / 4 % width)
        }
        return (rows, columns.min().flatMap { min in columns.max().map { min...$0 } })
    }

    @Test func increaseContrastDrawsALiveScoreInTheFullLabelColor() {
        // DESIGN 12, *Platform details*: live and resting differ by more than the pale level.
        let live = StatusItemIntelligence(Self.sums(score: 1286, thinking: 0), l10n: Localizer(preference: .zhHant), thinkingHidden: true)
        func strongest(_ increasedContrast: Bool) -> UInt8 {
            let bytes = Self.pixels(live, increasedContrast: increasedContrast)
            return stride(from: 3, to: bytes.count, by: 4).map { bytes[$0] }.max() ?? 0
        }
        #expect(strongest(true) == 255)
        #expect(strongest(false) < 230)
    }

    @Test func theProvisionalMarkIsDottedUnderTheNumberAndMovesNothingElse() throws {
        for (sums, thinkingHidden) in [(Self.sums(score: 948, thinking: 4.2), false), (Self.sums(score: 12_840, thinking: 1_280), false),
                                       (Self.sums(score: 1048, thinking: 0), true)] {
            for increasedContrast in [false, true] {
                let l10n = Localizer(preference: .zhHant)
                let plain = StatusItemIntelligence(sums, l10n: l10n, thinkingHidden: thinkingHidden)
                var marked = plain
                marked.intelligence.provisional = true
                #expect(marked.width == plain.width)
                let width = Int(ceil(plain.width * 2))
                let changed = Self.difference(Self.pixels(plain, increasedContrast: increasedContrast),
                                              Self.pixels(marked, increasedContrast: increasedContrast), width: width)
                // 1 pt dots (1.5 pt with Increase Contrast) on whole device pixels, from 1 pt under line 1's baseline:
                // 0.17 × 9 pt = 1.53 pt to their centre.
                let baseline = Int(plain.baselines(height: 22).intelligence * 2)
                let expectedRows = Set(baseline + 2..<baseline + (increasedContrast ? 5 : 4))
                #expect(changed.rows == expectedRows, "\(plain.intelligence.number) \(increasedContrast)")
                // Within the number's advance, which ends where line 2's unit ends, on the block's right edge.
                let columns = try #require(changed.columns)
                let numberWidth = (plain.intelligence.number as NSString).size(withAttributes: [.font: StatusItemIntelligence.font]).width
                let unitStart = Int((plain.width - Self.textWidth(plain.unit) - StatusItemIntelligence.unitGap) * 2)
                #expect(columns.upperBound > unitStart)
                #expect(columns.upperBound < Int(ceil(plain.width * 2)))
                #expect(Double(columns.upperBound - columns.lowerBound + 1) <= Double(numberWidth * 2) + 1)
                #expect(Double(columns.upperBound - columns.lowerBound + 1) >= Double(numberWidth * 2) - 2)
            }
        }
    }

    @Test func theScoreEndsWhereTheThinkingUnitEnds() {
        // DESIGN 10: line 1 has no unit, so the score shares the block's right edge with line 2's tok/s. Scores end in
        // a wide glyph: a tabular `1` keeps its advance but inks narrower, as in every tabular column.
        for score in [948.0, 1948, 12_840] {
            let block = StatusItemIntelligence(Self.sums(score: score, thinking: 68), l10n: Localizer(preference: .zhHant),
                                               thinkingHidden: false)
            let bytes = Self.pixels(block)
            let width = Int(ceil(block.width * 2))
            let baselines = block.baselines(height: 22)
            func rightmostInk(_ baseline: CGFloat) -> Int {
                let rows = Int((baseline - StatusItemIntelligence.font.capHeight) * 2)..<Int(baseline * 2)
                var right = -1
                for row in rows {
                    for column in 0..<width where bytes[(row * width + column) * 4 + 3] > 40 { right = max(right, column) }
                }
                return right
            }
            let line1 = rightmostInk(baselines.intelligence), line2 = rightmostInk(baselines.thinking)
            // 2× pixels: the last digit's and the `s`'s side bearings differ by well under a point.
            #expect(abs(line1 - line2) <= 2, "\(block.intelligence.number): \(line1) vs \(line2)")
        }
    }

    @Test func theDotsAreRoundEvenAndInTheNumbersColorAt70Percent() {
        let l10n = Localizer(preference: .zhHant)
        for scale in [1, 2, 3] as [CGFloat] {
            #expect(ProvisionalMark.dotDiameter(textSize: StatusItemIntelligence.size, scale: scale, increasedContrast: false) == 1)
        }
        #expect(ProvisionalMark.dotDiameter(textSize: StatusItemIntelligence.size, scale: 2, increasedContrast: true) == 1.5)
        var block = StatusItemIntelligence(Self.sums(score: 948, thinking: 4.2), l10n: l10n)
        block.intelligence.provisional = true
        let bytes = Self.pixels(block)
        let width = Int(ceil(block.width * 2))
        let row = Int(block.baselines(height: 22).intelligence * 2) + 2
        // Each dot covers two whole columns in both of its rows; the strongest pixel is the number's color (white at
        // 85 %) at 70 %, less the dot's anti-aliased corners. The label's ink also reaches below the baseline, so only
        // the number's span is read: from the number cell's start to the block's right edge, where the score ends.
        let numberLeft = Int((block.width - Self.textWidth(block.unit) - StatusItemIntelligence.unitGap - block.cell) * 2)
        let cell = numberLeft..<width
        let alphas = cell.map { bytes[(row * width + $0) * 4 + 3] }
        let lit = alphas.enumerated().filter { $0.element > 0 }.map(\.offset)
        let runs = lit.reduce(into: [[Int]]()) { runs, column in
            if let last = runs.last?.last, last == column - 1 { runs[runs.count - 1].append(column) } else { runs.append([column]) }
        }
        #expect(runs.count == ProvisionalMark.dotCount(width: (block.intelligence.number as NSString)
            .size(withAttributes: [.font: StatusItemIntelligence.font]).width, diameter: 1))
        #expect(runs.allSatisfy { $0.count == 2 })
        let gaps = zip(runs, runs.dropFirst()).map { $1[0] - $0[0] }
        #expect((gaps.max() ?? 0) - (gaps.min() ?? 0) <= 1)
        let strongest = Double(alphas.max() ?? 0) / 255
        #expect(strongest <= 0.85 * ProvisionalMark.opacity + 0.01)
        #expect(strongest >= 0.85 * ProvisionalMark.opacity * 0.7)
        #expect(cell.map { bytes[((row + 1) * width + $0) * 4 + 3] } == alphas)
    }

    // MARK: Thinking hidden at zero

    @Test func afterTheHoldLineOneStandsAloneCentredInTheSameCells() {
        for language in [LanguagePreference.zhHant, .en, .ja] {
            let l10n = Localizer(preference: language)
            for sums in [Self.sums(score: 1286, thinking: 0), Self.sums(score: 1452, form: .resting, thinking: 0, output: 0)] {
                let pair = StatusItemIntelligence(sums, l10n: l10n)
                let alone = StatusItemIntelligence(sums, l10n: l10n, thinkingHidden: true)
                #expect(!pair.alone)
                #expect(alone.alone)
                #expect(alone.width == pair.width)
                #expect(alone.cell == pair.cell)
                for height in [22, 24, 37] as [CGFloat] {
                    let lines = pair.baselines(height: height)
                    #expect(alone.baselines(height: height).intelligence == (lines.intelligence + lines.thinking) / 2)
                    #expect(alone.baselines(height: height).intelligence == lines.intelligence + StatusItemIntelligence.pitch / 2)
                }
            }
        }
        #expect(StatusItemIntelligence(Self.sums(score: 1286, thinking: 0), l10n: Localizer(preference: .zhHant), thinkingHidden: true)
            .spoken == "性能 1,286")
        #expect(StatusItemIntelligence(Self.sums(score: 1286, thinking: 0), l10n: Localizer(preference: .en), thinkingHidden: true)
            .spoken == "performance 1,286")
    }

    @Test func onlyAZeroUnderAScoreGoes() {
        let l10n = Localizer(preference: .zhHant)
        // A rate above 0 draws line 2 at once, even before the hold catches up.
        let busy = StatusItemIntelligence(Self.sums(score: 1286, thinking: 10), l10n: l10n, thinkingHidden: true)
        #expect(!busy.alone)
        #expect(busy.spoken == "性能 1,286，思考每秒 10 個 token")
        // Without a score both lines stay.
        let quiet = StatusItemIntelligence(.zero, l10n: l10n, thinkingHidden: true)
        #expect(!quiet.alone)
        #expect(quiet.spoken == "性能：尚無分數，思考每秒 0 個 token")
        #expect(!StatusItemIntelligence(Self.sums(score: nil, thinking: 0, output: 40), l10n: l10n, thinkingHidden: true).alone)
    }

    @Test func theHoldHidesAfterThreeSecondsAndReturnsAtOnce() {
        let hold = StatusItemThinkingHold()
        let zero = Self.sums(score: 1286, thinking: 0)
        hold.update(zero, at: 10)
        #expect(!hold.hidden)
        hold.update(zero, at: 12.9)
        #expect(!hold.hidden)
        hold.update(zero, at: 13)
        #expect(hold.hidden)
        hold.update(Self.sums(score: 1286, thinking: 4.2), at: 14)
        #expect(!hold.hidden)
        // A pause shorter than the hold never hides it.
        hold.update(zero, at: 15)
        hold.update(Self.sums(score: 1286, thinking: 3), at: 17.5)
        hold.update(zero, at: 18)
        hold.update(zero, at: 20.9)
        #expect(!hold.hidden)
        // A resting score after work goes the same way.
        let resting = Self.sums(score: 1452, form: .resting, thinking: 0, output: 0)
        hold.update(resting, at: 21)
        hold.update(resting, at: 24)
        #expect(hold.hidden)
        // No score: nothing is held.
        hold.update(.zero, at: 30)
        hold.update(.zero, at: 40)
        #expect(!hold.hidden)
        #expect(StatusItemThinkingHold.readsZero(Self.sums(score: 1286, thinking: 0.04)))
        #expect(!StatusItemThinkingHold.readsZero(Self.sums(score: 1286, thinking: 0.05)))
        #expect(!StatusItemThinkingHold.readsZero(.zero))
    }

    // MARK: Settings

    private static func defaults() throws -> (UserDefaults, String) {
        let suite = "StatusItemIntelligenceTests.\(UUID().uuidString)"
        return (try #require(UserDefaults(suiteName: suite)), suite)
    }

    @Test func storedChoicesReadBackAndUnknownReadsAsOff() throws {
        let (defaults, suite) = try Self.defaults()
        defer { defaults.removePersistentDomain(forName: suite) }
        #expect(AppSettings(defaults: defaults).barTraffic == .off)
        #expect(!AppSettings(defaults: defaults).barIntelligence)
        for (stored, choice) in [("off", BarTraffic.off), ("machine", .machine), ("agents", .agents), ("sparkles", .off), ("", .off)] {
            defaults.set(stored, forKey: "barTraffic")
            #expect(AppSettings(defaults: defaults).barTraffic == choice, "\(stored)")
        }
        let settings = AppSettings(defaults: defaults)
        settings.barTraffic = .agents
        settings.barIntelligence = true
        let reread = AppSettings(defaults: defaults)
        #expect(reread.barTraffic == .agents)
        #expect(reread.barIntelligence)
    }

    @Test func theRetiredIntelligenceChoiceMigratesOnceToTheSwitch() throws {
        let (defaults, suite) = try Self.defaults()
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(AppSettings.retiredIntelligenceTraffic, forKey: "barTraffic")
        let migrated = AppSettings(defaults: defaults)
        #expect(migrated.barTraffic == .off)
        #expect(migrated.barIntelligence)
        #expect(defaults.string(forKey: "barTraffic") == "off")
        #expect(defaults.object(forKey: "barIntelligence") as? Bool == true)
        // Once: a later choice sticks.
        migrated.barIntelligence = false
        #expect(!AppSettings(defaults: defaults).barIntelligence)
        // Both blocks chosen together stay so.
        defaults.set("machine", forKey: "barTraffic")
        defaults.set(true, forKey: "barIntelligence")
        let both = AppSettings(defaults: defaults)
        #expect(both.barTraffic == .machine && both.barIntelligence)
    }

    @Test func onlyTheNetworkChoicesNeedMeasuring() {
        #expect(BarTraffic.allCases == [.off, .machine, .agents])
        #expect(BarTraffic.allCases.filter(\.isNetwork) == [.machine, .agents])
        let l10n = Localizer(preference: .zhHant)
        #expect(BarTraffic.allCases.map { l10n($0.titleKey) } == ["不顯示", "網路速度：整台電腦", "網路速度：只看 agent"])
        #expect(l10n("settings.bar.intelligence") == "顯示性能")
    }

    // MARK: Both blocks

    private static func model() -> AppModel {
        _ = NSApplication.shared
        let suite = "StatusItemIntelligenceTests.model.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        defaults.removePersistentDomain(forName: suite)
        let model = AppModel(
            settings: AppSettings(defaults: defaults),
            updates: UpdateController(configuration: .current(environment: [:]), store: UpdateStore(defaults: defaults))
        )
        model.settings.language = .zhHant
        model.l10n.apply(.zhHant)
        model.settings.trafficEnabled = true
        model.settings.barMode = .glyphValue
        return model
    }

    @Test func bothBlocksAddTheirFixedWidthsInOrderAndNeverChangeWidth() {
        let model = Self.model()
        model.settings.barTraffic = .machine
        model.settings.barIntelligence = false
        let network = StatusItemState(model: model, time: 0)
        model.settings.barTraffic = .off
        model.settings.barIntelligence = true
        let intelligence = StatusItemState(model: model, time: 0)
        model.settings.barTraffic = .machine
        let both = StatusItemState(model: model, time: 0)
        model.settings.barTraffic = .off
        model.settings.barIntelligence = false
        let plain = StatusItemState(model: model, time: 0)

        let trafficWidth = network.layout.length - plain.layout.length
        let intelligenceWidth = intelligence.layout.length - plain.layout.length
        #expect(trafficWidth > 0 && intelligenceWidth > 0)
        // The sum of both fixed widths, each gap included.
        #expect(abs(both.layout.length - plain.layout.length - trafficWidth - intelligenceWidth) < 0.001)
        #expect(both.traffic != nil && both.intelligence != nil)
        // Left to right: the network block, then the intelligence block, each after its own 8 pt gap.
        #expect(both.layout.readoutX < both.layout.intelligenceX)
        #expect(abs(both.layout.intelligenceX - both.layout.readoutX - trafficWidth) < 0.001)
        #expect(abs(both.layout.length - StatusItemLayout.padding - both.layout.intelligenceX - intelligenceWidth) < 0.001)

        // The values never move the width: a wide score and wide rates measure the same.
        var snapshot = model.snapshot
        snapshot.intelligence = Self.sums(score: 12_840, thinking: 12_800)
        snapshot.agents = [Self.agent("a", .live, 12_840)]
        model.store.publishLive(snapshot)
        model.settings.barTraffic = .machine
        model.settings.barIntelligence = true
        #expect(abs(StatusItemState(model: model, time: 0).layout.length - both.layout.length) < 0.001)
    }
}
