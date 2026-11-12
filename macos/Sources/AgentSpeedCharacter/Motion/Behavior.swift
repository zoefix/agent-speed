import Foundation

/// What the agents of her orb are doing (spec/CHARACTERS.md, Behavior, Inputs).
public enum AgentActivity: String, Sendable, Equatable {
    case generating, tools, idle
}

/// One frame of what the app knows (motions/behavior.py, Inputs). Times are seconds.
public struct BehaviorInputs: Sendable, Equatable {
    /// `generating` when an agent is generating, else `tools` when one waits for a tool, else `idle`.
    public var phase: AgentActivity = .idle
    /// Seconds the request has waited for its first output; nil when not generating or once output flows.
    public var wait: Double?
    /// Seconds since the last response ended, while idle.
    public var idle = 0.0
    /// The local hour, 0–23.
    public var hour = 12
    public var newPeak = false
    public var trouble = false
    /// The pointer relative to her face center in her heights (x right, y down); nil when unknown.
    public var cursor: (x: Double, y: Double)?
    /// The pointer is over her figure.
    public var over = false
    /// A single click on her figure this frame.
    public var click = false

    public init(phase: AgentActivity = .idle, wait: Double? = nil, idle: Double = 0, hour: Int = 12,
                newPeak: Bool = false, trouble: Bool = false, cursor: (x: Double, y: Double)? = nil,
                over: Bool = false, click: Bool = false) {
        self.phase = phase
        self.wait = wait
        self.idle = idle
        self.hour = hour
        self.newPeak = newPeak
        self.trouble = trouble
        self.cursor = cursor
        self.over = over
        self.click = click
    }

    public static func == (a: BehaviorInputs, b: BehaviorInputs) -> Bool {
        a.phase == b.phase && a.wait == b.wait && a.idle == b.idle && a.hour == b.hour && a.newPeak == b.newPeak
            && a.trouble == b.trouble && a.cursor?.x == b.cursor?.x && a.cursor?.y == b.cursor?.y && a.over == b.over
            && a.click == b.click
    }
}

/// Her mood: event moods take over for a fixed time, state moods follow the agents' phase.
public enum CharacterMood: String, Sendable, CaseIterable {
    case poke, cheer, greet, trouble, think, work, focus, rest, wait

    /// Event moods, highest first, and how long each lasts.
    static let events: [(CharacterMood, Double)] = [(.poke, 1.6), (.cheer, 3.2), (.greet, 2.4), (.trouble, 2.5)]

    public var isEvent: Bool { CharacterMood.events.contains { $0.0 == self } }
    var isSocial: Bool { self == .wait || self == .work }
}

/// The pose a mood asks for: one of the pack's mood poses, or whatever pose shows already.
public enum MoodPose: Sendable, Equatable {
    case keep
    case state(String)
}

/// Moods, poses, faces, looks and actions from the inputs (motions/behavior.py, Behavior). The director asks for the
/// pose, the face, where she looks, whether she talks and which actions she plays.
public struct CharacterBehavior: Sendable {
    /// Full-body poses a pack may carry for the moods (manifest `states`).
    public static let stateNames = ["think", "wait", "cheer", "sleepy", "focus", "greet"]
    static let hold = 1.2
    static let thinkFace = 3.0, thinkPose = 10.0
    static let focusFace = 3.0, focusPose = 12.0
    static let waitPose = 20.0, waitSmile = 6.0, waitYawn = 180.0
    static let restIdle = 600.0, restNightIdle = 120.0, nightEnd = 6
    static let wake = 60.0
    static let pokeSurprise = 0.4
    static let entryDelay = 0.55
    /// She looks at a cursor within this many of her heights from her face.
    public static let lookRange = 2.0
    static let lookIn = 0.4, lookOut = 0.8
    static let headX = 0.7, headY = 0.5, headReach = 0.8
    static let eyeReach = 0.3
    static let up = (0.15, 0.3, 0.3, -0.8)
    static let gazeOn = 0.35, gazeOff = 0.25, gazeDwell = 0.5, gazeAxis = 0.75
    static let smileAfter = 0.8, gestureAfter = 4.0, goodbyeAfter = 1.0, attention = 8.0
    static let hoverGestures = ["wave", "peace", "thumbsup"]
    static let wink = 1.2
    static let moodFace: [CharacterMood: String] = [
        .cheer: "excited", .greet: "smile", .trouble: "worried", .think: "thinking", .focus: "focused", .rest: "sleepy",
    ]
    static let bandFace: [SpeedBand: String] = [.cruise: "smile", .boost: "smile", .overdrive: "focused",
                                                .redline: "excited"]
    /// The expressions actions name, as face patches; `talk` leaves the face as it is.
    static let actionFace: [String: String?] = ["smile": "smile", "happy": "excited", "surprised": "surprised",
                                                "determined": "focused", "sleepy": "sleepy", "talk": nil]
    static let actionOwnFace = ["yawn": "yawn", "giggle": "laugh"]
    static let moodPools: [CharacterMood: ([String], (Double, Double))] = [
        .think: (["chin", "tilt", "ponder"], (2.0, 4.0)), .focus: (["chin", "nod"], (3.0, 6.0)),
        .rest: (["yawn"], (20.0, 40.0)),
    ]
    static let waitLate: Set<String> = ["yawn", "stretch"]

