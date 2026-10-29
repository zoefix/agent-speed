import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeedOrbModel

/// DESIGN 12, *Motion*, on the orb readout: a change of at least 1 % and 3 counts up or down for
/// `clamp(0.3 + 0.35 |log2(new/old)|, 0.3, 0.7)` s at the orb's 30 fps, smaller ones replace the digits, `–` ↔ a number
/// crossfades, live ↔ resting crossfades the color, the provisional mark fades, Reduce Motion crossfades every change.
@Suite("Orb score motion")
struct ScoreMotionTests {
    static func near(_ a: Double, _ b: Double) -> Bool { abs(a - b) < 1e-9 }

    static func score(_ form: PerfValueForm, _ shown: Int?, provisional: Bool = false) -> PerfSetReading {
        LineQuietTests.score(form, shown, provisional: provisional)
    }

    // MARK: Decisions

    @Test func changesOfOnePercentAndThreeCountSmallerOnesReplace() {
        #expect(PerfMotion.change(from: 1000, to: 1000, reduceMotion: false) == .none)
        #expect(PerfMotion.change(from: 1000, to: 1009, reduceMotion: false) == .replace)
        #expect(PerfMotion.change(from: 1000, to: 1010, reduceMotion: false)
            == .count(seconds: PerfMotion.countSeconds(from: 1000, to: 1010)))
        #expect(PerfMotion.change(from: 1000, to: 990, reduceMotion: false)
            == .count(seconds: PerfMotion.countSeconds(from: 1000, to: 990)))
        #expect(PerfMotion.change(from: 100, to: 102, reduceMotion: false) == .replace, "3 at least")
        #expect(PerfMotion.change(from: 100, to: 103, reduceMotion: false) == .count(seconds: PerfMotion.countSeconds(from: 100, to: 103)))
        #expect(PerfMotion.change(from: nil, to: 1286, reduceMotion: false) == .crossfade(seconds: 0.15))
        #expect(PerfMotion.change(from: 1286, to: nil, reduceMotion: false) == .crossfade(seconds: 0.15))
        #expect(PerfMotion.change(from: 1000, to: 2000, reduceMotion: false, counts: false) == .replace)
        for (old, new) in [(1000, 1001), (1000, 2000), (2000, 1000)] as [(Int?, Int?)] + [(nil, 12)] {
            #expect(PerfMotion.change(from: old, to: new, reduceMotion: true) == .crossfade(seconds: 0.15))
        }
        #expect(PerfMotion.change(from: nil, to: nil, reduceMotion: true) == .none)
    }

    @Test func aCountTakesItsLogDistanceWithinThreeAndSevenTenthsOfASecond() {
        #expect(Self.near(PerfMotion.countSeconds(from: 1000, to: 2000), 0.65))
        #expect(Self.near(PerfMotion.countSeconds(from: 2000, to: 1000), 0.65))
        #expect(PerfMotion.countSeconds(from: 1000, to: 1010) > 0.3 && PerfMotion.countSeconds(from: 1000, to: 1010) < 0.31)
        #expect(PerfMotion.countSeconds(from: 1000, to: 8000) == 0.7)
        #expect(PerfMotion.countSeconds(from: 0, to: 500) == 0.7)
    }

    // MARK: Count

    @Test func aCountMovesMonotonicallyWithEaseOutCubicTiming() {
        let seconds = PerfMotion.countSeconds(from: 902, to: 1286)
        var last = 902
        for step in 0...Int(seconds * 1000) {
            let value = PerfMotion.countValue(from: 902, to: 1286, elapsed: Double(step) / 1000)
            #expect(value >= last)
            last = value
        }
        #expect(PerfMotion.countValue(from: 902, to: 1286, elapsed: 0) == 902)
        #expect(PerfMotion.countValue(from: 902, to: 1286, elapsed: seconds) == 1286)
        // Quick at first, settling into the final number: half way in time is 7/8 of the way.
        #expect(PerfMotion.countValue(from: 902, to: 1286, elapsed: seconds / 2) == 902 + 336)
        // Down as well.
        #expect(PerfMotion.countValue(from: 1286, to: 902, elapsed: seconds / 2) == 1286 - 336)
    }

    @Test func aCountRendersAtTheOrbsRateForItsDurationOnlyInTheCompactForm() {
        var motion = OrbScoreMotion(Self.score(.live, 902))
        #expect(!motion.isMoving(at: 0, reduceMotion: false))
        motion.update(Self.score(.live, 1286), at: 10)
        let seconds = PerfMotion.countSeconds(from: 902, to: 1286)
        #expect(motion.frame(at: 10, reduceMotion: false).text == "902")
        #expect(motion.frame(at: 10 + seconds / 2, reduceMotion: false).text == "1,238")
        #expect(motion.frame(at: 10 + seconds, reduceMotion: false).text == "1,286")
        #expect(motion.frame(at: 99, reduceMotion: false).text == "1,286")
        #expect(motion.frame(at: 10.2, reduceMotion: false).alpha == 1 && motion.frame(at: 10.2, reduceMotion: false).outgoing == nil)
        #expect(motion.isMoving(at: 10, reduceMotion: false) && motion.isMoving(at: 10 + seconds - 0.01, reduceMotion: false))
        #expect(!motion.isMoving(at: 10 + seconds, reduceMotion: false) && !motion.isMoving(at: 9.99, reduceMotion: false))

        var large = OrbScoreMotion(Self.score(.live, 9000))
        large.update(Self.score(.live, 12_840), at: 0)
        #expect(large.frame(at: 0, reduceMotion: false).text == "9,000")
        #expect(large.frame(at: 1, reduceMotion: false).text == "12.8k")
    }

    @Test func aSmallChangeReplacesTheDigitsWithoutFrames() {
        var motion = OrbScoreMotion(Self.score(.live, 1286))
        motion.update(Self.score(.live, 1290), at: 5)
        #expect(motion.change(reduceMotion: false) == .replace)
        #expect(motion.frame(at: 5, reduceMotion: false) == OrbScoreMotion.Frame(text: "1,290", alpha: 1, outgoing: nil,
                                                                                 outgoingAlpha: 0, pale: 0, provisional: 0))
        #expect(!motion.isMoving(at: 5, reduceMotion: false))
    }

    // MARK: Crossfades

    @Test func theAbsentMarkCrossfadesWithANumberInThePaleColor() {
        var motion = OrbScoreMotion(.none)
        #expect(motion.frame(at: 0, reduceMotion: false).text == "\u{2013}")
        #expect(motion.frame(at: 0, reduceMotion: false).pale == 1)
        motion.update(Self.score(.live, 1048, provisional: true), at: 1)
        let frame = motion.frame(at: 1.075, reduceMotion: false)
        #expect(frame.text == "1,048" && frame.outgoing == "\u{2013}")
        #expect(Self.near(frame.alpha, 0.5))
        #expect(Self.near(motion.frame(at: 1.1, reduceMotion: false).pale, 0.5))
        motion.update(.none, at: 5)
        #expect(motion.frame(at: 5.1, reduceMotion: false).text == "\u{2013}")
        #expect(motion.frame(at: 5.1, reduceMotion: false).outgoing == "1,048")
    }

    @Test func liveAndRestingCrossfadeTheColorEvenUnderReduceMotion() {
        var motion = OrbScoreMotion(Self.score(.live, 1286))
        #expect(motion.frame(at: 0, reduceMotion: false).pale == 0)
        motion.update(Self.score(.resting, 1286), at: 3)
        for reduceMotion in [false, true] {
            #expect(Self.near(motion.frame(at: 3.05, reduceMotion: reduceMotion).pale, 0.25))
            #expect(motion.frame(at: 3.2, reduceMotion: reduceMotion).pale == 1)
            #expect(motion.isMoving(at: 3.1, reduceMotion: reduceMotion))
            #expect(!motion.isMoving(at: 3.2, reduceMotion: reduceMotion))
        }
        #expect(motion.frame(at: 3.1, reduceMotion: false).outgoing == nil)
        var back = OrbScoreMotion(Self.score(.live, 1286))
        back.update(Self.score(.resting, 1286), at: 3)
        back.update(Self.score(.live, 1286), at: 3.05)
        #expect(Self.near(back.frame(at: 3.05, reduceMotion: false).pale, 0.25))
        #expect(back.frame(at: 3.1, reduceMotion: false).pale == 0)
    }

    @Test func reduceMotionCrossfadesEveryChangeFromTheValueThatWasShown() {
        var motion = OrbScoreMotion(Self.score(.live, 902))
        motion.update(Self.score(.live, 1286), at: 1)
        let frame = motion.frame(at: 1.075, reduceMotion: true)
        #expect(frame.text == "1,286" && frame.outgoing == "902")
        #expect(Self.near(frame.alpha, 0.5))
        #expect(motion.isMoving(at: 1.14, reduceMotion: true))
        #expect(!motion.isMoving(at: 1.15, reduceMotion: true))
        motion.update(Self.score(.live, 1100), at: 1.2)
        #expect(motion.frame(at: 1.25, reduceMotion: true).outgoing == "1,286")
    }

    @Test func aChangeDuringACountStartsFromTheIntegerItReached() {
        var motion = OrbScoreMotion(Self.score(.live, 902))
        motion.update(Self.score(.live, 1286), at: 0)
        let reached = PerfMotion.countValue(from: 902, to: 1286, elapsed: 0.2)
        motion.update(Self.score(.live, 1500), at: 0.2)
        #expect(motion.change(reduceMotion: false) == .count(seconds: PerfMotion.countSeconds(from: reached, to: 1500)))
        #expect(motion.frame(at: 0.2, reduceMotion: false).text == PerfFormat.compact(reached))
    }

    // MARK: Provisional mark

    @Test func theProvisionalMarkFadesAndAtOnceUnderReduceMotion() {
        var motion = OrbScoreMotion(Self.score(.live, 1048))
        #expect(motion.frame(at: 0, reduceMotion: false).provisional == 0)
        motion.update(Self.score(.live, 1048, provisional: true), at: 2)
        #expect(Self.near(motion.frame(at: 2.125, reduceMotion: false).provisional, 0.5))
        #expect(motion.frame(at: 2.25, reduceMotion: false).provisional == 1)
        #expect(motion.isMoving(at: 2.2, reduceMotion: false) && !motion.isMoving(at: 2.25, reduceMotion: false))
        #expect(motion.frame(at: 2, reduceMotion: true).provisional == 1)
        #expect(!motion.isMoving(at: 2, reduceMotion: true))
        var reversal = OrbScoreMotion(Self.score(.live, 1048))
        reversal.update(Self.score(.live, 1048, provisional: true), at: 0)
        reversal.update(Self.score(.live, 1048), at: 0.1)
        #expect(Self.near(reversal.frame(at: 0.1, reduceMotion: false).provisional, 0.4))
        #expect(reversal.frame(at: 0.2, reduceMotion: false).provisional == 0)
    }

    @Test func onlyAProvisionalScoreCarriesTheMarkAndNoneNever() {
        for form in [PerfValueForm.live, .resting] {
            #expect(OrbScoreMotion(Self.score(form, 1048, provisional: true)).frame(at: 0, reduceMotion: false).provisional == 1)
            #expect(OrbScoreMotion(Self.score(form, 1048)).frame(at: 0, reduceMotion: false).provisional == 0)
        }
        #expect(OrbScoreMotion(PerfSetReading(form: .none, shown: nil, provisional: true, count: 0, agentId: nil))
            .frame(at: 0, reduceMotion: false).provisional == 0)
    }

    @Test func aNewMotionIsStill() {
        for score in [Self.score(.live, 1286), Self.score(.resting, 1452, provisional: true), .none] {
            let motion = OrbScoreMotion(score)
            for time in [-1e9, 0, 1e9] {
                #expect(!motion.isMoving(at: time, reduceMotion: false))
                #expect(!motion.isMoving(at: time, reduceMotion: true))
            }
            #expect(motion.frame(at: 0, reduceMotion: false).alpha == 1)
            #expect(motion.frame(at: 0, reduceMotion: false).pale == (score.form == .live ? 0 : 1))
        }
    }

    @Test func theReadingsFramePolicyIncludesTheScoreButNotOnceGone() {
        var motion = OrbScoreMotion(Self.score(.live, 902))
        motion.update(Self.score(.live, 1286), at: 20)
        let reading = OrbLineReading(content: .intelligence(score: Self.score(.live, 1286), thinking: "12"), shownAt: 0,
                                     quietAt: 30, scoreMotion: motion)
        #expect(reading.motion(at: 20.3, reduceMotion: false).frames)
        #expect(!reading.motion(at: 21, reduceMotion: false).frames)
        let gone = OrbLineReading(content: reading.content, shownAt: 0, quietAt: 10, scoreMotion: motion)
        #expect(!gone.motion(at: 20.3, reduceMotion: false).frames)
    }
}
