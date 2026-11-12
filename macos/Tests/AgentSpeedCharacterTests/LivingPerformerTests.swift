import Foundation
import Testing
@testable import AgentSpeedCharacter

/// A living pack on the stage (spec/CHARACTERS.md, Behavior; section 6 with mood poses): mood poses after the bands,
/// the mood's pose shown through the same swap, the shown pose's face patches, and the inputs ignored by other packs.
enum LivingPack {
    /// The tiny rig with face patches (`expr-smile`, `mouth-a`, `mouth-o`, `gaze-left`, `gaze-closed`) as parameters.
    static func rigJSON(patches: [String] = ["expr-smile", "expr-thinking", "mouth-a", "mouth-o", "gaze-left",
                                             "gaze-right", "gaze-closed"],
                        faceTop: Double = 10) throws -> [String: Any] {
        var rig = try TinyPack.rigJSON(faceTop: faceTop)
        var params = rig["params"] as! [[String: Any]]
        params += patches.map { ["id": "Var.\($0)", "min": 0, "max": 1, "default": 0] }
        rig["params"] = params
        rig["variants"] = patches.map { id -> [String: Any] in
            let region = id.hasPrefix("expr") ? "face" : id.hasPrefix("mouth") ? "mouth" : "eyes"
            return ["id": id, "kind": "patch", "param": "Var.\(id)", "region": region]
        }
        return rig
    }

    /// A pack with an idle (and cruise) band pose and the given mood poses; `extra` adds manifest `states` entries.
    static func make(states: [String], extra: [String: String] = [:], idle: [String: Any]? = nil) throws -> URL {
        let rig = try idle ?? rigJSON()
        let root = try TinyPack.make(bands: ["idle": rig, "cruise": rig]) { manifest in
            var map = Dictionary(uniqueKeysWithValues: states.map { ($0, "poses/\($0)") })
            for (k, v) in extra { map[k] = v }
            manifest["states"] = map
        }
        for state in states {
            let dir = root.appendingPathComponent("poses/\(state)")
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            try JSONSerialization.data(withJSONObject: try TinyPack.rigJSON(faceTop: 20))
                .write(to: dir.appendingPathComponent("rig.json"))
            try Data([0]).write(to: dir.appendingPathComponent("atlas.png"))
        }
        return root
    }
}

@Suite struct LivingPerformerTests {
    @Test func moodPosesLoadAfterTheBandsAndNeverStandForABand() throws {
        let pack = try CharacterPack.load(from: LivingPack.make(states: ["sleepy", "think"]))
        #expect(pack.poses.map(\.name) == ["idle", "cruise", "think", "sleepy"])
        #expect(pack.states == ["think", "sleepy"])
        #expect(pack.isLiving)
        let performer = CharacterPerformer(pack: pack)
        #expect(performer.living && performer.director.isLiving)
        #expect(performer.poseIndex(for: .redline) == 1)
    }