    public internal(set) var rng: Rng
    public let states: [String]
    /// The hover gestures the shown pose can play and whether it has a wink (set by the director per pose).
    var hoverOptions: [String] = []
    var hasWink = false
    public private(set) var inputs = BehaviorInputs()
    public private(set) var t = 0.0
    public private(set) var phase = AgentActivity.idle
    public private(set) var phaseT = 0.0
    public private(set) var stateMood = CharacterMood.wait
    var stateT = CharacterBehavior.hold
    var until: [CharacterMood: Double] = [:]
    var started: [CharacterMood: Double] = [:]
    public private(set) var mood = CharacterMood.wait
    var moodT = 0.0
    var woken = -Double.infinity
    public private(set) var overT = 0.0
    var hoverDone = false
    var attentionUntil = -Double.infinity
    var faceOverride: String?
    var faceUntil = -Double.infinity
    var queue: [String] = []
    var queueAt = 0.0
    public private(set) var lookWeight = 0.0
    public private(set) var lookTarget = (0.0, 0.0, 0.0, 0.0)
    /// Where her eyes look as a gaze patch: `left`, `right`, `up`, `down`, or nil.
    public private(set) var gaze: String?
    var gazeSince = 0.0
    public internal(set) var talk: Talk

    public init(seed: UInt64, states: [String] = []) {
        rng = Rng(seed: seed &+ 1)
        self.states = CharacterBehavior.stateNames.filter(states.contains)
        talk = Talk()
    }

    // Moods

    func resting(_ i: BehaviorInputs) -> Bool {
        let night = 0 <= i.hour && i.hour < CharacterBehavior.nightEnd
        let longIdle = i.idle >= CharacterBehavior.restIdle || (night && i.idle >= CharacterBehavior.restNightIdle)
        return i.phase == .idle && longIdle && t - woken >= CharacterBehavior.wake
    }

    func stateWanted(_ i: BehaviorInputs) -> CharacterMood {
        switch i.phase {
        case .generating:
            if let wait = i.wait, wait >= CharacterBehavior.thinkFace { return .think }
            return .work
        case .tools:
            return phaseT >= CharacterBehavior.focusFace ? .focus : .work
        case .idle:
            return resting(i) ? .rest : .wait
        }
    }

    mutating func start(_ mood: CharacterMood, _ fresh: inout Set<CharacterMood>) {
        let lasts = CharacterMood.events.first { $0.0 == mood }!.1
        until[mood] = t + lasts
        started[mood] = t
        fresh.insert(mood)
    }

