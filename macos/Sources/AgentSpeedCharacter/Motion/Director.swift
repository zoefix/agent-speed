import Foundation

/// Something the director wants the scene to show, e.g. a puff of petals at a jump's take-off.
public enum DirectorEvent: String, Sendable {
    case petals
}

/// Turns the speed level into parameter values (motions/director.py): a groove per band on a beat clock, one action
/// at a time from the band's pool, hand acting, breathing, glances, blinks and wind, then springs. Side steps move the
/// character around its spot, away from the orb, and she stays where she lands.
public struct CharacterDirector: Sendable {
    static let pools: [SpeedBand: [String]] = [
        .idle: ["open", "chin", "hairtuck", "hug", "yawn", "stretch", "lookaround", "tilt", "foottap", "ponder", "step",
                "bow"],
        .cruise: ["wave", "hug", "present", "hairtuck", "hum", "nod", "tilt", "giggle", "sway", "tiptoe", "curtsy",
                  "step", "peek", "cuddle"],
        .boost: ["idea", "peace", "hip", "wave", "hop", "sway", "tiptoe", "step", "spin", "giggle", "doubletake",
                 "hooray", "eureka", "hops"],
        .overdrive: ["fist", "present", "hip", "idea", "hops", "run", "spin", "step", "doubletake", "cheer", "proud",
                     "hop"],
        .redline: ["fist", "raise", "peace", "jumpjoy", "run", "spin", "hops", "cheer", "hooray", "jump"],
    ]
    static let celebrations = ["jumpjoy", "spin", "cheer", "hop"]
    static let gaps: [SpeedBand: (Double, Double)] = [
        .idle: (2.4, 4.4), .cruise: (0.9, 2.2), .boost: (0.7, 1.8), .overdrive: (0.4, 1.3), .redline: (0.3, 1.0),
    ]
    static let hard = ["Var.", "Lift.", "Bend.", "Spin", "PosX"]
    static let stage = 0.55
    /// Actions that leave the ground or travel, skipped while `grounded` (Reduce Motion).
    static let airborne: Set<String> = ["hop", "jump", "hops", "spin", "step", "run", "jumpjoy", "pounce"]

    struct GrooveLayer: Sendable {
        var band: SpeedBand
        var w: Double
        var target: Double
    }

    struct Playing: Sendable {
        var motion: Motion
        var hard: [Bool]
        var t = 0.0
        var w = 0.0
        var fired = 0
    }

    public let space: ParamSpace
    let slots: Slots
    public let profile: MotionProfile
    /// The band of the pose on screen, for its hand set.
    public private(set) var poseBand: SpeedBand = .idle
    /// Groove scale per slot (the profile's bounce, sway and energy) and the slots a seated character never moves.
    private let grooveScale: [Double]
    private let frozen: [Int]
    /// The arm slots, held at their defaults while the pose's hand set plays a two-handed instrument.
    private let armSlots: [Int]
    private var armsHeld = false
    /// The head angle slots, held at their defaults while the pose's hand set plays an instrument at the lips.
    private let headSlots: [Int]
    private var headHeld = false
    public private(set) var out: CharacterSide
    public private(set) var book: CharacterSide
    public let orb: Double
    public let celebrate: Bool
    /// Skips the actions that leave the ground (DESIGN 2a.6, Reduce Motion).
    public var grounded = false
    var rng: Rng
    var beat = Beat()
    var grooves: [GrooveLayer] = []
    var gesture: Playing?
    var nextGesture = 2.0
    public private(set) var lastGesture: String?
    var recent: [String] = []
    public private(set) var stageX = 0.0
    public var events: [DirectorEvent] = []
    public private(set) var band: SpeedBand?
    public private(set) var level = 0
    public private(set) var intensity = 0.0
    var blinkAt = 1.2
    var blinkT: Double?
    var gaze = (0.0, 0.0)
    var gazeTarget = (0.0, 0.0)
    var gazeAt = 0.8
    var breathPhase = 0.0
    public private(set) var time = 0.0
    var hands: [HandActor] = []
    /// Actions started so far (gestures and hand fidgets); starting one allocates its curves.
    var motionStarts = 0
    /// The parameters of the last update, by slot; `present` marks the ones written this frame.
    public private(set) var values: [Double]
    var present: [Bool]
    private var grooveOut: Groove.Out = []
    private var grooveSum: [Double]
    private var grooveWeight: [Double]
    private var grooveTouched: [Int] = []

    /// Whether a gesture is playing, and its name.
    public var activeGesture: String? { gesture == nil ? nil : lastGesture }

