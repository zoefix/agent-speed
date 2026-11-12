import Foundation

/// What happened in a performer's frame, for effects the renderer adds (petals, a sound, ...).
public enum PerformerEvent: Sendable, Equatable {
    case petals
    /// The pose was swapped at the edge-on instant: the band's, or a mood pose (with the band at the time).
    case poseSwapped(band: SpeedBand)
}

/// How a pose sits on screen relative to the idle pose (spec/CHARACTERS.md, Pose swap): its feet on the idle pose's
/// feet, scaled so the face-to-feet distance matches.
public struct PosePlacement: Sendable, Equatable {
    /// Canvas pixels of this pose per canvas pixel of the idle pose.
    public var scale: Double
    /// The pose's feet in its own canvas.
    public var feet: (x: Double, y: Double)

    public static func == (a: PosePlacement, b: PosePlacement) -> Bool {
        a.scale == b.scale && a.feet == b.feet
    }

    /// Screen position of a pose canvas point, given the idle pose's feet on screen and screen points per idle
    /// canvas pixel.
    public func screen(_ x: Double, _ y: Double, feetOnScreen: (Double, Double), pointsPerPixel: Double) -> (Double, Double) {
        let k = pointsPerPixel * scale
        return (feetOnScreen.0 + (x - feet.x) * k, feetOnScreen.1 + (y - feet.y) * k)
    }
}

/// One character on stage: its poses, one director over the union of their parameters, physics per pose, the pose
/// swap and the placement of each pose (characters/reference/stage.py). Step it once per frame with the speed level;
/// then read `activePose`, `world` and `opacities` to draw. No heap allocation per frame except when an action starts.
public struct CharacterPerformer: Sendable {
    /// The reference's root travel per unit of PosX / PosY, in face widths (rig/params.py).
    public static let posX = 3.0
    public static let posY = 2.0

    public let pack: CharacterPack
    public private(set) var director: CharacterDirector
    public private(set) var activePose = 0
    public let placements: [PosePlacement]
    /// The idle pose's height from the top of its art to its feet, in its canvas pixels.
    public let idleHeight: Double
    /// Parameter values by director slot after the last step (swap and physics applied).
    public private(set) var values: [Double]
    /// The active pose's rig parameters, bone transforms and part opacities after the last step.
    public private(set) var rigValues: [Double]
    public private(set) var world: [Affine2] = []
    public private(set) var opacities: [Double] = []
    public private(set) var events: [PerformerEvent] = []
    /// Set on the frame an exit turn reaches edge-on (see `CharacterStage`).
    public private(set) var exitReached = false
    /// How much of the director's motion shows, 0…1 (DESIGN 2a.6 effects: Off 0 keeps only breathing and blinks,
    /// Soft 0.5).
    public var amplitude = 1.0
    /// Reduce Motion: no airborne actions, and pose swaps crossfade over `crossfadeDuration` instead of turning.
    public var calm = false {
        didSet { director.grounded = calm }
    }
    public static let crossfadeDuration = 0.3
    /// During a calm swap: the outgoing pose, its last transforms and opacities, and the crossfade progress 0 → 1.
    public private(set) var fadingPose: Int?
    public private(set) var fadingWorld: [Affine2] = []
    public private(set) var fadingOpacities: [Double] = []
    public private(set) var fadeProgress = 1.0

    var physics: [CharacterPhysics]
    var slotMaps: [[Int]]
    var poseSwitch = PoseSwitch()
    var pendingExit = false
    let slots: Slots
    let faceWidth: Double
    static let exitTarget = -1
    /// A living pack (spec/CHARACTERS.md, Behavior): the director gets the shown pose's variants and the mood poses.
    public let living: Bool
    /// The first step with inputs shows the mood pose at once, without a turn.
    var started = false
    /// The pointer's last cursor and when it last moved, for the frame policy.
    var lastCursor: (x: Double, y: Double)?
    var cursorMovedAt = -Double.infinity
    /// A head turn toward a moved cursor settles within this (the head springs).
    static let followSettle = 0.6