    /// The actions a mood starts with, and the earliest start.
    func entry(_ mood: CharacterMood, cameFrom: CharacterMood) -> ([String], Double) {
        switch mood {
        case .poke: return (["doubletake"], t)
        case .trouble: return (["chin", "shake"], t)
        case .cheer: return (states.contains("cheer") ? ["hop"] : ["jumpjoy", "hop"], t + CharacterBehavior.entryDelay)
        case .greet: return (states.contains("greet") ? [] : ["wave", "bow"], t + CharacterBehavior.entryDelay)
        case .wait where [.work, .think, .focus].contains(cameFrom): return (["nod"], t)
        case .rest: return (["yawn"], t + CharacterBehavior.entryDelay)
        default: return ([], t)
        }
    }

    mutating func moods(_ dt: Double, _ i: BehaviorInputs) {
        var fresh = Set<CharacterMood>()
        if i.phase != phase {
            if phase == .idle, stateMood == .rest { start(.greet, &fresh) }
            phase = i.phase
            phaseT = 0
        } else {
            phaseT += dt
        }
        if i.newPeak { start(.cheer, &fresh) }
        if i.trouble { start(.trouble, &fresh) }
        if i.click {
            if stateMood == .rest { woken = t }
            start(.poke, &fresh)
        }

        let previousState = stateMood
        stateT += dt
        let wanted = stateWanted(i)
        if wanted != stateMood, stateT >= CharacterBehavior.hold {
            stateMood = wanted
            stateT = 0
        }

        let before = mood
        mood = CharacterMood.events.first { (until[$0.0] ?? -Double.infinity) > t }?.0 ?? stateMood
        if mood != before {
            moodT = 0
            queue = []
        } else {
            moodT += dt
        }
        if fresh.contains(mood) || (mood == stateMood && stateMood != previousState) {
            (queue, queueAt) = entry(mood, cameFrom: previousState)
        }
    }

    // The cursor

    mutating func hover(_ dt: Double, _ i: BehaviorInputs, band: SpeedBand) {
        let social = mood.isSocial
        if i.over {
            overT += dt
            if overT >= CharacterBehavior.smileAfter { attentionUntil = t + CharacterBehavior.attention }
            if overT >= CharacterBehavior.gestureAfter, !hoverDone, social {
                hoverDone = true
                if !hoverOptions.isEmpty {
                    queue = [rng.choice(hoverOptions)]
                    queueAt = t
                } else if hasWink {
                    faceOverride = "wink"
                    faceUntil = t + CharacterBehavior.wink
                }
            }
            return
        }
        if overT >= CharacterBehavior.goodbyeAfter, social, band == .idle || band == .cruise {
            queue = ["wave", "nod"]
            queueAt = t
        }
        overT = 0
        hoverDone = false
    }

    func direction(_ dx: Double, _ dy: Double) -> String {
        var horizontal = abs(dx) >= abs(dy)
        if gaze == "left" || gaze == "right" {
            horizontal = abs(dx) >= CharacterBehavior.gazeAxis * abs(dy)
        } else if gaze == "up" || gaze == "down" {
            horizontal = CharacterBehavior.gazeAxis * abs(dx) > abs(dy)
        }
        if horizontal { return dx > 0 ? "right" : "left" }
        return dy > 0 ? "down" : "up"
    }

    /// Where she looks this mood: the user, up, or nowhere in particular.
    public var lookMode: String? {
        mood == .think ? "up" : (mood == .focus || mood == .rest) ? nil : "user"
    }

    mutating func look(_ dt: Double, _ i: BehaviorInputs) {
        let mode = lookMode
        let cursor = i.cursor.flatMap { hypot($0.x, $0.y) <= CharacterBehavior.lookRange ? $0 : nil }
        var on = true
        var wanted: String?
        if mode == "up" {
            lookTarget = CharacterBehavior.up
            wanted = "up"
        } else if mode == "user", let (dx, dy) = cursor {
            func clamp(_ v: Double) -> Double { max(-1, min(1, v)) }
            lookTarget = (CharacterBehavior.headX * clamp(dx / CharacterBehavior.headReach),
                          -CharacterBehavior.headY * clamp(dy / CharacterBehavior.headReach),
                          clamp(dx / CharacterBehavior.eyeReach), clamp(dy / CharacterBehavior.eyeReach))
            let far = max(abs(dx), abs(dy))
            if far >= (gaze != nil ? CharacterBehavior.gazeOff : CharacterBehavior.gazeOn) {
                wanted = direction(dx, dy)
            }
        } else if mode == "user", mood.isEvent {
            lookTarget = (0, 0, 0, 0)
        } else {
            on = false
        }
        if on {
            lookWeight = min(1, lookWeight + dt / CharacterBehavior.lookIn)
        } else {
            lookWeight = max(0, lookWeight - dt / CharacterBehavior.lookOut)
        }
        if wanted != gaze, t - gazeSince >= CharacterBehavior.gazeDwell {
            gaze = wanted
            gazeSince = t
        }
    }