    @Test func aMoodPoseLeadingOutsideThePackIsRefusedAndAMissingOneSkipped() throws {
        #expect(throws: PackLoadError.unsafePath("../think")) {
            try CharacterPack.load(from: LivingPack.make(states: [], extra: ["think": "../think"]))
        }
        let pack = try CharacterPack.load(from: LivingPack.make(states: ["think"], extra: ["focus": "poses/focus"]))
        #expect(pack.states == ["think"])
    }

    @Test func aPackWithoutPatchesOrStatesIgnoresTheInputs() throws {
        let rig = try TinyPack.rigJSON()
        let pack = try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig, "cruise": rig]))
        #expect(!pack.isLiving)
        var plain = CharacterPerformer(pack: pack, seed: 3)
        var fed = CharacterPerformer(pack: pack, seed: 3)
        let inputs = BehaviorInputs(phase: .generating, wait: 12, cursor: (0.3, 0.1), over: true)
        for n in 0..<600 {
            let level = n < 300 ? 0 : 3
            plain.step(1.0 / 60, level: level)
            fed.step(1.0 / 60, level: level, inputs: inputs)
        }
        #expect(plain.values == fed.values)
        #expect(fed.director.poseWanted() == nil)
    }

    @Test func restingShowsTheSleepyPoseAtOnceAndWorkWakesHer() throws {
        let pack = try CharacterPack.load(from: LivingPack.make(states: ["sleepy", "greet", "think"]))
        var performer = CharacterPerformer(pack: pack)
        let sleepy = pack.poses.firstIndex { $0.state == "sleepy" }!
        performer.step(1.0 / 60, level: 0, inputs: BehaviorInputs(phase: .idle, idle: 900, hour: 1))
        #expect(performer.director.mood == .rest)
        #expect(performer.activePose == sleepy)
        var seen: [String] = []
        var request = BehaviorInputs(phase: .generating, wait: 0, hour: 1)
        for n in 0..<(60 * 14) {
            request.wait = Double(n) / 60
            performer.step(1.0 / 60, level: 0, inputs: request)
            let name = pack.poses[performer.activePose].name
            if seen.last != name { seen.append(name) }
        }
        // Waking up greets in the greet pose, the long wait for the first token thinks in the think pose.
        #expect(seen == ["sleepy", "greet", "idle", "think"], "\(seen)")
    }

    @Test func theFaceCenterIsTheShownPosesFaceBoxOnScreen() throws {
        let pack = try CharacterPack.load(from: LivingPack.make(states: []))
        let performer = CharacterPerformer(pack: pack)
        let p = performer.faceCenter(feetOnScreen: (200, 400), pointsPerPixel: 2)
        // Face box [40, 10, 60, 30], feet (50, 90): the face center is right above the feet, 70 px up.
        #expect(abs(p.x - 200) < 1e-9 && abs(p.y - (400 - 70 * 2)) < 1e-9)
    }

    @Test func theCursorTurnsHerHeadAndHerEyesTakeTheGazePatch() throws {
        let pack = try CharacterPack.load(from: LivingPack.make(states: []))
        var performer = CharacterPerformer(pack: pack)
        let angleX = performer.director.space.slot("AngleX")!
        var looking = BehaviorInputs(phase: .idle, idle: 30, cursor: (0.9, 0.05))
        var turned = 0.0
        for _ in 0..<(60 * 3) {
            performer.step(1.0 / 60, level: 0, inputs: looking)
            turned = max(turned, performer.values[angleX])
        }
        #expect(turned > 0.45, "head toward the cursor on her right: \(turned)")
        #expect(performer.director.behavior?.gaze == "right")
        #expect(performer.director.gazePatch == "right" || performer.director.expression != nil)
        looking.cursor = nil
        for _ in 0..<(60 * 3) { performer.step(1.0 / 60, level: 0, inputs: looking) }
        #expect(performer.director.behavior?.lookWeight == 0)
    }

    @Test func sheTalksInMouthShapesWhileOutputFlows() throws {
        let pack = try CharacterPack.load(from: LivingPack.make(states: []))
        var performer = CharacterPerformer(pack: pack)
        let a = performer.director.space.slot("Var.mouth-a")!, o = performer.director.space.slot("Var.mouth-o")!
        var shapes = Set<String>()
        var mixed = false
        for _ in 0..<(60 * 20) {
            performer.step(1.0 / 60, level: 3, inputs: BehaviorInputs(phase: .generating))
            if let shape = performer.director.mouthShape { shapes.insert(shape) }
            let v = (performer.values[a], performer.values[o])
            if v.0 > 0, v.0 < 1 || v.1 > 0 { mixed = true }
        }
        #expect(shapes == ["a", "o"])
        #expect(!mixed, "mouth shapes switch on a frame and never blend")
    }

    @Test func aClickPokesHerAndKeepsTheShownPose() throws {
        let pack = try CharacterPack.load(from: LivingPack.make(states: []))
        var performer = CharacterPerformer(pack: pack)
        for _ in 0..<60 { performer.step(1.0 / 60, level: 0, inputs: BehaviorInputs(phase: .idle, idle: 30)) }
        performer.step(1.0 / 60, level: 0, inputs: BehaviorInputs(phase: .idle, idle: 30, cursor: (0, 0.3), over: true,
                                                                  click: true))
        #expect(performer.director.mood == .poke)
        #expect(performer.director.poseWanted() == .keep)
    }

    @Test(.enabled(if: livingOptimized, "allocation check needs an optimized build: swift test -c release"))
    func talkingAndFollowingTheCursorAllocateNothingOnceWarm() throws {
        let pack = try CharacterPack.load(from: LivingPack.make(states: ["think"]))
        var performer = CharacterPerformer(pack: pack)
        var inputs = BehaviorInputs(phase: .generating)
        var t = 0.0
        func step() {
            t += 1.0 / 60
            inputs.cursor = (0.5 * sin(t), 0.2 * cos(0.7 * t))
            performer.step(1.0 / 60, level: 3, inputs: inputs)
        }
        // A minute first: each path's first run initializes its static tables once.
        for _ in 0..<3600 { step() }
        var quietWindows = 0
        for _ in 0..<60 {
            let starts = performer.director.motionStarts
            let mood = performer.director.mood
            let count = AllocationProbe.allocations { for _ in 0..<20 { step() } }
            guard let count, performer.director.motionStarts == starts, performer.director.mood == mood else { continue }
            quietWindows += 1
            #expect(count == 0, "allocations in a window without new actions or moods")
        }
        #expect(quietWindows > 0)
    }
}

