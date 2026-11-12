import AgentSpeedCore
import Foundation

/// What the agents of a `character` orb are doing, for her moods (CHARACTERS 5, Behavior, Inputs): the phase of the
/// orb's set, when the request still waiting for its first output started, when the newest response ended, and
/// `trouble` events. Updated every engine tick from the orb's agents (METRICS 6.8, set A).
public struct CharacterActivity: Sendable {
    /// `generating` when an agent of the set is generating, else `tools` when one waits for a tool, else `idle`
    /// (the agents' phases already follow the 300 s staleness of METRICS 6.7).
    public private(set) var phase = AgentPhase.idle
    /// While generating and none of the generating agents has output since its in-flight request started: the
    /// earliest such start; nil otherwise.
    public private(set) var waitingSince: Date?
    /// The newest end of a response of the set; the app's start while there is none.
    public private(set) var idleSince: Date
    /// Counts `trouble`: an agent starts Looping (METRICS 6.10), or a tool result read live fails right after the
    /// previous result of that agent failed too.
    public private(set) var troubles = 0

    static let remembered = 512
    private var primed = false
    private var looping: [String: Bool] = [:]
    private var failures: [String: Date] = [:]

    public init(start: Date) {
        idleSince = start
    }

    /// Takes one engine tick's agents of the set; returns whether a `trouble` event fired.
    @discardableResult
    public mutating func update(agents: [AgentStatus], lastResponseEnd: Date? = nil) -> Bool {
        phase = agents.contains { $0.phase == .generating } ? .generating
            : agents.contains { $0.phase == .tools } ? .tools : .idle
        if phase == .generating {
            let generating = agents.filter { $0.phase == .generating }
            waitingSince = generating.contains { $0.outputSinceInFlight } ? nil
                : generating.compactMap(\.inFlightSince).min()
        } else {
            waitingSince = nil
        }
        for end in agents.compactMap(\.lastResponseEnd) + [lastResponseEnd].compactMap({ $0 }) {
            noteResponseEnd(end)
        }

        var fired = false
        var nextLooping: [String: Bool] = [:]
        for agent in agents {
            let loops = agent.signals.looping != nil
            if primed, loops, looping[agent.id] != true { fired = true }
            nextLooping[agent.id] = loops
            // The failure already met stays remembered, so an agent that leaves the list and returns never repeats it.
            if let failure = agent.repeatFailureAt, failures[agent.id] != failure {
                if primed { fired = true }
                failures[agent.id] = failure
            }
        }
        looping = nextLooping
        if failures.count > Self.remembered {
            let present = Set(agents.map(\.id))
            failures = failures.filter { present.contains($0.key) }
        }
        primed = true
        if fired { troubles &+= 1 }
        return fired
    }

    /// A response of the set ended (a `responseCompleted` event, or a newer end on a tick).
    public mutating func noteResponseEnd(_ end: Date) {
        if end > idleSince { idleSince = end }
    }
}

/// A single click (DESIGN 9, Orb, Switching 1, and CHARACTERS 5, Behavior, `click`): a press and release within 4 pt
/// and 0.5 s, fired once the system double-click time passed without a second click; a double-click and the end of a
/// drag never fire. Times in seconds, points in screen points.
public struct SingleClickRule: Sendable {
    public static let maxMovement = 4.0
    public static let maxPress = 0.5

    public var doubleClickInterval: Double
    private var press: (time: Double, x: Double, y: Double)?
    /// The click waiting for the double-click time: when it fires and where it was pressed.
    public private(set) var pending: (fireAt: Double, x: Double, y: Double)?

    public init(doubleClickInterval: Double) {
        self.doubleClickInterval = doubleClickInterval
    }

    /// A press; `armed` when it may start a click (on her figure's orb, not a control-click).
    public mutating func down(time: Double, x: Double, y: Double, clickCount: Int, armed: Bool = true) {
        if clickCount >= 2 {
            press = nil
            pending = nil
            return
        }
        press = armed ? (time, x, y) : nil
    }

    /// The release; `moved` when the press turned into a drag. Returns the delay until the click fires, or nil.
    @discardableResult
    public mutating func up(time: Double, x: Double, y: Double, moved: Bool) -> Double? {
        guard let press else { return nil }
        self.press = nil
        let distance = hypot(x - press.x, y - press.y)
        guard !moved, distance <= Self.maxMovement, time - press.time <= Self.maxPress else { return nil }
        let delay = max(0, doubleClickInterval - (time - press.time))
        pending = (time + delay, press.x, press.y)
        return delay
    }

    /// The pending click once its time came, taken once.
    public mutating func take(now: Double) -> (x: Double, y: Double)? {
        guard let p = pending, now >= p.fireAt - 1e-6 else { return nil }
        pending = nil
        return (p.x, p.y)
    }

    public mutating func cancel() {
        press = nil
        pending = nil
    }
}