    public init(pack: CharacterPack, seed: UInt64 = 7, orb: Double = 1) {
        self.pack = pack
        var params: [CharacterParam] = []
        var seen = Set<String>()
        for pose in pack.poses {
            for p in pose.rig.params where seen.insert(p.id).inserted { params.append(p) }
        }
        let idle = pack.poses[0].rig
        let book = CharacterSide(rawValue: idle.landmarks.objectSide ?? "") ?? .left
        living = pack.isLiving
        let director = CharacterDirector(params: params, seed: seed, out: book.other, book: book, orb: orb,
                                         celebrate: false, profile: pack.manifest.motion ?? MotionProfile(),
                                         variants: living ? idle.variants : [], states: living ? pack.states : [])
        self.director = director
        let space = director.space
        slots = Slots(space)
        values = [Double](repeating: 0, count: space.count)
        physics = pack.poses.map { pose in
            var p = CharacterPhysics(pose.rig.physics, windSign: 1) { name in
                space.isParam(name) ? space.slot(name) : nil
            }
            p.reset(space.params.map { $0?.default ?? 0 })
            return p
        }
        slotMaps = pack.poses.map { pose in pose.rig.params.map { space.slot($0.id)! } }
        rigValues = []
        let base = CharacterPerformer.measures(idle)
        placements = pack.poses.map { pose in
            let feet = pose.rig.landmarks.feet ?? [pose.rig.canvas.width / 2, pose.rig.canvas.height]
            let scale = CharacterPerformer.poseScale(base: base, pose: CharacterPerformer.measures(pose.rig))
            return PosePlacement(scale: scale, feet: (feet[0], feet.count > 1 ? feet[1] : pose.rig.canvas.height))
        }
        var top = Double.infinity
        for part in idle.parts {
            var i = 1
            while i < part.vertices.count {
                top = min(top, part.vertices[i])
                i += 2
            }
        }
        let feetY = idle.landmarks.feet?.last ?? idle.canvas.height
        idleHeight = top.isFinite ? feetY - top : idle.canvas.height
        faceWidth = idle.landmarks.faceWidth ?? idle.canvas.width / 10
        events.reserveCapacity(8)
        rigValues = [Double](repeating: 0, count: pack.poses.map(\.rig.params.count).max() ?? 0)
    }

    /// Face center to feet, face box diagonal, shoulders to hips (nil in packs built without it).
    static func measures(_ rig: CharacterRig) -> [Double?] {
        let lm = rig.landmarks
        guard let f = lm.face, f.count == 4 else { return [nil, nil, lm.torsoLength] }
        let body = lm.feet.flatMap { $0.count == 2 ? $0[1] - (f[1] + f[3]) / 2 : nil }
        return [body, hypot(f[2] - f[0], f[3] - f[1]), lm.torsoLength]
    }

    /// How much a pose is scaled to match the idle pose: the middle of the three ratios of `measures`, so one that
    /// the pose throws off (tucked legs in a jump, a turned face) is outvoted; face to feet alone without a torso.
    static func poseScale(base: [Double?], pose: [Double?]) -> Double {
        var ratios: [Double] = []
        for (b, p) in zip(base, pose) {
            if let b, let p, b > 0, p > 0 { ratios.append(b / p) }
        }
        if ratios.count == 3 { return ratios.sorted()[1] }
        if let b = base.first ?? nil, let p = pose.first ?? nil, b > 0, p > 0 { return b / p }
        return 1
    }

    /// The pose index a band shows: its own, else the nearest lower band's.
    public func poseIndex(for band: SpeedBand) -> Int {
        var best = 0
        for (i, pose) in pack.poses.enumerated() where pose.state == nil && pose.band <= band { best = i }
        return best
    }

    /// The pose to show (section 6 with Behavior): the mood's pose when the pack has it, the shown pose while a poke or
    /// trouble keeps it, else the band's.
    public var wantedPose: Int {
        let band = director.band ?? .idle
        switch director.poseWanted() {
        case .keep: return activePose
        case .state(let name): return pack.poses.firstIndex { $0.state == name } ?? poseIndex(for: band)
        case nil: return poseIndex(for: band)
        }
    }

    /// The band a swap into the shown pose reports: its own for a band pose, the director's for a mood pose.
    var swappedBand: SpeedBand {
        let pose = pack.poses[activePose]
        return pose.state == nil ? pose.band : director.band ?? .idle
    }

    /// Hands the director the sides, the name and (in a living pack) the variants of the pose now shown.
    mutating func adoptActivePose() {
        let pose = pack.poses[activePose]
        let book = CharacterSide(rawValue: pose.rig.landmarks.objectSide ?? "") ?? director.book
        director.setSides(out: book.other, book: book, pose: pose.name, variants: living ? pose.rig.variants : nil)
    }