    mutating func update(_ dt: Double, _ inputs: BehaviorInputs, band: SpeedBand) {
        t += dt
        self.inputs = inputs
        moods(dt, inputs)
        hover(dt, inputs, band: band)
        look(dt, inputs)
    }

    // What the director asks for

    /// The mood pose to show: a state the pack has, `keep`, or nil for the band pose.
    public func pose() -> MoodPose? {
        let i = inputs
        var name: String?
        switch mood {
        case .poke, .trouble: return .keep
        case .cheer: name = "cheer"
        case .greet: name = "greet"
        case .rest: name = "sleepy"
        case .think: if let wait = i.wait, wait >= CharacterBehavior.thinkPose { name = "think" }
        case .focus: if phaseT >= CharacterBehavior.focusPose { name = "focus" }
        case .wait: if i.idle >= CharacterBehavior.waitPose, t >= attentionUntil { name = "wait" }
        case .work: break
        }
        guard let name, states.contains(name) else { return nil }
        return .state(name)
    }

    /// The actions she picks from between others, the gap range, and the band whose signature actions join them (nil:
    /// the mood's own list); an event mood picks none.
    func pool(band: SpeedBand) -> ([String], (Double, Double), SpeedBand?) {
        if mood.isEvent { return ([], CharacterDirector.gaps[band]!, nil) }
        if let (pool, gap) = CharacterBehavior.moodPools[mood] { return (pool, gap, nil) }
        if mood == .wait {
            let late = inputs.idle >= CharacterBehavior.waitYawn
            let pool = CharacterDirector.pools[.idle]!.filter { late || !CharacterBehavior.waitLate.contains($0) }
            return (pool, CharacterDirector.gaps[.idle]!, .idle)
        }
        return (CharacterDirector.pools[band]!, CharacterDirector.gaps[band]!, band)
    }

    /// The gap range of `pool(band:)`.
    func gaps(band: SpeedBand) -> (Double, Double) {
        if mood.isEvent { return CharacterDirector.gaps[band]! }
        if let (_, gap) = CharacterBehavior.moodPools[mood] { return gap }
        return CharacterDirector.gaps[mood == .wait ? .idle : band]!
    }

    /// The queued actions to try in turn once no action plays, when their time has come.
    mutating func takeQueue() -> [String] {
        guard !queue.isEmpty, t >= queueAt else { return [] }
        let out = queue
        queue = []
        return out
    }

    /// The expression she means to show (a patch name, unchecked against the pose), or nil.
    func face(action: Motion?, eventFace: String?, band: SpeedBand) -> String? {
        if mood == .poke {
            return t - (started[.poke] ?? t) < CharacterBehavior.pokeSurprise ? "surprised" : "laugh"
        }
        if let action, action.name == "yawn" { return "yawn" }
        if !mood.isSocial { return CharacterBehavior.moodFace[mood] }
        if t < faceUntil { return faceOverride }
        if let action, let expression = action.expression, !expression.isEmpty {
            if let own = CharacterBehavior.actionOwnFace[action.name] { return own }
            if let mapped = CharacterBehavior.actionFace[expression] { return mapped }
            return expression
        }
        if mood == .work, let eventFace, !eventFace.isEmpty {
            if let mapped = CharacterBehavior.actionFace[eventFace] { return mapped }
            return eventFace
        }
        var face = mood == .work ? CharacterBehavior.bandFace[band]
            : (inputs.idle < CharacterBehavior.waitSmile ? "smile" : nil)
        if face == nil, overT >= CharacterBehavior.smileAfter { face = "smile" }
        return face
    }

