import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeedOrbModel

@Suite("Orb line readout quiet rule")
struct LineQuietTests {
    static func traffic(_ reading: OrbLineReading?) -> (rx: TrafficFormat.Parts, tx: TrafficFormat.Parts)? {
        guard case .traffic(let rx, let tx)? = reading?.content else { return nil }
        return (rx, tx)
    }

    @Test func quietSamplesNeverShowTheReadout() {
        var rule = OrbLineQuietRule()
        let changed1 = rule.apply(TrafficRate(rx: 400, tx: 300), bits: false, at: 0)
        #expect(!changed1)
        #expect(rule.reading == nil)
    }

    @Test func loudSampleFadesInAndQuietOneFadesOutAfterTenSeconds() throws {
        var rule = OrbLineQuietRule()
        let changed2 = rule.apply(TrafficRate(rx: 12_400_000, tx: 186_000), bits: false, at: 100)
        #expect(changed2)
        let reading = try #require(rule.reading)
        #expect(Self.traffic(reading)?.rx == .init(number: "12.4", unit: "MB/s"))
        #expect(Self.traffic(reading)?.tx == .init(number: "186", unit: "KB/s"))
        #expect(reading.alpha(at: 100, reduceMotion: false) == 0)
        #expect(reading.alpha(at: 100.25, reduceMotion: false) == 1)
        #expect(reading.isFading(at: 100.1, reduceMotion: false))
        // Quiet samples keep updating the numbers while it is shown, without moving the quiet moment.
        let changed3 = rule.apply(TrafficRate(rx: 200, tx: 100), bits: false, at: 101)
        #expect(changed3)
        #expect(rule.reading?.quietAt == 110)
        #expect(Self.traffic(rule.reading)?.rx.number == "200")
        #expect(abs((rule.reading?.alpha(at: 110.3, reduceMotion: false) ?? 0) - 0.5) < 1e-9)
        // Once gone, quiet samples change nothing (no frames).
        let changed4 = rule.apply(TrafficRate(rx: 300, tx: 100), bits: false, at: 111)
        #expect(!changed4)
    }

    @Test func loudSampleDuringFadeOutReturnsFromCurrentOpacity() throws {
        var rule = OrbLineQuietRule()
        rule.apply(TrafficRate(rx: 5000, tx: 0), bits: false, at: 0)
        rule.apply(TrafficRate(rx: 5000, tx: 0), bits: false, at: 10.3)
        let reading = try #require(rule.reading)
        #expect(abs(reading.alpha(at: 10.3, reduceMotion: false) - 0.5) < 1e-9)
        #expect(reading.quietAt == 20.3)
    }

    @Test func reduceMotionMakesFadesInstant() throws {
        var rule = OrbLineQuietRule()
        rule.apply(TrafficRate(rx: 5000, tx: 0), bits: true, at: 0)
        let reading = try #require(rule.reading)
        #expect(Self.traffic(reading)?.rx == .init(number: "40.0", unit: "Kb/s"))
        #expect(reading.alpha(at: 0, reduceMotion: true) == 1)
        #expect(reading.alpha(at: 10, reduceMotion: true) == 0)
        #expect(!reading.isFading(at: 0.1, reduceMotion: true))
    }

    @Test func unavailableClearsTheReadout() {
        var rule = OrbLineQuietRule()
        rule.apply(TrafficRate(rx: 5000, tx: 0), bits: false, at: 0)
        let changed5 = rule.apply(nil, bits: false, at: 1)
        #expect(changed5)
        #expect(rule.reading == nil)
    }

    static func score(_ form: PerfValueForm, _ shown: Int?, provisional: Bool = false, count: Int = 1) -> PerfSetReading {
        PerfSetReading(form: form, shown: shown, provisional: provisional, count: count, agentId: form == .none ? nil : "a")
    }

    static func intelligence(_ reading: OrbLineReading?) -> (score: PerfSetReading, thinking: String)? {
        guard case .intelligence(let score, let thinking)? = reading?.content else { return nil }
        return (score, thinking)
    }

    @Test func intelligenceShowsWhileTheScoreIsLiveAndHoldsItBetweenBursts() throws {
        var rule = OrbLineQuietRule()
        let idle = rule.apply(score: .none, thinking: 0, generating: false, at: 0)
        #expect(!idle)
        #expect(rule.reading == nil)
        let working = rule.apply(score: Self.score(.live, 1286), thinking: 41.25, generating: true, at: 1)
        #expect(working)
        #expect(Self.intelligence(rule.reading)?.score == Self.score(.live, 1286))
        #expect(Self.intelligence(rule.reading)?.thinking == "41")
        // A tool call generates nothing, but a live score keeps the readout loud.
        rule.apply(score: Self.score(.live, 1286), thinking: 0, generating: false, at: 30)
        #expect(rule.reading?.quietAt == 40)
        #expect(Self.intelligence(rule.reading)?.thinking == "0")
    }

