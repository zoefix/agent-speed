import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation
import Testing

/// The agents' inputs of a living character (CHARACTERS 5, Behavior, Inputs) from an orb's agents.
@Suite struct CharacterActivityTests {
    static let start = Date(timeIntervalSince1970: 1_800_000_000)

    static func agent(_ id: String, _ phase: AgentPhase, inFlight: Double? = nil, output: Bool = false,
                      lastEnd: Double? = nil, looping: Int? = nil, repeatFailure: Double? = nil) -> AgentStatus {
        var a = AgentStatus(id: id, source: .claudeCode, project: "p", model: nil, phase: phase, isSubagent: false,
                            live: .zero, recentSpeed: nil, lastActivity: start,
                            signals: WorkSignals(looping: looping))
        a.inFlightSince = inFlight.map { start.addingTimeInterval($0) }
        a.outputSinceInFlight = output
        a.lastResponseEnd = lastEnd.map { start.addingTimeInterval($0) }
        a.repeatFailureAt = repeatFailure.map { start.addingTimeInterval($0) }
        return a
    }

    @Test func thePhaseIsGeneratingThenToolsThenIdle() {
        var activity = CharacterActivity(start: Self.start)
        activity.update(agents: [Self.agent("a", .tools), Self.agent("b", .generating, inFlight: 5)])
        #expect(activity.phase == .generating)
        activity.update(agents: [Self.agent("a", .tools), Self.agent("b", .idle)])
        #expect(activity.phase == .tools)
        activity.update(agents: [Self.agent("a", .idle)])
        #expect(activity.phase == .idle)
        activity.update(agents: [])
        #expect(activity.phase == .idle)
    }

    @Test func theWaitRunsFromTheEarliestRequestUntilAnyOutput() {
        var activity = CharacterActivity(start: Self.start)
        activity.update(agents: [Self.agent("a", .generating, inFlight: 12), Self.agent("b", .generating, inFlight: 7)])
        #expect(activity.waitingSince == Self.start.addingTimeInterval(7))
        activity.update(agents: [Self.agent("a", .generating, inFlight: 12),
                                 Self.agent("b", .generating, inFlight: 7, output: true)])
        #expect(activity.waitingSince == nil)
        activity.update(agents: [Self.agent("a", .tools, inFlight: 12)])
        #expect(activity.waitingSince == nil)
    }

    @Test func idleCountsFromTheNewestEndOrTheStart() {
        var activity = CharacterActivity(start: Self.start)
        activity.update(agents: [Self.agent("a", .idle)])
        #expect(activity.idleSince == Self.start)
        activity.update(agents: [Self.agent("a", .idle, lastEnd: 40), Self.agent("b", .idle, lastEnd: 25)])
        #expect(activity.idleSince == Self.start.addingTimeInterval(40))
        activity.noteResponseEnd(Self.start.addingTimeInterval(55))
        activity.update(agents: [], lastResponseEnd: Self.start.addingTimeInterval(50))
        #expect(activity.idleSince == Self.start.addingTimeInterval(55), "an older end never moves it back")
    }

    @Test func troubleFiresWhenAnAgentStartsLoopingNotWhenTheOrbFirstSeesIt() {
        var activity = CharacterActivity(start: Self.start)
        let atFirstSight = activity.update(agents: [Self.agent("a", .tools, looping: 3), Self.agent("b", .tools)])
        #expect(!atFirstSight)
        let stillLooping = activity.update(agents: [Self.agent("a", .tools, looping: 4), Self.agent("b", .tools)])
        #expect(!stillLooping)
        let otherStarts = activity.update(agents: [Self.agent("a", .tools, looping: 4), Self.agent("b", .tools, looping: 3)])
        #expect(otherStarts)
        let firstStops = activity.update(agents: [Self.agent("a", .tools), Self.agent("b", .tools, looping: 3)])
        #expect(!firstStops)
        let firstAgain = activity.update(agents: [Self.agent("a", .tools, looping: 3), Self.agent("b", .tools, looping: 3)])
        #expect(firstAgain)
        #expect(activity.troubles == 2)
    }

    @Test func troubleFiresOnceForEachFailureRightAfterAFailure() {
        var activity = CharacterActivity(start: Self.start)
        let atFirstSight = activity.update(agents: [Self.agent("a", .tools, repeatFailure: 10)])
        #expect(!atFirstSight)
        let same = activity.update(agents: [Self.agent("a", .tools, repeatFailure: 10)])
        #expect(!same)
        let newer = activity.update(agents: [Self.agent("a", .tools, repeatFailure: 14)])
        #expect(newer)
        let cleared = activity.update(agents: [Self.agent("a", .generating)])
        #expect(!cleared)
        let next = activity.update(agents: [Self.agent("a", .tools, repeatFailure: 20)])
        #expect(next)
    }
}

/// A single click on the orb (DESIGN 9 Switching 1, CHARACTERS 5 `click`): versus a double-click and a drag.
@Suite struct SingleClickRuleTests {
    @Test func aQuickClickFiresOnceTheDoubleClickTimePassed() {
        var rule = SingleClickRule(doubleClickInterval: 0.5)
        rule.down(time: 10, x: 100, y: 200, clickCount: 1)
        let delay = rule.up(time: 10.1, x: 102, y: 201, moved: false)
        #expect(delay.map { abs($0 - 0.4) < 1e-9 } == true)
        let early = rule.take(now: 10.3)
        #expect(early == nil)
        let click = rule.take(now: 10.5)
        #expect(click?.x == 100 && click?.y == 200, "the press point")
        let again = rule.take(now: 11)
        #expect(again == nil, "taken once")
    }

    @Test func aDoubleClickNeverFires() {
        var rule = SingleClickRule(doubleClickInterval: 0.5)
        rule.down(time: 10, x: 100, y: 200, clickCount: 1)
        rule.up(time: 10.08, x: 100, y: 200, moved: false)
        rule.down(time: 10.2, x: 100, y: 200, clickCount: 2)
        let second = rule.up(time: 10.28, x: 100, y: 200, moved: false)
        #expect(second == nil)
        let pending = rule.take(now: 11)
        #expect(pending == nil)
    }

    @Test func aDragALongPressAndAWanderingPressNeverFire() {
        var rule = SingleClickRule(doubleClickInterval: 0.5)
        rule.down(time: 10, x: 100, y: 200, clickCount: 1)
        let dragged = rule.up(time: 10.1, x: 100, y: 200, moved: true)
        #expect(dragged == nil)
        rule.down(time: 20, x: 100, y: 200, clickCount: 1)
        let held = rule.up(time: 20.6, x: 100, y: 200, moved: false)
        #expect(held == nil)
        rule.down(time: 30, x: 100, y: 200, clickCount: 1)
        let wandered = rule.up(time: 30.1, x: 105, y: 200, moved: false)
        #expect(wandered == nil)
        rule.down(time: 40, x: 100, y: 200, clickCount: 1, armed: false)
        let unarmed = rule.up(time: 40.1, x: 100, y: 200, moved: false)
        #expect(unarmed == nil)
        let late = rule.take(now: 50)
        #expect(late == nil)
    }
}
