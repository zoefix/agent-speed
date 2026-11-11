import Foundation
import Testing
@testable import AgentSpeedCharacter

/// `Squash` stretches (+) or squashes (-) her whole figure over the feet, face included: past `held` either way it
/// reads as the art drawn out or pressed flat, so only an instant of a take-off or a landing goes there, and the crouch
/// before a leap no longer than `crouch` (spec/CHARACTERS.md, Motion).
@Suite struct StretchTests {
    static let held = 0.2
    static let brief = 0.25
    static let crouch = 0.5

    /// The longest run of samples `dt` apart above `held` (below `-held` for `sign` -1), in seconds.
    static func longest(_ series: [Double], _ dt: Double, sign: Double = 1) -> Double {
        var run = 0.0, best = 0.0
        for v in series {
            run = sign * v > held + 1e-9 ? run + dt : 0
            best = max(best, run)
        }
        return best
    }

    static func expectInShape(_ series: [Double], _ dt: Double, _ name: String) {
        #expect(longest(series, dt) < brief, "\(name) stretched")
        #expect(longest(series, dt, sign: -1) < crouch, "\(name) squashed")
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
            Self.expectInShape((0...Int(m.duration / dt)).map { curve(Double($0) * dt) }, dt, m.name)
        }
        var swap = PoseSwitch()
        swap.request(1)
        var hop: [Double] = []
        while swap.isRunning { hop.append(swap.update(dt).output?.squash ?? 0) }
        Self.expectInShape(hop, dt, "pose swap")
    }

    @Test func sheIsNeverHeldDrawnOutInAnyBand() throws {
        let params = try Fixture.json("director.json")["params"] as! [[String: Any]]
        let decoded = try JSONDecoder().decode([CharacterParam].self, from: JSONSerialization.data(withJSONObject: params))
        let profile = try JSONDecoder().decode(MotionProfile.self, from: Data(
            #"{"signature": ["stretch", "yawn", "tiptoe", "curtsy", "hop", "pounce"], "bounce": 1.4}"#.utf8))
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
        #expect(played.isSuperset(of: ["stretch", "yawn", "tiptoe", "curtsy"]), "\(played)")
        Self.expectInShape(series, dt, "director")
    }
}

/// `AGENT_SPEED_CHARACTER_PACKS` (a folder of unpacked packs): every pack rests at 0 tok/s for ten minutes, then tours
/// the bands, at the idle band's 15 fps and at 60; her root (all of her) is never held stretched or squashed past
/// `Squash` ±0.2 for longer than an instant (a crouch), outside the turn of a pose swap. Prints the longest runs and the
/// tallest and flattest frames per pack.
@Suite struct StretchPackTests {
    @Test(.enabled(if: ProcessInfo.processInfo.environment["AGENT_SPEED_CHARACTER_PACKS"] != nil))
    func everyPackKeepsHerShape() throws {
        let env = ProcessInfo.processInfo.environment
        let root = URL(fileURLWithPath: env["AGENT_SPEED_CHARACTER_PACKS"]!)
        let ids = try FileManager.default.contentsOfDirectory(atPath: root.path).filter {
            FileManager.default.fileExists(atPath: root.appendingPathComponent("\($0)/manifest.json").path)
        }.sorted().filter { id in env["AGENT_SPEED_CHARACTER_IDS"].map { $0.split(separator: ",").contains(Substring(id)) } ?? true }
        // Root height over width at Squash ±0.2 (rig/params.py: scaleY 0.1, scaleX -0.06 per unit).
        let h = StretchTests.held
        let tall = (1 + 0.1 * h) / (1 - 0.06 * h), flat = (1 - 0.1 * h) / (1 + 0.06 * h)
        let tour = [0, 2, 0, 6, 0, 10, 13, 0, 3, 8, 0]
        for id in ids {
            let pack = try CharacterPack.load(from: root.appendingPathComponent(id))
            var longest = (up: 0.0, down: 0.0), action = (up: "", down: ""), extremes = (tallest: 1.0, flattest: 1.0)
            for (fps, seconds, levels) in [(15.0, 600.0, [0]), (60.0, 330.0, tour)] {
                var performer = CharacterPerformer(pack: pack, seed: 11)
                let spin = performer.director.space.slot("Spin")
                let dt = 1 / fps, frames = Int(seconds * fps)
                var run = (up: 0.0, down: 0.0)
                for n in 0..<frames {
                    performer.step(dt, level: levels[n * levels.count / frames])
                    if let spin, performer.values[spin] != 0 {
                        run = (0, 0)
                        continue
                    }
                    let w = performer.world[0]
                    let aspect = w.d / max(1e-9, abs(w.a))
                    extremes = (max(extremes.tallest, aspect), min(extremes.flattest, aspect))
                    run.up = aspect > tall + 1e-9 ? run.up + dt : 0
                    run.down = aspect < flat - 1e-9 ? run.down + dt : 0
                    let gesture = performer.director.activeGesture ?? "groove"
                    if run.up > longest.up { (longest.up, action.up) = (run.up, gesture) }
                    if run.down > longest.down { (longest.down, action.down) = (run.down, gesture) }
                }
            }
            print(String(format: "stretch %@: longest stretched %.2f s (%@), squashed %.2f s (%@); root %.3f…%.3f", id,
                         longest.up, action.up, longest.down, action.down, extremes.flattest, extremes.tallest))
            #expect(longest.up < StretchTests.brief, "\(id) \(action.up)")
            #expect(longest.down < StretchTests.crouch, "\(id) \(action.down)")
        }
    }
}