    @Test func aRestingScoreStaysPaleForAsLongAsItsAgentsAreListedThenFades() throws {
        var rule = OrbLineQuietRule()
        rule.apply(score: Self.score(.live, 1452), thinking: 12, generating: true, at: 0)
        // The agent goes idle: resting keeps the readout loud, tick after tick, without a token.
        for time in stride(from: 1.0, through: 600, by: 0.5) {
            rule.apply(score: Self.score(.resting, 1452), thinking: 0, generating: false, at: time)
        }
        let resting = try #require(rule.reading)
        #expect(resting.quietAt == 610)
        #expect(resting.alpha(at: 600, reduceMotion: false) == 1)
        #expect(Self.intelligence(resting)?.score.isResting == true)
        // 10 minutes after its last line the agent leaves the list: the pale score holds until the readout has faded.
        rule.apply(score: .none, thinking: 0, generating: false, at: 600.5)
        #expect(Self.intelligence(rule.reading)?.score == Self.score(.resting, 1452))
        #expect(rule.reading?.quietAt == 610)
        rule.apply(score: .none, thinking: 0, generating: false, at: 609)
        #expect(Self.intelligence(rule.reading)?.score == Self.score(.resting, 1452))
        let gone = rule.apply(score: .none, thinking: 0, generating: false, at: 611)
        #expect(!gone)
        #expect(rule.reading.map { $0.isGone(at: 611) } == true)
    }

    @Test func aGeneratingSetWithoutAScoreShowsTheAbsentMark() throws {
        var rule = OrbLineQuietRule()
        let changed = rule.apply(score: .none, thinking: 22, generating: true, at: 0)
        #expect(changed)
        let reading = try #require(rule.reading)
        #expect(Self.intelligence(reading)?.score == PerfSetReading.none)
        #expect(reading.quietAt == 10)
        // Between bursts nothing holds it: 10 s after the last token it fades, showing `–` until then.
        rule.apply(score: .none, thinking: 0, generating: false, at: 2)
        #expect(Self.intelligence(rule.reading)?.score == PerfSetReading.none)
        #expect(rule.reading?.quietAt == 10)
    }

    @Test func aNewPromptAfterRestingShowsTheAbsentMarkWhileTheAgentReads() {
        var rule = OrbLineQuietRule()
        rule.apply(score: Self.score(.resting, 1452), thinking: 0, generating: false, at: 0)
        // A new agent with fewer than three timed requests: none, with the agent generating.
        rule.apply(score: .none, thinking: 30, generating: true, at: 5)
        #expect(Self.intelligence(rule.reading)?.score == PerfSetReading.none)
        #expect(rule.reading?.shownAt == 0)
        // Its third timed request brings a provisional live score.
        rule.apply(score: Self.score(.live, 1048, provisional: true), thinking: 30, generating: true, at: 9)
        #expect(Self.intelligence(rule.reading)?.score.isProvisional == true)
    }

    @Test func aScoreChangeMovesTheReadingsScoreMotion() throws {
        var rule = OrbLineQuietRule()
        rule.apply(score: Self.score(.live, 1000), thinking: 12, generating: true, at: 0)
        #expect(rule.reading?.scoreMotion == OrbScoreMotion(Self.score(.live, 1000)))
        rule.apply(score: Self.score(.live, 2000), thinking: 12, generating: true, at: 1)
        let reading = try #require(rule.reading)
        guard case .count(let seconds) = reading.scoreMotion.change(reduceMotion: false) else {
            Issue.record("a doubling counts")
            return
        }
        #expect(abs(seconds - 0.65) < 1e-9)
        #expect(reading.motion(at: 1.5, reduceMotion: false).frames)
        #expect(!reading.motion(at: 1.66, reduceMotion: false).frames)
        // A later tick with the same values moves only the quiet moment; the count runs on.
        rule.apply(score: Self.score(.live, 2000), thinking: 12, generating: true, at: 1.5)
        #expect(rule.reading?.scoreMotion == reading.scoreMotion)
        let still = rule.apply(score: Self.score(.live, 2000), thinking: 12, generating: true, at: 1.5)
        #expect(!still)
    }

    @Test func aReadoutThatReturnsStartsWithoutMotion() {
        var rule = OrbLineQuietRule()
        rule.apply(score: .none, thinking: 12, generating: true, at: 0)
        rule.apply(score: .none, thinking: 0, generating: false, at: 1)
        rule.apply(score: Self.score(.live, 948), thinking: 12, generating: true, at: 30)
        #expect(rule.reading?.shownAt == 30)
        #expect(rule.reading?.scoreMotion == OrbScoreMotion(Self.score(.live, 948)))
        #expect(rule.reading?.motion(at: 30.5, reduceMotion: false).frames == false)
    }

    @Test func switchingKindsStartsOver() {
        var rule = OrbLineQuietRule()
        rule.apply(TrafficRate(rx: 5000, tx: 0), bits: false, at: 0)
        rule.apply(score: Self.score(.live, 1000), thinking: 0, generating: true, at: 2)
        #expect(rule.reading?.shownAt == 2)
        rule.apply(TrafficRate(rx: 10, tx: 0), bits: false, at: 3)
        #expect(rule.reading == nil)
    }
}