    /// The center of the shown pose's face box at rest, where she stands now, in screen points (y down): the point the
    /// cursor input is measured from (spec/CHARACTERS.md, Behavior, Inputs).
    public func faceCenter(feetOnScreen: (Double, Double), pointsPerPixel: Double) -> (x: Double, y: Double) {
        let rig = activeRig
        let face = rig.landmarks.face ?? []
        let (x, y) = face.count == 4 ? ((face[0] + face[2]) / 2, (face[1] + face[3]) / 2)
            : (activePlacement.feet.x, activePlacement.feet.y - 0.9 * idleHeight / activePlacement.scale)
        let p = activePlacement.screen(x, y, feetOnScreen: feetOnScreen, pointsPerPixel: pointsPerPixel)
        return (p.0 + bodyOffset.dx * pointsPerPixel, p.1)
    }

    public var activeRig: CharacterRig { pack.poses[activePose].rig }
    public var activePlacement: PosePlacement { placements[activePose] }
    public var band: SpeedBand? { director.band }
    public var intensity: Double { director.intensity }
    /// True while she acts out something (a gesture, a move, a hand fidget aside), turns to swap poses, or in a living
    /// pack follows a moving cursor, swaps a face or talks: the frames that need the full 60 fps (DESIGN 2a.6).
    public var isActing: Bool {
        director.activeGesture != nil || poseSwitch.isRunning || fadingPose != nil
            || director.isLivingBusy(following: director.time - cursorMovedAt < CharacterPerformer.followSettle)
    }

    /// The character's sideways and upward travel this frame (side steps, hops), in idle canvas pixels (y down).
    public var bodyOffset: (dx: Double, dy: Double) {
        (values[slots.posX] * CharacterPerformer.posX * faceWidth, -values[slots.posY] * CharacterPerformer.posY * faceWidth)
    }

    /// Asks the character to turn edge-on so another one can take over (`CharacterStage`).
    public mutating func requestExit() {
        pendingExit = true
    }

    /// Starts this character on the edge-on instant in the pose of `level`, turning back toward the viewer.
    public mutating func enterEdgeOn(level: Int) {
        activePose = poseIndex(for: SpeedBand(level: level))
        director.setLevel(level)
        adoptActivePose()
        poseSwitch.enterEdgeOn()
        started = true
    }

    /// The floating readout's geometry for this frame (characters/reference/stage.py): the idle pose's feet at
    /// `feetOnScreen`, `pointsPerPixel` screen points per idle canvas pixel, `unit` points per reference unit.
    public func hudLayout(feetOnScreen: (Double, Double), pointsPerPixel: Double, unit: Double) -> HudLayout {
        let h = idleHeight * pointsPerPixel
        let (dx, dy) = bodyOffset
        let fx = feetOnScreen.0 + dx * pointsPerPixel, fy = feetOnScreen.1
        let lift = dy * pointsPerPixel
        return HudLayout(ground: (fx, fy + 6 * unit, 0.27 * h, 0.27 * h * 0.17),
                         waist: (fx + 0.01 * h, fy - 0.6 * h + lift, 0.3 * h, 0.05 * h), characterHeight: h, unit: unit)
    }

    /// Pulls every motion parameter toward its rest value by `amplitude`, keeping breathing and blinks.
    mutating func damp() {
        let space = director.space
        for i in values.indices where i != slots.breath && i != slots.eyeOpen[0] && i != slots.eyeOpen[1] {
            let rest = space.params[i]?.default ?? 0
            values[i] = rest + (values[i] - rest) * amplitude
        }
    }

    /// Starts this character in the pose of `level`, facing the viewer (a calm character change).
    public mutating func enterAtRest(level: Int) {
        activePose = poseIndex(for: SpeedBand(level: level))
        director.setLevel(level)
        adoptActivePose()
    }

