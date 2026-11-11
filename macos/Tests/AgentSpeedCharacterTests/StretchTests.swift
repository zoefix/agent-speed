import Foundation
import Testing
@testable import AgentSpeedCharacter

/// `Squash` stretches her whole figure over the feet, face included: past `held` it reads as the art drawn out, so
/// only an instant of a take-off or a landing goes there (spec/CHARACTERS.md, Motion).
@Suite struct StretchTests {
    static let held = 0.2
    static let brief = 0.25

    /// The longest run of samples `dt` apart above `held`, in seconds.
    static func longest(_ series: [Double], _ dt: Double, over limit: Double = held) -> Double {
        var run = 0.0, best = 0.0
        for v in series {
            run = v > limit + 1e-9 ? run + dt : 0
            best = max(best, run)
        }
        return best
    }

    @Test func noActionHoldsHerDrawnOut() {
        let out = CharacterSide.right, book = CharacterSide.left
        var actions = Moves.names.compactMap { Moves.make($0, out: out, book: book) }
        actions += Moves.profile.keys.sorted().compactMap { Moves.profileMove($0, out: out, book: book) }
        actions += Gestures.head.map(Gestures.headGesture)
        actions += Gestures.face.map { Gestures.faceGesture($0) }
        actions += [Moves.sidestep(out, book, 0.5), Moves.peek(out, book, 1), Moves.pounce(out, book, 1)]
        let dt = 1.0 / 240
        for m in actions {
            guard let keys = m.curves["Squash"] else { continue }
            let curve = Curve(keys)
            let squash = (0...Int(m.duration / dt)).map { curve(Double($0) * dt) }
            #expect(Self.longest(squash, dt) < Self.brief, "\(m.name)")
        }
        var swap = PoseSwitch()
        swap.request(1)
        var hop: [Double] = []
        while swap.isRunning { hop.append(swap.update(dt).output?.squash ?? 0) }
        #expect(Self.longest(hop, dt) < Self.brief, "pose swap")
    }

    @Test func sheIsNeverHeldDrawnOutInAnyBand() throws {
        let params = try Fixture.json("director.json")["params"] as! [[String: Any]]
        let decoded = try JSONDecoder().decode([CharacterParam].self, from: JSONSerialization.data(withJSONObject: params))
        let profile = try JSONDecoder().decode(MotionProfile.self, from: Data(
            #"{"signature": ["stretch", "yawn", "tiptoe", "hop", "pounce"], "bounce": 1.4}"#.utf8))
        var director = CharacterDirector(params: decoded, seed: 7, out: .right, book: .left, orb: -1, celebrate: false,
                                         profile: profile)
        let squash = director.space.slot("Squash")!
        let levels = [0, 0, 2, 6, 10, 13, 3, 0]
        let dt = 1.0 / 60, frames = 240 * 60
        var played = Set<String>()
        var series: [Double] = []
        for n in 0..<frames {
            director.setLevel(levels[n * levels.count / frames])
            director.update(dt)
            series.append(director.values[squash])
            if let g = director.activeGesture { played.insert(g) }
        }
        #expect(played.isSuperset(of: ["stretch", "yawn", "tiptoe"]), "\(played)")
        #expect(Self.longest(series, dt) < Self.brief)
    }
}

/// `AGENT_SPEED_CHARACTER_PACKS` (a folder of unpacked packs): every pack rests at 0 tok/s for ten minutes, then tours
/// the bands, at the idle band's 15 fps and at 60; her root (all of her) is never held stretched past `Squash` 0.2 for
/// longer than an instant, outside the turn of a pose swap. Prints the longest run and the tallest frame per pack.
@Suite struct StretchPackTests {
    @Test(.enabled(if: ProcessInfo.processInfo.environment["AGENT_SPEED_CHARACTER_PACKS"] != nil))
    func everyPackKeepsHerShape() throws {
        let env = ProcessInfo.processInfo.environment
        let root = URL(fileURLWithPath: env["AGENT_SPEED_CHARACTER_PACKS"]!)
        let ids = try FileManager.default.contentsOfDirectory(atPath: root.path).filter {
            FileManager.default.fileExists(atPath: root.appendingPathComponent("\($0)/manifest.json").path)
        }.sorted().filter { id in env["AGENT_SPEED_CHARACTER_IDS"].map { $0.split(separator: ",").contains(Substring(id)) } ?? true }
        // Root height over width at Squash 0.2 (rig/params.py: scaleY 0.1, scaleX -0.06 per unit).
        let limit = (1 + 0.1 * StretchTests.held) / (1 - 0.06 * StretchTests.held)
        let tour = [0, 2, 0, 6, 0, 10, 13, 0, 3, 8, 0]
        for id in ids {
            let pack = try CharacterPack.load(from: root.appendingPathComponent(id))
            var longest = 0.0, action = "", tallest = 1.0
            for (fps, seconds, levels) in [(15.0, 600.0, [0]), (60.0, 330.0, tour)] {
                var performer = CharacterPerformer(pack: pack, seed: 11)
                let spin = performer.director.space.slot("Spin")
                let dt = 1 / fps, frames = Int(seconds * fps)
                var run = 0.0
                for n in 0..<frames {
                    performer.step(dt, level: levels[n * levels.count / frames])
                    if let spin, performer.values[spin] != 0 {
                        run = 0
                        continue
                    }
                    let w = performer.world[0]
                    let aspect = w.d / max(1e-9, abs(w.a))
                    tallest = max(tallest, aspect)
                    run = aspect > limit + 1e-9 ? run + dt : 0
                    if run > longest {
                        longest = run
                        action = performer.director.activeGesture ?? "groove"
                    }
                }
            }
            print(String(format: "stretch %@: longest held %.2f s (%@), tallest root %.3f", id, longest, action, tallest))
            #expect(longest < StretchTests.brief, "\(id) \(action)")
        }
    }
}