    public init(params: [CharacterParam], seed: UInt64 = 7, out: CharacterSide = .right, book: CharacterSide = .left,
                orb: Double = -1, celebrate: Bool = true, profile: MotionProfile = MotionProfile()) {
        space = ParamSpace(params: params)
        slots = Slots(space)
        self.profile = profile
        let space = self.space
        grooveScale = space.names.map { profile.grooveScale($0) }
        frozen = profile.seated ? MotionProfile.seatedFreeze.compactMap { space.isParam($0) ? space.slot($0) : nil } : []
        armSlots = space.names.indices.filter { i in
            space.params[i] != nil && MotionProfile.armParams.contains { space.names[i].hasPrefix($0) }
        }
        armsHeld = profile.poseHands[SpeedBand.idle.rawValue].map(MotionProfile.twoHanded.contains) ?? false
        headSlots = MotionProfile.headParams.compactMap { space.isParam($0) ? space.slot($0) : nil }
        headHeld = profile.poseHands[SpeedBand.idle.rawValue].map(MotionProfile.atMouth.contains) ?? false
        beat = Beat(tempo: profile.tempo)
        self.out = out
        self.book = book
        self.orb = orb
        self.celebrate = celebrate
        rng = Rng(seed: seed)
        values = [Double](repeating: 0, count: space.count)
        present = [Bool](repeating: false, count: space.count)
        grooveSum = [Double](repeating: 0, count: space.count)
        grooveWeight = [Double](repeating: 0, count: space.count)
        grooveSeen = [Bool](repeating: false, count: space.count)
        springs = space.springs.map { $0.map { SecondOrder($0) } }
        springLive = [Bool](repeating: false, count: space.count)
        grooveOut.reserveCapacity(32)
        grooveTouched.reserveCapacity(32)
        events.reserveCapacity(8)
        for (k, side) in CharacterSide.allCases.enumerated() where space.isParam("Finger.\(side.rawValue).0") {
            var hand = HandActor(side: side, holds: side == book, phase: Double(k) * 1.7, rng: &rng)
            hand.freeSets = profile.hands
            hand.setMode(profile.poseHands[SpeedBand.idle.rawValue])
            hands.append(hand)
        }
    }

    var springs: [SecondOrder?]
    var springLive: [Bool]
    private var grooveSeen: [Bool]

    /// Which arm swings free and which holds the object, for a new pose (of `poseBand`, for its hand set).
    public mutating func setSides(out: CharacterSide, book: CharacterSide, poseBand: SpeedBand? = nil) {
        self.out = out
        self.book = book
        if let poseBand { self.poseBand = poseBand }
        for i in hands.indices {
            hands[i].holds = hands[i].side == book
            hands[i].setMode(profile.poseHands[self.poseBand.rawValue])
        }
        armsHeld = profile.poseHands[self.poseBand.rawValue].map(MotionProfile.twoHanded.contains) ?? false
        headHeld = profile.poseHands[self.poseBand.rawValue].map(MotionProfile.atMouth.contains) ?? false
        for i in grooves.indices { grooves[i].target = 0 }
        grooves.append(GrooveLayer(band: band ?? .idle, w: 0, target: 1))
    }

    func can(_ names: String...) -> Bool { names.allSatisfy { space.isParam($0) } }

    mutating func spec(_ gid: String) -> MotionSpec? {
        if Gestures.arm.contains(gid) || Gestures.combos.contains(gid) { return nil }
        if grounded, CharacterDirector.airborne.contains(gid) { return nil }
        if gid == "jumpjoy" {
            return can("PosY", "Squash") ? Moves.jump(out, book, arms: true) : nil
        }
        if gid == "step" {
            guard can("PosX", "PosY") else { return nil }
            let away = -orb * CharacterDirector.stage
            let targets = [0.0, 0.45 * away, away].filter { abs($0 - stageX) > 0.1 }
            return Moves.sidestep(out, book, rng.choice(targets) - stageX)
        }
        if gid == "peek" { return can("Lean") ? Moves.peek(out, book, orb) : nil }
        if gid == "pounce" { return can("PosY", "Squash") ? Moves.pounce(out, book, orb) : nil }
        if let needs = Moves.profile[gid] {
            return needs.contains { space.isParam($0) } ? Moves.profileMove(gid, out: out, book: book) : nil
        }
        if Moves.names.contains(gid) { return can("PosY", "Squash") ? Moves.make(gid, out: out, book: book) : nil }
        if Gestures.head.contains(gid) { return Gestures.headGesture(gid) }
        if Gestures.face.contains(gid), profile.signature.contains(gid) { return Gestures.faceGesture(gid) }
        return nil
    }

