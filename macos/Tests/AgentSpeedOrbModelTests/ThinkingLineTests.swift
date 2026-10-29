import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeedOrbModel

/// DESIGN 10, Intelligence readout, Thinking hidden at zero: the thinking line holds for 3 s at `0`, then fades out
/// (0.25 s) while the intelligence line glides (0.3 s) to the single-line position; a rate above 0 reverses it at once.
@Suite("Orb thinking line hidden at zero")
struct ThinkingLineTests {
    static func near(_ a: Double, _ b: Double) -> Bool { abs(a - b) < 1e-9 }

    @Test func onlyARateThatReadsZeroCounts() {
        for shown in ["0", "0.0"] { #expect(OrbThinkingLine.readsZero(shown), "\(shown)") }
        for shown in ["0.1", "1.0", "10", "100", "1.00k", "–", ""] { #expect(!OrbThinkingLine.readsZero(shown), "\(shown)") }
        #expect(OrbLineContent.intelligence(score: Self.score(125), thinking: "0").thinkingReadsZero)
        #expect(!OrbLineContent.intelligence(score: Self.score(125), thinking: "0.1").thinkingReadsZero)
        let traffic = OrbLineContent.traffic(rx: .init(number: "0", unit: "B/s"), tx: .init(number: "0", unit: "B/s"))
        #expect(!traffic.thinkingReadsZero)
    }

    @Test func aLineThatWasNeverZeroStaysPut() {
        let line = OrbThinkingLine()
        for time in [-1e9, 0, 1e9] {
            #expect(line.alpha(at: time, reduceMotion: false) == 1)
            #expect(line.single(at: time, reduceMotion: false) == 0)
            #expect(!line.isHidden(at: time))
            #expect(!line.isMoving(at: time))
            #expect(line.holdEnd(after: time) == nil)
        }
    }

    @Test func zeroHoldsForThreeSecondsThenFadesAndGlides() {
        var line = OrbThinkingLine()
        let started = line.update(zero: true, at: 10)
        let again = line.update(zero: true, at: 10.5)
        #expect(started && !again)
        #expect(line.holdEnd(after: 10.5) == 13)
        for time in [10, 11.5, 12.999] {
            #expect(line.alpha(at: time, reduceMotion: false) == 1)
            #expect(line.single(at: time, reduceMotion: false) == 0)
            #expect(!line.isHidden(at: time))
            #expect(!line.isMoving(at: time))
        }
        #expect(line.isHidden(at: 13))
        #expect(line.holdEnd(after: 13) == nil)
        // Fade 0.25 s, glide 0.3 s, from the same moment.
        #expect(Self.near(line.alpha(at: 13.125, reduceMotion: false), 0.5))
        #expect(Self.near(line.single(at: 13.15, reduceMotion: false), 0.5))
        #expect(line.alpha(at: 13.25, reduceMotion: false) == 0)
        #expect(Self.near(line.single(at: 13.25, reduceMotion: false), 0.25 / 0.3))
        #expect(line.single(at: 13.3, reduceMotion: false) == 1)
        #expect(line.isMoving(at: 13) && line.isMoving(at: 13.29))
        #expect(!line.isMoving(at: 13.3))
    }

    @Test func aRateBackWithinTheHoldChangesNothingVisible() {
        var line = OrbThinkingLine()
        line.update(zero: true, at: 10)
        let back = line.update(zero: false, at: 12.5)
        #expect(back)
        #expect(line == OrbThinkingLine())
        // A new zero starts a new hold.
        line.update(zero: true, at: 14)
        #expect(line.holdEnd(after: 14) == 17)
    }

    @Test func aRateAboveZeroBringsTheLineBackAtOnceInReverse() {
        var line = OrbThinkingLine()
        line.update(zero: true, at: 10)
        let back = line.update(zero: false, at: 20)
        #expect(back)
        #expect(!line.isHidden(at: 20))
        #expect(line.holdEnd(after: 20) == nil)
        #expect(line.alpha(at: 20, reduceMotion: false) == 0)
        #expect(Self.near(line.alpha(at: 20.125, reduceMotion: false), 0.5))
        #expect(line.alpha(at: 20.25, reduceMotion: false) == 1)
        #expect(line.single(at: 20, reduceMotion: false) == 1)
        #expect(Self.near(line.single(at: 20.15, reduceMotion: false), 0.5))
        #expect(line.single(at: 20.3, reduceMotion: false) == 0)
        #expect(line.isMoving(at: 20.2) && !line.isMoving(at: 20.3))
    }

    @Test func aReversalPartWayStartsFromWhereEachMotionStands() {
        var line = OrbThinkingLine()
        line.update(zero: true, at: 0)
        let alpha = line.alpha(at: 3.1, reduceMotion: false)
        let single = line.single(at: 3.1, reduceMotion: false)
        #expect(Self.near(alpha, 0.6))
        line.update(zero: false, at: 3.1)
        #expect(Self.near(line.alpha(at: 3.1, reduceMotion: false), alpha))
        #expect(Self.near(line.single(at: 3.1, reduceMotion: false), single))
        // Each finishes what was left of it: 40 % of the fade, a third of the glide.
        #expect(Self.near(line.alpha(at: 3.1 + 0.4 * 0.25, reduceMotion: false), 1))
        #expect(Self.near(line.single(at: 3.1 + 0.1, reduceMotion: false), 0))
        #expect(line.single(at: 3.1 + 0.09, reduceMotion: false) > 0)
    }

    @Test func reduceMotionHidesAndReturnsAtOnce() {
        var line = OrbThinkingLine()
        line.update(zero: true, at: 0)
        #expect(line.alpha(at: 2.9, reduceMotion: true) == 1)
        #expect(line.alpha(at: 3, reduceMotion: true) == 0)
        #expect(line.single(at: 3, reduceMotion: true) == 1)
        line.update(zero: false, at: 5)
        #expect(line.alpha(at: 5, reduceMotion: true) == 1)
        #expect(line.single(at: 5, reduceMotion: true) == 0)
    }

    // MARK: The quiet rule

    static func score(_ shown: Int) -> PerfSetReading { LineQuietTests.score(.live, shown) }

    @Test func theRuleStartsTheHoldWhenTheShownRateTurnsZero() throws {
        var rule = OrbLineQuietRule()
        rule.apply(score: Self.score(90), thinking: 41, generating: true, at: 0)
        #expect(rule.reading?.thinking == OrbThinkingLine())
        rule.apply(score: Self.score(90), thinking: 0.04, generating: true, at: 0.5)
        #expect(rule.reading?.content == .intelligence(score: Self.score(90), thinking: "0.0"))
        #expect(rule.reading?.thinking.holdEnd(after: 0.5) == 3.5)
        // Still zero in another form: the hold keeps its end.
        rule.apply(score: Self.score(90), thinking: 0, generating: true, at: 1)
        #expect(rule.reading?.content == .intelligence(score: Self.score(90), thinking: "0"))
        #expect(rule.reading?.thinking.holdEnd(after: 1) == 3.5)
        // A quiet tick that holds the values changes nothing, so a still orb renders no frame.
        let changed = rule.apply(score: .none, thinking: 0, generating: false, at: 1.5)
        #expect(!changed)
        rule.apply(score: Self.score(90), thinking: 12, generating: true, at: 6)
        let reading = try #require(rule.reading)
        #expect(!reading.thinking.isHidden(at: 6))
        #expect(reading.thinking.alpha(at: 6, reduceMotion: false) == 0)
    }

    @Test func aReadoutThatAppearsAtZeroShowsTheZeroForTheHold() throws {
        var rule = OrbLineQuietRule()
        rule.apply(score: Self.score(95), thinking: 0, generating: true, at: 40)
        let reading = try #require(rule.reading)
        #expect(reading.shownAt == 40)
        #expect(reading.thinking.holdEnd(after: 40) == 43)
        // Gone and back: the hold starts over with the new reading.
        rule.apply(score: .none, thinking: 0, generating: false, at: 51)
        rule.apply(score: Self.score(95), thinking: 0, generating: true, at: 60)
        #expect(rule.reading?.shownAt == 60)
        #expect(rule.reading?.thinking.holdEnd(after: 60) == 63)
    }

    @Test func networkReadingsNeverHoldAThinkingLine() {
        var rule = OrbLineQuietRule()
        rule.apply(TrafficRate(rx: 0, tx: 5000), bits: false, at: 0)
        #expect(rule.reading?.thinking == OrbThinkingLine())
    }

    // MARK: Frame policy

    @Test func framesRunOnlyWhileAMotionRunsAndTheHoldEndWakesAStillOrb() {
        var reading = OrbLineReading(content: .intelligence(score: Self.score(90), thinking: "0"), shownAt: 0, quietAt: 20)
        reading.thinking.update(zero: true, at: 1)
        #expect(reading.motion(at: 0.1, reduceMotion: false).frames)
        #expect(!reading.motion(at: 1, reduceMotion: false).frames)
        #expect(reading.motion(at: 1, reduceMotion: false).wake == 4)
        #expect(reading.motion(at: 4, reduceMotion: false).frames)
        #expect(reading.motion(at: 4.29, reduceMotion: false).frames)
        #expect(!reading.motion(at: 4.3, reduceMotion: false).frames)
        #expect(reading.motion(at: 4.3, reduceMotion: false).wake == 20)
        // Reduce Motion: each step still gets its frame.
        #expect(reading.motion(at: 4.02, reduceMotion: true).frames)
        #expect(!reading.motion(at: 4.06, reduceMotion: true).frames)
    }

    @Test func aHoldEndingAfterTheReadoutHasGoneWakesNothing() {
        var reading = OrbLineReading(content: .intelligence(score: Self.score(90), thinking: "0"), shownAt: 0, quietAt: 10)
        reading.thinking.update(zero: true, at: 9)
        #expect(reading.motion(at: 9.5, reduceMotion: false).wake == 10)
        #expect(reading.motion(at: 10.2, reduceMotion: false).wake == .infinity)
        #expect(!reading.motion(at: 12.1, reduceMotion: false).frames)
    }
}
