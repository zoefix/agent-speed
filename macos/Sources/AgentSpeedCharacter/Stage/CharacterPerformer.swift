import Foundation

/// What happened in a performer's frame, for effects the renderer adds (petals, a sound, ...).
public enum PerformerEvent: Sendable, Equatable {
    case petals
    /// The band's pose was swapped at the edge-on instant.
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

    public init(pack: CharacterPack, seed: UInt64 = 7, orb: Double = 1) {
        self.pack = pack
        var params: [CharacterParam] = []
        var seen = Set<String>()
        for pose in pack.poses {
            for p in pose.rig.params where seen.insert(p.id).inserted { params.append(p) }
        }
        let idle = pack.poses[0].rig
        let book = CharacterSide(rawValue: idle.landmarks.objectSide ?? "") ?? .left
        let director = CharacterDirector(params: params, seed: seed, out: book.other, book: book, orb: orb,
                                         celebrate: false, profile: pack.manifest.motion ?? MotionProfile())
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
        for (i, pose) in pack.poses.enumerated() where pose.band <= band { best = i }
        return best
    }

    public var activeRig: CharacterRig { pack.poses[activePose].rig }
    public var activePlacement: PosePlacement { placements[activePose] }
    public var band: SpeedBand? { director.band }
    public var intensity: Double { director.intensity }
    /// True while she acts out something (a gesture, a move, a hand fidget aside) or turns to swap poses: the frames
    /// that need the full 60 fps.
    public var isActing: Bool { director.activeGesture != nil || poseSwitch.isRunning || fadingPose != nil }

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
        let book = CharacterSide(rawValue: activeRig.landmarks.objectSide ?? "") ?? director.book
        director.setLevel(level)
        director.setSides(out: book.other, book: book, poseBand: pack.poses[activePose].band)
        poseSwitch.enterEdgeOn()
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
        let book = CharacterSide(rawValue: activeRig.landmarks.objectSide ?? "") ?? director.book
        director.setLevel(level)
        director.setSides(out: book.other, book: book, poseBand: pack.poses[activePose].band)
    }

    /// Advances one frame at speed level 0...13.
    public mutating func step(_ dt: Double, level: Int) {
        events.removeAll(keepingCapacity: true)
        exitReached = false
        director.setLevel(level)
        director.update(dt)
        for e in director.events where e == .petals { events.append(.petals) }
        director.events.removeAll(keepingCapacity: true)
        for i in values.indices { values[i] = director.values[i] }
        if amplitude < 1 { damp() }
        if fadingPose != nil {
            fadeProgress = min(1, fadeProgress + dt / CharacterPerformer.crossfadeDuration)
            if fadeProgress >= 1 { fadingPose = nil }
        }

        // Reduce Motion, and a character in a chair, crossfade poses instead of turning.
        if calm || director.profile.seated, !pendingExit, let band = director.band, poseIndex(for: band) != activePose,
           !poseSwitch.isRunning {
            fadingPose = activePose
            fadingWorld = world
            fadingOpacities = opacities
            fadeProgress = 0
            activePose = poseIndex(for: band)
            let book = CharacterSide(rawValue: activeRig.landmarks.objectSide ?? "") ?? director.book
            director.setSides(out: book.other, book: book, poseBand: pack.poses[activePose].band)
            events.append(.poseSwapped(band: pack.poses[activePose].band))
        } else if pendingExit, !poseSwitch.isRunning {
            poseSwitch.request(CharacterPerformer.exitTarget)
            pendingExit = false
        } else if !pendingExit, let band = director.band {
            let wanted = poseIndex(for: band)
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
                let book = CharacterSide(rawValue: activeRig.landmarks.objectSide ?? "") ?? director.book
                director.setSides(out: book.other, book: book, poseBand: pack.poses[target].band)
                events.append(.poseSwapped(band: pack.poses[target].band))
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

    public mutating func step(_ dt: Double, level: Int) {
        swapped = false
        if current.calm != calm { current.calm = calm }
        current.amplitude = amplitude
        if var o = outgoing {
            o.amplitude = amplitude
            o.step(dt, level: level)
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
        current.step(dt, level: level)
        if current.exitReached, var next = incoming {
            next.enterEdgeOn(level: level)
            next.step(dt, level: level)
            current = next
            incoming = nil
            swapped = true
        }
    }
}