    @discardableResult
    mutating func startGesture(_ gid: String) -> Bool {
        guard profile.allows(gid), let spec = spec(gid) else { return false }
        let space = self.space
        let motion = spec.bind { space.slot($0) }
        let hard = motion.entries.map { e in CharacterDirector.hard.contains { e.name.hasPrefix($0) } }
        gesture = Playing(motion: motion, hard: hard)
        motionStarts += 1
        lastGesture = gid
        recent = Array((recent + [gid]).suffix(3))
        return true
    }

    var restIdle: Bool { profile.idle == "rest" }

    mutating func playGroove(_ band: SpeedBand) {
        for i in grooves.indices { grooves[i].target = 0 }
        grooves.append(GrooveLayer(band: band, w: grooves.isEmpty ? 1 : 0, target: 1))
    }

    public mutating func setLevel(_ level: Int) {
        let band = SpeedBand(level: level)
        if self.band == nil {
            self.band = band
            playGroove(band)
        } else if band != self.band {
            let rising = band > self.band!
            self.band = band
            playGroove(band)
            if rising {
                if celebrate, gesture == nil || gesture!.t > 0.5 * gesture!.motion.duration {
                    for gid in rng.sample(CharacterDirector.celebrations, CharacterDirector.celebrations.count) {
                        if startGesture(gid) { break }
                    }
                }
            }
            nextGesture = time + 1.5
        }
        self.level = level
    }

    mutating func blendGrooves(_ dt: Double) {
        for i in grooves.indices {
            grooves[i].w += max(-dt / 0.5, min(dt / 0.5, grooves[i].target - grooves[i].w))
        }
        grooves.removeAll { !($0.w > 1e-3 || $0.target > 0) }
        grooveTouched.removeAll(keepingCapacity: true)
        let o = slots.side(out), k = slots.side(book)
        for layer in grooves {
            Groove.emit(layer.band, beat.phase, time, slots, out: o, book: k, rest: restIdle, into: &grooveOut)
            for (slot, raw) in grooveOut {
                let v = raw * grooveScale[slot]
                if !grooveSeen[slot] {
                    grooveSeen[slot] = true
                    grooveTouched.append(slot)
                    grooveSum[slot] = 0
                    grooveWeight[slot] = 0
                }
                grooveSum[slot] += v * layer.w
                grooveWeight[slot] += layer.w
            }
        }
        for slot in grooveTouched {
            let wsum = grooveWeight[slot]
            let w = min(1.0, wsum)
            write(slot, values[slot] * (1 - w) + grooveSum[slot] / max(wsum, 1e-6) * w)
            grooveSeen[slot] = false
        }
    }

    @inline(__always) mutating func write(_ slot: Int, _ v: Double) {
        values[slot] = v
        present[slot] = true
    }

    /// Adds an idle motion of an appendage, scaled down while an action drives the same parameter, within its range.
    mutating func addIdle(_ slot: Int, _ v: Double) {
        guard let p = space.params[slot] else { return }
        var share = 1.0
        if let g = gesture, g.motion.entries.contains(where: { $0.slot == slot }) { share = 1 - g.w }
        write(slot, min(max(values[slot] + share * v, p.min), p.max))
    }

    mutating func applyGesture(_ dt: Double) -> Motion? {
        guard var g = gesture else {
            if time >= nextGesture {
                var pool = profile.pool(CharacterDirector.pools[band!]!, band!).filter { !recent.contains($0) }
                rng.shuffle(&pool)
                for gid in pool where startGesture(gid) { break }
                nextGesture = time + 1.0
            }
            return nil
        }
        g.t += dt
        let m = g.motion
        g.w = min(min(1.0, g.t / m.fade), max(0.0, (m.duration - g.t) / m.fade))
        let local = m.localTime(g.t)
        for (i, entry) in m.entries.enumerated() {
            let v = entry.curve(local)
            write(entry.slot, g.hard[i] ? v : values[entry.slot] * (1 - g.w) + v * g.w)
        }
        while g.fired < m.events.count, m.events[g.fired].time <= g.t {
            if let event = DirectorEvent(rawValue: m.events[g.fired].name) { events.append(event) }
            g.fired += 1
        }
        if g.t >= m.duration {
            stageX += m.travel
            gesture = nil
            let gap = CharacterDirector.gaps[band!]!
            nextGesture = time + rng.uniform(gap.0 * profile.gap, gap.1 * profile.gap)
            return nil
        }
        gesture = g
        return m
    }