    /// Advances one frame at speed level 0...13. `inputs` (the agents' phases and the user's cursor) drive the moods
    /// of a living pack; any other pack ignores them.
    public mutating func step(_ dt: Double, level: Int, inputs: BehaviorInputs? = nil) {
        events.removeAll(keepingCapacity: true)
        exitReached = false
        director.setLevel(level)
        let inputs = living ? inputs : nil
        if let cursor = inputs?.cursor {
            if let last = lastCursor, abs(last.x - cursor.x) < 1e-4, abs(last.y - cursor.y) < 1e-4 {} else {
                cursorMovedAt = director.time + dt
            }
            lastCursor = cursor
        } else if lastCursor != nil {
            lastCursor = nil
            cursorMovedAt = director.time + dt
        }
        director.update(dt, inputs: inputs)
        if inputs != nil, !started {
            started = true
            let wanted = wantedPose
            if wanted != activePose, !pendingExit, !poseSwitch.isRunning {
                activePose = wanted
                adoptActivePose()
            }
        }
        for e in director.events where e == .petals { events.append(.petals) }
        director.events.removeAll(keepingCapacity: true)
        for i in values.indices { values[i] = director.values[i] }
        if amplitude < 1 { damp() }
        if fadingPose != nil {
            fadeProgress = min(1, fadeProgress + dt / CharacterPerformer.crossfadeDuration)
            if fadeProgress >= 1 { fadingPose = nil }
        }

        // Reduce Motion, and a character in a chair, crossfade poses instead of turning.
        let wanted = wantedPose
        if calm || director.profile.seated, !pendingExit, director.band != nil, wanted != activePose,
           !poseSwitch.isRunning {
            fadingPose = activePose
            fadingWorld = world
            fadingOpacities = opacities
            fadeProgress = 0
            activePose = wanted
            adoptActivePose()
            events.append(.poseSwapped(band: swappedBand))
        } else if pendingExit, !poseSwitch.isRunning {
            poseSwitch.request(CharacterPerformer.exitTarget)
            pendingExit = false
        } else if !pendingExit, director.band != nil {
            if wanted != activePose { poseSwitch.request(wanted) }
        }
        let (swap, output, swapped) = poseSwitch.update(dt)
        if let o = output {
            values[slots.spin] = o.spin
            values[slots.squash] += o.squash
            values[slots.posY] += o.posY
            values[slots.skirtFlare] = max(values[slots.skirtFlare], o.skirtFlare)
        }
        if swapped, let target = swap {
            if target == CharacterPerformer.exitTarget {
                exitReached = true
            } else {
                activePose = target
                adoptActivePose()
                events.append(.poseSwapped(band: swappedBand))
            }
        }
        for i in physics.indices {
            physics[i].step(dt, values)
            if i == activePose { physics[i].write(into: &values) }
        }
        let map = slotMaps[activePose]
        for (i, slot) in map.enumerated() { rigValues[i] = values[slot] }
        let rig = activeRig
        rig.worldTransforms(rigValues, into: &world)
        rig.opacities(rigValues, into: &opacities)
    }
}

/// The character an orb shows and the turn between two characters (spec/CHARACTERS.md, Which character): the current
/// one turns edge-on, the next one takes over at that instant and turns back.
public struct CharacterStage: Sendable {
    public private(set) var current: CharacterPerformer
    public private(set) var incoming: CharacterPerformer?
    /// Set on the frame the incoming character took over.
    public private(set) var swapped = false

    public init(_ performer: CharacterPerformer) {
        current = performer
    }

    /// Starts the turn to another character; a newer request replaces one still waiting.
    public mutating func switchTo(_ next: CharacterPerformer) {
        incoming = next
        current.requestExit()
    }

    /// Reduce Motion: a character change crossfades over `CharacterPerformer.crossfadeDuration` instead of turning;
    /// passed on to the performers with `amplitude`.
    public var calm = false
    public var amplitude = 1.0
    /// During a calm character change: the outgoing character and the crossfade progress 0 → 1.
    public private(set) var outgoing: CharacterPerformer?
    public private(set) var outgoingFade = 1.0

    public mutating func step(_ dt: Double, level: Int, inputs: BehaviorInputs? = nil) {
        swapped = false
        if current.calm != calm { current.calm = calm }
        current.amplitude = amplitude
        var quiet = inputs
        quiet?.click = false
        quiet?.over = false
        if var o = outgoing {
            o.amplitude = amplitude
            o.step(dt, level: level, inputs: quiet)
            outgoing = o
        }
        if outgoing != nil {
            outgoingFade = min(1, outgoingFade + dt / CharacterPerformer.crossfadeDuration)
            if outgoingFade >= 1 { outgoing = nil }
        }
        if calm, var next = incoming {
            next.calm = true
            next.enterAtRest(level: level)
            outgoing = current
            outgoingFade = 0
            current = next
            incoming = nil
            swapped = true
        }
        current.step(dt, level: level, inputs: inputs)
        if current.exitReached, var next = incoming {
            next.enterEdgeOn(level: level)
            next.step(dt, level: level, inputs: quiet)
            current = next
            incoming = nil
            swapped = true
        }
    }
}
