import Foundation

/// Something the director wants the scene to show, e.g. a puff of petals at a jump's take-off.
public enum DirectorEvent: String, Sendable {
    case petals
}

/// Turns the speed level into parameter values (motions/director.py): a groove per band on a beat clock, one action
/// at a time from the band's pool, hand acting, breathing, glances, blinks and wind, then springs. Side steps move the
/// character around its spot, away from the orb, and she stays where she lands.
///
/// A living pack (face patches or mood poses) also takes the app's inputs with every update: the behavior picks the
/// mood, and with it the actions, the face patches, the talking mouth and where she looks (Director+Living.swift).
/// Without inputs, or for any other pack, the director moves exactly as before.
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
    /// The pose on screen (a band, or a mood pose of a living pack), for its hand set.
    public private(set) var poseName = SpeedBand.idle.rawValue
    /// The band of the pose on screen; nil while a mood pose shows.
    public var poseBand: SpeedBand? { SpeedBand(rawValue: poseName) }
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

    /// The shown pose's variants: arm gestures by id, the ids of face edits, and its face patches.
    var meta: [String: RigDocument.Variant] = [:]
    var faceIDs: Set<String> = []
    var expressions = PatchGroup()
    var mouths = PatchGroup()
    var gazes = PatchGroup()
    /// The open mouth shapes the pose has, in `Talk.shapes` order.
    var talkShapes: [String] = []
    public internal(set) var behavior: CharacterBehavior?
    /// True for the frames that had inputs, in a living pack.
    public internal(set) var active = false
    public internal(set) var mood: CharacterMood?
    /// The expression and gaze patches shown, by name.
    public internal(set) var expression: String?
    public internal(set) var gazePatch: String?
    var snapFaces = false
    /// A face or gaze swap waits for a blink, or a patch is still fading.
    var facePending = false
    var blinkHold = 0.04
    var blinkSkipped = false
    var eventFace: String?
    var eventUntil = 0.0

    /// Whether a gesture is playing, and its name.
    public var activeGesture: String? { gesture == nil ? nil : lastGesture }

    public init(params: [CharacterParam], seed: UInt64 = 7, out: CharacterSide = .right, book: CharacterSide = .left,
                orb: Double = -1, celebrate: Bool = true, profile: MotionProfile = MotionProfile(),
                variants: [RigDocument.Variant] = [], states: [String] = []) {
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
        setVariants(variants)
        if !states.isEmpty || !expressions.isEmpty || !mouths.isEmpty || !gazes.isEmpty {
            behavior = CharacterBehavior(seed: seed, states: states)
            refreshBehaviorOptions()
        }
    }

    var springs: [SecondOrder?]
    var springLive: [Bool]
    private var grooveSeen: [Bool]

    /// Which arm swings free and which holds the object, for a new pose (of `poseBand`, for its hand set).
    public mutating func setSides(out: CharacterSide, book: CharacterSide, poseBand: SpeedBand? = nil) {
        setSides(out: out, book: book, pose: poseBand?.rawValue, variants: nil)
    }

    /// As `setSides(out:book:poseBand:)` for a pose by name (a band or a mood pose). With `variants`, the new pose's
    /// variants replace the old pose's: an action playing an old variant stops (the swap is edge-on) and the face
    /// patches take the new pose's at once.
    public mutating func setSides(out: CharacterSide, book: CharacterSide, pose: String?,
                                  variants: [RigDocument.Variant]?) {
        self.out = out
        self.book = book
        if let variants {
            if let g = gesture, g.motion.entries.contains(where: { $0.name.hasPrefix("Var.") }) { gesture = nil }
            setVariants(variants)
            refreshBehaviorOptions()
            snapFaces = true
        }
        if let pose { poseName = pose }
        let hand = profile.poseHands[poseName]
        for i in hands.indices {
            hands[i].holds = hands[i].side == book
            hands[i].setMode(hand)
        }
        armsHeld = hand.map(MotionProfile.twoHanded.contains) ?? false
        headHeld = hand.map(MotionProfile.atMouth.contains) ?? false
        for i in grooves.indices { grooves[i].target = 0 }
        grooves.append(GrooveLayer(band: band ?? .idle, w: 0, target: 1))
    }

    func can(_ names: String...) -> Bool { names.allSatisfy { space.isParam($0) } }

    func arm(_ gid: String) -> RigDocument.Variant? {
        guard let m = meta[gid], m.kind == "arm" else { return nil }
        return m
    }

    mutating func spec(_ gid: String) -> MotionSpec? {
        if grounded, CharacterDirector.airborne.contains(gid) { return nil }
        if let a = Gestures.armSpecs[gid] {
            guard let m = arm(gid) else { return nil }
            return Gestures.armGesture(m, hold: a.hold, flourish: a.flourish, expression: a.expression,
                                       tiptoe: a.tiptoe && can("FootPoint"))
        }
        if let c = Gestures.comboSpecs[gid] {
            guard let a = arm(c.book), let b = arm(c.free) else { return nil }
            return Gestures.combo(gid, book: a, free: b, hold: c.hold, expression: c.expression)
        }
        if gid == "jumpjoy" {
            guard can("PosY", "Squash") else { return nil }
            let cheer = spec("cheer")
            let body = Moves.jump(out, book, arms: cheer == nil)
            guard let cheer else { return body }
            return Keys.merge("jumpjoy", [cheer, body], delays: [0, 0.2], expression: "happy")
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
        if Gestures.face.contains(gid) {
            // A living pack yawns and hums in every pose: the face and the lips follow when the pose has patches.
            let needs = gid == "yawn" ? "sleepy" : "talk"
            let ok = faceIDs.contains(needs) || profile.signature.contains(gid) || active
            return ok ? Gestures.faceGesture(gid) : nil
        }
        return nil
    }

    @discardableResult
    mutating func startGesture(_ gid: String) -> Bool {
        guard profile.allows(gid), let spec = spec(gid) else { return false }
        let space = self.space
        let motion = spec.bind { space.slot($0) }
        let hard = motion.entries.map { e in CharacterDirector.hard.contains { e.name.hasPrefix($0) } }
        gesture = Playing(motion: motion, hard: hard)
        for entry in motion.entries where entry.name.contains("@") && springs[entry.slot] != nil {
            springs[entry.slot]!.snap(entry.curve.start)
            springLive[entry.slot] = true
        }
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
                eventFace = band == .redline ? "surprised" : "happy"
                eventUntil = time + 1.2
            } else {
                eventFace = "smile"
                eventUntil = time + 1.5
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

    /// The share of a parameter the playing action leaves to the layers below it.
    func idleShare(_ slot: Int) -> Double {
        if let g = gesture, g.motion.entries.contains(where: { $0.slot == slot }) { return 1 - g.w }
        return 1
    }

    /// Adds an idle motion of an appendage, scaled down while an action drives the same parameter, within its range.
    mutating func addIdle(_ slot: Int, _ v: Double) {
        guard let p = space.params[slot] else { return }
        write(slot, min(max(values[slot] + idleShare(slot) * v, p.min), p.max))
    }

    /// The gap range between actions: the band's, or the mood's in a living pack (without building the pool).
    func currentGaps() -> (Double, Double) {
        let band = self.band ?? .idle
        guard active, let behavior else { return CharacterDirector.gaps[band]! }
        return behavior.gaps(band: band)
    }

    /// The actions to pick from and the gap range: the band's, or the mood's in a living pack.
    func currentPool() -> ([String], (Double, Double)) {
        let band = self.band ?? .idle
        guard active, let behavior else {
            return (profile.pool(CharacterDirector.pools[band]!, band), CharacterDirector.gaps[band]!)
        }
        let (actions, gaps, signature) = behavior.pool(band: band)
        if let signature { return (profile.pool(actions, signature), gaps) }
        return (actions.filter { profile.allows($0) }, gaps)
    }

    mutating func applyGesture(_ dt: Double) -> Motion? {
        guard var g = gesture else {
            if active {
                for gid in behavior!.takeQueue() where startGesture(gid) { return nil }
            }
            if time >= nextGesture, !(active && behavior!.attending) {
                var pool = currentPool().0.filter { !recent.contains($0) }
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
            let gap = currentGaps()
            nextGesture = time + rng.uniform(gap.0 * profile.gap, gap.1 * profile.gap)
            return nil
        }
        gesture = g
        return m
    }

    /// Advances one frame; the parameters land in `values` (read the rig's own through `space`). `inputs` drive the
    /// moods of a living pack; without them nothing changes.
    public mutating func update(_ dt: Double, inputs: BehaviorInputs? = nil) {
        time += dt
        feel(dt, inputs)
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
        let playing = applyGesture(dt)
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
        if active {
            let target = behavior!.lookTarget
            look(slots.angleX, target.0)
            look(slots.angleY, target.1)
        }
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

        let openness: Double
        if active {
            let target = behavior!.lookTarget
            look(slots.eyeBallX, target.2)
            look(slots.eyeBallY, target.3)
            openness = livingFace(dt, playing)
        } else {
            openness = blink(dt)
        }
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