    /// Advances one frame; the parameters land in `values` (read the rig's own through `space`).
    public mutating func update(_ dt: Double) {
        time += dt
        intensity += (Double(level) / 13 - intensity) * min(1.0, dt * 2.5)
        let s = intensity, t = time
        beat.update(dt, band ?? .idle)
        for i in 0..<space.count {
            if let p = space.params[i] {
                values[i] = p.default
                present[i] = true
            } else {
                values[i] = 0
                present[i] = false
            }
        }
        blendGrooves(dt)
        _ = applyGesture(dt)
        write(slots.posX, values[slots.posX] + stageX)
        if armsHeld {
            for slot in armSlots { write(slot, space.params[slot]!.default) }
        }
        for h in hands.indices {
            let side = slots.side(hands[h].side)
            var arm = 0.0
            arm += values[side.arm]
            arm += values[side.forearm]
            arm += values[side.lift]
            arm += values[side.bend]
            let idle = hands[h].action == nil
            hands[h].update(dt, time: time, band: band ?? .idle, beat: 60 / beat.bpm, arm: arm, rng: &rng)
            if idle, hands[h].action != nil { motionStarts += 1 }
            for i in 0..<5 { write(side.fingers[i], values[side.fingers[i]] + hands[h].fingers[i]) }
            write(side.spread, values[side.spread] + hands[h].spread)
            write(side.wrist, values[side.wrist] + hands[h].wrist)
        }

        breathPhase += dt / (4.2 - 2.4 * s)
        write(slots.breath, max(values[slots.breath], 0.5 - 0.5 * cos(2 * Double.pi * breathPhase)))
        let amp = 0.06 + 0.12 * s
        write(slots.angleX, values[slots.angleX] + amp * (sin(t * 0.83) + 0.5 * sin(t * 1.91 + 1.3)))
        write(slots.angleY, values[slots.angleY] + amp * 0.7 * sin(t * 1.17 + 0.4))
        write(slots.angleZ, values[slots.angleZ] + amp * 0.8 * sin(t * 0.61 + 2.1))
        write(slots.bodySway, values[slots.bodySway] + (0.15 + 0.3 * s) * sin(t * (0.9 + 1.6 * s)))
        write(slots.wind, min(1.0, pow(s, 1.2) * 1.05))
        // The idle swish and flutter give way to an action that moves the tail or the wings.
        if space.isParam(slots.tailSwish) {
            addIdle(slots.tailSwish, (0.18 + 0.3 * s) * profile.sway * sin(Double.pi * beat.phase + 0.5))
        }
        if space.isParam(slots.wingFlap) { addIdle(slots.wingFlap, 0.12 * sin(2 * Double.pi * breathPhase)) }
        for slot in frozen { write(slot, space.params[slot]!.default) }
        if headHeld {
            for slot in headSlots { write(slot, space.params[slot]!.default) }
        }
        applySprings(dt)

        gazeAt -= dt
        if gazeAt <= 0 {
            let gx = rng.uniform(-0.5, 0.5)
            let gy = rng.uniform(-0.35, 0.35)
            gazeTarget = (gx, gy)
            gazeAt = rng.uniform(0.9, 2.6) * (1.2 - 0.6 * s)
        }
        let k = min(1.0, dt * 14)
        gaze = (gaze.0 + (gazeTarget.0 - gaze.0) * k, gaze.1 + (gazeTarget.1 - gaze.1) * k)
        write(slots.eyeBallX, max(-1, min(1, values[slots.eyeBallX] + 0.35 * gaze.0)))
        write(slots.eyeBallY, max(-1, min(1, values[slots.eyeBallY] + 0.35 * gaze.1)))

        let openness = blink(dt)
        for slot in slots.eyeOpen where present[slot] {
            values[slot] = min(values[slot], openness)
        }
    }

    mutating func applySprings(_ dt: Double) {
        for i in 0..<space.count where present[i] && space.params[i] != nil {
            guard springs[i] != nil else { continue }
            if !springLive[i] {
                springs[i]!.snap(values[i])
                springLive[i] = true
            }
            values[i] = springs[i]!.update(dt, values[i])
        }
    }

    mutating func blink(_ dt: Double) -> Double {
        blinkAt -= dt
        if blinkT == nil, blinkAt <= 0 { blinkT = 0 }
        guard var bt = blinkT else { return 1 }
        bt += dt
        blinkT = bt
        if bt < 0.07 { return 1 - bt / 0.07 }
        if bt < 0.11 { return 0 }
        if bt < 0.2 { return (bt - 0.11) / 0.09 }
        blinkT = nil
        blinkAt = rng.random() > 0.15 ? rng.uniform(2.0, 5.0) : 0.25
        return 1
    }

    /// The value of a parameter after the last update, or nil when the director does not know it.
    public func value(_ name: String) -> Double? {
        space.slot(name).map { values[$0] }
    }
}
