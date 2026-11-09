import AgentSpeedCharacter
import Foundation
import Testing

/// Reduce Motion and the effects levels (DESIGN 2a.6): calm swaps crossfade, airborne actions are skipped, amplitude
/// pulls motion toward rest.
@Suite struct CalmTests {
    @Test func calmPoseSwapsCrossfadeWithoutTurning() throws {
        let rig = try TinyPack.rigJSON()
        var other = try TinyPack.rigJSON(objectSide: "right")
        other["name"] = "other"
        let pack = try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig, "boost": other]))
        var performer = CharacterPerformer(pack: pack)
        performer.calm = true
        let spin = performer.director.space.slot("Spin")!
        for _ in 0..<30 { performer.step(1.0 / 60, level: 0) }
        var maxSpin = 0.0
        var faded = false
        for _ in 0..<60 {
            performer.step(1.0 / 60, level: 6)
            maxSpin = max(maxSpin, performer.values[spin])
            if performer.fadingPose == 0 { faded = true }
        }
        #expect(performer.activePose == 1)
        #expect(faded)
        #expect(maxSpin == 0)
        #expect(performer.fadingPose == nil)
        #expect(performer.fadeProgress == 1)
    }

    @Test func calmCharacterChangesCrossfade() throws {
        let rig = try TinyPack.rigJSON()
        let a = CharacterPerformer(pack: try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig], id: "a")))
        let b = CharacterPerformer(pack: try CharacterPack.load(from: TinyPack.make(bands: ["idle": rig], id: "b")))
        var stage = CharacterStage(a)
        stage.calm = true
        stage.step(1.0 / 60, level: 2)
        stage.switchTo(b)
        stage.step(1.0 / 60, level: 2)
        #expect(stage.swapped)
        #expect(stage.current.pack.id == "b")
        #expect(stage.outgoing?.pack.id == "a")
        for _ in 0..<30 { stage.step(1.0 / 60, level: 2) }
        #expect(stage.outgoing == nil)
    }

    @Test func groundedDirectorNeverLeavesTheGround() throws {
        let params = try Fixture.json("director.json")["params"] as! [[String: Any]]
        let decoded = try JSONDecoder().decode([CharacterParam].self, from: JSONSerialization.data(withJSONObject: params))
        var director = CharacterDirector(params: decoded, seed: 7, out: .right, book: .left, orb: 1, celebrate: false)
        director.grounded = true
        var played = Set<String>()
        for n in 0..<(60 * 60) {
            director.setLevel(min(13, (n / 300) % 14))
            director.update(1.0 / 60)
            if let g = director.activeGesture { played.insert(g) }
        }
        #expect(played.isDisjoint(with: ["hop", "jump", "hops", "spin", "step", "run", "jumpjoy"]))
        #expect(!played.isEmpty)
    }

    @Test func zeroAmplitudeKeepsTheRestPoseButBreathes() throws {
        let pack = try CharacterPack.load(from: TinyPack.make(bands: ["idle": try TinyPack.rigJSON()]))
        var performer = CharacterPerformer(pack: pack)
        performer.amplitude = 0
        let space = performer.director.space
        let breath = space.slot("Breath")!
        var breathSeen = 0.0
        for _ in 0..<240 {
            performer.step(1.0 / 60, level: 12)
            breathSeen = max(breathSeen, performer.values[breath])
            for name in ["PosX", "PosY", "Squash", "Lean"] {
                #expect(abs(performer.values[space.slot(name)!]) < 1e-9)
            }
        }
        #expect(breathSeen > 0.3)
    }
}