    /// The cursor is over her in a mood that answers it: she starts nothing from her pool meanwhile.
    var attending: Bool { inputs.over && mood.isSocial }

    var talking: Bool { mood == .work && inputs.phase == .generating && inputs.wait == nil }
}

/// Anime lip flaps in phrases, not lip sync (motions/behavior.py, Talk): a group of 2–4 phrases of 3–8 syllables, a
/// pause between phrases, a silence after the group. A group waits until the face is clear of expressions before its
/// first syllable; `speaking` is true from then until its silence.
public struct Talk: Sendable {
    /// The open shapes and how often each comes (`mouth-i`, the teeth, never talks).
    public static let shapes: [(String, Double)] = [("a", 0.6), ("o", 0.25), ("u", 0.15)]
    static let phraseSyllables = (3, 6), groupPhrases = (2, 3)
    static let rate = (5.0, 6.0), rateGain = 0.1, rateMax = 7.0
    static let open = (0.55, 0.70)
    static let pause = (0.4, 1.2), silence = (2.0, 5.0)

    public enum Stage: Sendable {
        case ready, open, gap, pause, silence
    }

    public private(set) var on = false
    /// The open shape now, nil while closed.
    public private(set) var shape: String?
    public private(set) var stage: Stage?
    var left = 0.0
    var gap = 0.0
    var syllables = 0
    var phrases = 0
    var rate = Talk.rate.0

    public var speaking: Bool { stage != .silence }

    mutating func pick(_ shapes: [String], _ rng: inout Rng) -> String {
        var total = 0.0
        for (s, w) in Talk.shapes where shapes.contains(s) { total += w }
        var x = rng.random() * total
        var last = shapes[0]
        for (s, w) in Talk.shapes where shapes.contains(s) {
            x -= w
            if x < 0 { return s }
            last = s
        }
        return last
    }

    mutating func phrase(_ level: Int, _ rng: inout Rng) {
        syllables = Talk.phraseSyllables.0 + rng.index(Talk.phraseSyllables.1)
        rate = min(Talk.rateMax, rng.uniform(Talk.rate.0, Talk.rate.1) + Talk.rateGain * Double(level))
    }

    mutating func syllable(_ shapes: [String], _ rng: inout Rng) {
        shape = pick(shapes, &rng)
        let span = 1.0 / rate
        let opened = span * rng.uniform(Talk.open.0, Talk.open.1)
        stage = .open
        gap = span - opened
        left += opened
    }

    /// The open shape now, or nil for closed. `shapes` are the open mouth shapes the pose has, in `Talk.shapes` order.
    mutating func update(_ dt: Double, on talking: Bool, level: Int, shapes: [String], clear: Bool,
                         rng: inout Rng) -> String? {
        if !talking || shapes.isEmpty {
            on = false
            shape = nil
            stage = nil
            left = 0
            return nil
        }
        if !on {
            on = true
            stage = .ready
            left = 0
            phrases = Talk.groupPhrases.0 + rng.index(Talk.groupPhrases.1)
        }
        if stage == .ready {
            if !clear { return nil }
            left = 0
            phrase(level, &rng)
            syllable(shapes, &rng)
        }
        left -= dt
        while left <= 0 {
            switch stage {
            case .open:
                stage = .gap
                shape = nil
                left += gap
                syllables -= 1
            case .gap where syllables > 0:
                syllable(shapes, &rng)
            case .gap:
                phrases -= 1
                if phrases > 0 {
                    stage = .pause
                    left += rng.uniform(Talk.pause.0, Talk.pause.1)
                } else {
                    stage = .silence
                    left += rng.uniform(Talk.silence.0, Talk.silence.1)
                }
            case .pause:
                phrase(level, &rng)
                syllable(shapes, &rng)
            default:
                stage = .ready
                left = 0
                phrases = Talk.groupPhrases.0 + rng.index(Talk.groupPhrases.1)
                return shape
            }
        }
        return shape
    }
}