#if DEBUG
private let livingOptimized = false
#else
private let livingOptimized = true
#endif

/// What a living pack costs per frame (`AGENT_SPEED_LIVING_PACK`, and `AGENT_SPEED_PLAIN_PACK` for a pack without
/// behavior to compare with): the director, physics and rig of the scripted session of behavior.json, printed per
/// frame. Run optimized: `swift test -c release`.
@Suite struct LivingCostTests {
    static let env = ProcessInfo.processInfo.environment

    static func cost(_ pack: CharacterPack, inputs: Bool) throws -> Double {
        let session = BehaviorSession(doc: try Fixture.json("behavior.json")["session"] as! [String: Any])
        var performer = CharacterPerformer(pack: pack)
        let frames = Int(session.seconds * 60)
        let clock = ContinuousClock()
        let elapsed = clock.measure {
            for n in 0..<frames {
                let t = Double(n) / 60
                performer.step(1.0 / 60, level: session.level(at: t),
                               inputs: inputs ? session.inputs(at: t, dt: 1.0 / 60) : nil)
            }
        }
        return (Double(elapsed.components.seconds) * 1e3 + Double(elapsed.components.attoseconds) / 1e15) / Double(frames)
    }

    @Test(.enabled(if: env["AGENT_SPEED_LIVING_PACK"] != nil && env["AGENT_SPEED_PLAIN_PACK"] != nil))
    func aLivingPackCostsLittleMorePerFrame() throws {
        let living = try CharacterPack.load(from: URL(fileURLWithPath: Self.env["AGENT_SPEED_LIVING_PACK"]!))
        let plain = try CharacterPack.load(from: URL(fileURLWithPath: Self.env["AGENT_SPEED_PLAIN_PACK"]!))
        _ = try Self.cost(plain, inputs: false)
        let rows = [("plain pack, no inputs", try Self.cost(plain, inputs: false)),
                    ("living pack, no inputs", try Self.cost(living, inputs: false)),
                    ("living pack, session inputs", try Self.cost(living, inputs: true))]
        for (name, ms) in rows {
            print(String(format: "living cost: %@: %.3f ms per frame", name, ms))
        }
        #expect(rows[2].1 < 2)
    }
}
