import Foundation

/// The face patches of one kind (`expr-*`, `mouth-*` or `gaze-*`) a pose has: names, slots and shown weights.
struct PatchGroup: Sendable {
    var names: [String] = []
    var slots: [Int?] = []
    var weights: [Double] = []

    var isEmpty: Bool { names.isEmpty }

    func contains(_ name: String?) -> Bool { name.map(names.contains) ?? false }

    mutating func add(_ name: String, _ slot: Int?) {
        if let i = names.firstIndex(of: name) {
            slots[i] = slot
            return
        }
        names.append(name)
        slots.append(slot)
        weights.append(0)
    }
}

/// The living pack (spec/CHARACTERS.md, Behavior; motions/director.py): moods from the inputs, face patches swapped
/// while she blinks, lip flaps through the mouth patches, and the head and eyes turned toward where she looks.
extension CharacterDirector {
    /// Face patch rates (seconds for a full swap); a blink over a patch shows `gaze-closed` while the eyes are half
    /// shut. Mouth shapes switch on a frame: a blend of two mouths would show both.
    static let patchFade = 0.05, blinkFade = 0.03
    /// Expressions whose eyes are shut already: no blink over them.
    static let eyesShut: Set<String> = ["laugh", "yawn"]
    /// A dozing blink holds the eyes shut this long.
    static let doze = (0.5, 1.2)

    /// Whether the pack is living (face patches or mood poses): only then do inputs change anything.
    public var isLiving: Bool { behavior != nil }

    /// The open mouth shape this frame (`a`, `o`, `u`), nil while closed or silent.
    public var mouthShape: String? { active ? behavior?.talk.shape : nil }

    /// Whether she speaks (a group of phrases until its silence) this frame.
    public var isTalking: Bool { active && behavior?.talk.on == true }

    /// Whether the living face or look moves this frame (DESIGN 2a.6, Frame policy): a face or gaze swap, a phrase
    /// being spoken, the look turning on or off, or (`following`) the head following a cursor that just moved.
    public func isLivingBusy(following: Bool) -> Bool {
        guard active, let b = behavior else { return false }
        if facePending { return true }
        if b.talk.on, let stage = b.talk.stage, stage == .open || stage == .gap || stage == .pause { return true }
        if b.lookWeight > 0, b.lookWeight < 1 { return true }
        return following && b.lookMode == "user" && b.lookWeight > 0
    }

    /// The mood pose to show in a living pack fed inputs, else nil: the band pose.
    public func poseWanted() -> MoodPose? {
        active ? behavior?.pose() : nil
    }

    mutating func setVariants(_ variants: [RigDocument.Variant]) {
        meta = [:]
        faceIDs = []
        expressions = PatchGroup()
        mouths = PatchGroup()
        gazes = PatchGroup()
        for v in variants {
            meta[v.id] = v
            if v.kind == "face" { faceIDs.insert(v.id) }
            guard v.kind == "patch", let dash = v.id.firstIndex(of: "-") else { continue }
            let kind = v.id[..<dash], name = String(v.id[v.id.index(after: dash)...])
            let slot = space.slot(v.param)
            switch kind {
            case "expr": expressions.add(name, slot)
            case "mouth": mouths.add(name, slot)
            case "gaze": gazes.add(name, slot)
            default: break
            }
        }
        talkShapes = Talk.shapes.map(\.0).filter(mouths.names.contains)
    }

    /// What the behavior asks of the shown pose before it queues a hover reaction.
    mutating func refreshBehaviorOptions() {
        guard behavior != nil else { return }
        behavior!.hoverOptions = CharacterBehavior.hoverGestures.filter { gid in
            Gestures.armSpecs[gid] != nil && profile.allows(gid) && arm(gid) != nil
        }
        behavior!.hasWink = expressions.contains("wink")
    }

    mutating func feel(_ dt: Double, _ inputs: BehaviorInputs?) {
        active = behavior != nil && inputs != nil
        guard active, let inputs else { return }
        behavior!.update(dt, inputs, band: band ?? .idle)
        if behavior!.mood != mood {
            mood = behavior!.mood
            let gap = currentGaps()
            nextGesture = time + rng.uniform(gap.0 * profile.gap, gap.1 * profile.gap)
        }
    }

    /// Blends a head or eye parameter toward where she looks, except the share a playing action moves.
    mutating func look(_ slot: Int, _ target: Double) {
        let w = behavior!.lookWeight
        guard w > 0 else { return }
        let k = w * idleShare(slot)
        write(slot, values[slot] * (1 - k) + target * k)
    }

    func slewed(_ w: Double, _ target: Double, _ dt: Double, _ seconds: Double) -> Double {
        snapFaces ? target : w + max(-dt / seconds, min(dt / seconds, target - w))
    }

    /// Face patches: one expression, or the gaze where she looks, swapped while she blinks; a blink over a patch shows
    /// `gaze-closed`; the lips move through the mouth patches while she talks. Returns how open the eyes are.
    mutating func livingFace(_ dt: Double, _ gesture: Motion?) -> Double {
        let b = behavior!
        let yawning = gesture?.name == "yawn"
        var talk = (b.talking && level >= 1) || gesture?.talk == true
        talk = talk && profile.talk && !talkShapes.isEmpty && !yawning
        // While she speaks (a group of phrases until its silence) the face is drawn: no expression, no gaze.
        let speaking = talk && b.talk.speaking
        let event = time < eventUntil ? eventFace : nil
        var wanted = speaking ? nil : b.face(action: gesture, eventFace: event, band: band ?? .idle)
        if !expressions.contains(wanted) { wanted = nil }
        let gaze = !speaking && wanted == nil && gazes.contains(b.gaze) ? b.gaze : nil
        let change = wanted != expression || gaze != gazePatch
        let openness = dozeBlink(dt, force: change, doze: b.mood == .rest)
        if snapFaces || (change && (openness <= 0.02 || blinkSkipped)) {
            expression = wanted
            gazePatch = gaze
        }

        let covered = expression != nil || gazePatch != nil
        let blinking = covered && !(expression.map(CharacterDirector.eyesShut.contains) ?? false) && openness < 0.5
        for i in expressions.names.indices {
            let w = slewed(expressions.weights[i], expressions.names[i] == expression ? 1 : 0, dt,
                           CharacterDirector.patchFade)
            expressions.weights[i] = w
            if let slot = expressions.slots[i] { write(slot, w) }
        }
        for i in gazes.names.indices {
            let closed = gazes.names[i] == "closed"
            let target: Double = closed ? (blinking ? 1 : 0) : (gazes.names[i] == gazePatch ? 1 : 0)
            let w = slewed(gazes.weights[i], target, dt, closed ? CharacterDirector.blinkFade : CharacterDirector.patchFade)
            gazes.weights[i] = w
            if let slot = gazes.slots[i] { write(slot, w) }
        }

        let clear = expression == nil && expressions.weights.allSatisfy { $0 <= 0 }
        var shape = behavior!.speak(dt, on: talk, level: level, shapes: talkShapes, clear: clear)
        if shape == nil, talk, behavior!.talk.stage == .gap { shape = "closed" }
        for i in mouths.names.indices {
            let w = mouths.names[i] == shape ? 1.0 : 0.0
            mouths.weights[i] = w
            if let slot = mouths.slots[i] { write(slot, w) }
        }
        snapFaces = false
        facePending = expression != wanted || gazePatch != gaze
            || expressions.weights.contains { $0 > 0 && $0 < 1 }
            || gazes.names.indices.contains { gazes.names[$0] != "closed" && gazes.weights[$0] > 0 && gazes.weights[$0] < 1 }
        return openness
    }

    /// The blink of a living pack: as `blink`, with the eyes held shut `doze` seconds while she rests.
    /// `blinkSkipped` tells that the whole shut phase fell between the last frame and this one (a low frame rate).
    mutating func dozeBlink(_ dt: Double, force: Bool, doze: Bool) -> Double {
        blinkSkipped = false
        blinkAt -= dt
        if blinkT == nil, blinkAt <= 0 || force {
            blinkT = 0
            blinkHold = doze ? behavior!.rng.uniform(CharacterDirector.doze.0, CharacterDirector.doze.1) : 0.04
        }
        guard var bt = blinkT else { return 1 }
        bt += dt
        blinkT = bt
        let hold = blinkHold
        blinkSkipped = bt - dt < 0.07 && bt >= 0.07 + hold
        if bt < 0.07 { return 1 - bt / 0.07 }
        if bt < 0.07 + hold { return 0 }
        if bt < 0.16 + hold { return (bt - 0.07 - hold) / 0.09 }
        blinkT = nil
        blinkAt = rng.random() > 0.15 ? rng.uniform(2.0, 5.0) : 0.25
        return 1
    }
}

extension CharacterBehavior {
    /// Steps the lip flaps with the behavior's own generator.
    mutating func speak(_ dt: Double, on: Bool, level: Int, shapes: [String], clear: Bool) -> String? {
        talk.update(dt, on: on, level: level, shapes: shapes, clear: clear, rng: &rng)
    }
}
