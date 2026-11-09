import Foundation
import Testing
@testable import AgentSpeedCharacter

/// The shared fixtures of spec/CHARACTERS.md, section 9.
enum Fixture {
    static let root = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        .appendingPathComponent("spec/fixtures/characters")

    static func data(_ name: String) throws -> Data {
        try Data(contentsOf: root.appendingPathComponent(name))
    }

    static func json(_ name: String) throws -> [String: Any] {
        try JSONSerialization.jsonObject(with: data(name)) as! [String: Any]
    }

    static func doubles(_ any: Any?) -> [Double] {
        (any as? [Any] ?? []).map { ($0 as! NSNumber).doubleValue }
    }

    static func keys(_ any: Any?) -> [CurveKey] {
        (any as! [[Any]]).map { CurveKey(($0[0] as! NSNumber).doubleValue, ($0[1] as! NSNumber).doubleValue) }
    }
}

func close(_ a: Double, _ b: Double, _ tolerance: Double = 1e-6) -> Bool {
    abs(a - b) <= tolerance * max(1, abs(b))
}

@Suite struct CharacterFixtureTests {
    @Test func randomNumbersMatchSplitMix64() throws {
        let doc = try Fixture.json("rng.json")
        for c in doc["cases"] as! [[String: Any]] {
            let seed = UInt64(c["seed"] as! String)!
            var g = Rng(seed: seed)
            #expect((c["u64"] as! [String]).map { UInt64($0)! } == (0..<5).map { _ in g.nextUInt64() })
            // The 64-bit outputs above match exactly; JSONSerialization may read a double one ulp off.
            g = Rng(seed: seed)
            for expected in Fixture.doubles(c["random"]) {
                #expect(close(g.random(), expected, 1e-15))
            }
            g = Rng(seed: seed)
            #expect(close(g.uniform(2, 5), (c["uniform_2_5"] as! NSNumber).doubleValue, 1e-15))
            g = Rng(seed: seed)
            var items = Array(0..<10)
            g.shuffle(&items)
            #expect(items == (c["shuffle10"] as! [Int]))
            g = Rng(seed: seed)
            #expect(g.sample(Array("abcde").map(String.init), 3) == (c["sample_abcde_3"] as! [String]))
        }
    }

    @Test func curvesMatchTheReference() throws {
        let doc = try Fixture.json("curves.json")
        for c in doc["cases"] as! [[String: Any]] {
            let curve = Curve(Fixture.keys(c["keys"]), periodic: c["periodic"] as! Bool)
            for (x, y) in zip(Fixture.doubles(c["x"]), Fixture.doubles(c["y"])) {
                #expect(close(curve(x), y), "x=\(x)")
            }
        }
    }

    @Test func springsMatchTheReference() throws {
        let doc = try Fixture.json("springs.json")
        for c in doc["cases"] as! [[String: Any]] {
            func d(_ k: String) -> Double { (c[k] as! NSNumber).doubleValue }
            var s = SecondOrder(d("f"), d("z"), d("r"), x0: d("x0"))
            for (x, y) in zip(Fixture.doubles(c["inputs"]), Fixture.doubles(c["outputs"])) {
                #expect(close(s.update(d("dt"), x), y))
            }
        }
    }

    @Test func orbitsAndKeplerMatchTheReference() throws {
        let doc = try Fixture.json("orbits.json")
        for c in doc["orbits"] as! [[String: Any]] {
            func d(_ k: String) -> Double { (c[k] as! NSNumber).doubleValue }
            let o = Orbit(a: d("a"), e: d("e"), period: d("period"), phase: d("phase"), omega: d("omega"),
                          height: d("height"), tilt: d("tilt"), precess: d("precess"), seed: d("seed"))
            let center = Fixture.doubles(c["center"])
            let positions = c["xyDepth"] as! [[Any]]
            let spins = Fixture.doubles(c["spin"])
            for (i, t) in Fixture.doubles(c["t"]).enumerated() {
                let p = o.at(t, center[0], center[1])
                let e = Fixture.doubles(positions[i])
                #expect(close(p.x, e[0]) && close(p.y, e[1]) && close(p.depth, e[2]), "t=\(t)")
                #expect(close(o.spin(t), spins[i]))
            }
        }
        for k in doc["kepler"] as! [[String: Any]] {
            let mean = (k["mean"] as! NSNumber).doubleValue, e = (k["e"] as! NSNumber).doubleValue
            #expect(close(Orbit.eccentricAnomaly(mean, e), (k["E"] as! NSNumber).doubleValue))
        }
    }

    @Test func rigMatchesTheReference() throws {
        let doc = try Fixture.json("rig.json")
        let rigData = try JSONSerialization.data(withJSONObject: doc["rig"]!)
        let rig = try CharacterRig(RigDocument.decode(rigData))
        var world: [Affine2] = []
        var opacity: [Double] = []
        var skinned: [Double] = []
        for c in doc["cases"] as! [[String: Any]] {
            let given = c["values"] as! [String: Any]
            let values = rig.params.map { p in (given[p.id] as? NSNumber)?.doubleValue ?? p.default }
            rig.worldTransforms(values, into: &world)
            for (m, e) in zip(world, (c["world"] as! [[Any]]).map(Fixture.doubles)) {
                #expect(close(m.a, e[0]) && close(m.b, e[1]) && close(m.tx, e[2]))
                #expect(close(m.c, e[3]) && close(m.d, e[4]) && close(m.ty, e[5]))
            }
            for (i, e) in (c["vertices"] as! [[Any]]).map(Fixture.doubles).enumerated() {
                rig.skin(part: i, world: world, into: &skinned)
                #expect(zip(skinned, e).allSatisfy { close($0, $1) }, "part \(i) \(given)")
            }
            rig.opacities(values, into: &opacity)
            #expect(zip(opacity, Fixture.doubles(c["opacity"])).allSatisfy { close($0, $1) })
        }
    }

    @Test func physicsMatchesTheReference() throws {
        let doc = try Fixture.json("physics.json")
        let spec = try JSONDecoder().decode(RigDocument.Chain.self,
                                            from: JSONSerialization.data(withJSONObject: doc["spec"]!))
        let names = ["Sway", "Lift", "Wind", "H1", "H2"]
        for c in doc["cases"] as! [[String: Any]] {
            var physics = CharacterPhysics([spec], windSign: (c["windSign"] as! NSNumber).doubleValue) {
                names.firstIndex(of: $0)
            }
            var values = [Double](repeating: 0, count: names.count)
            physics.reset(values)
            let dt = (c["dt"] as! NSNumber).doubleValue
            for frame in c["frames"] as! [[String: Any]] {
                let input = frame["in"] as! [String: NSNumber]
                for (k, v) in input { values[names.firstIndex(of: k)!] = v.doubleValue }
                physics.step(dt, values)
                physics.write(into: &values)
                let out = frame["out"] as! [String: NSNumber]
                #expect(close(values[3], out["H1"]!.doubleValue) && close(values[4], out["H2"]!.doubleValue))
            }
        }
    }

    @Test func odometerMatchesTheReference() throws {
        let doc = try Fixture.json("odometer.json")
        let dt = (doc["dt"] as! NSNumber).doubleValue
        var odo = Odometer()
        for (target, frame) in zip(Fixture.doubles(doc["targets"]), doc["frames"] as! [[String: Any]]) {
            let text = odo.update(dt, target)
            #expect(text == frame["text"] as! String)
            let digits = frame["digits"] as! [[Any]]
            #expect(digits.count == odo.digits.count)
            for (d, e) in zip(odo.digits, digits) {
                #expect(d.current.map(String.init) == e[0] as? String)
                #expect(d.previous.map(String.init) == e[1] as? String)
                #expect(close(d.t, (e[2] as! NSNumber).doubleValue))
            }
        }
    }

    @Test func poseSwitchMatchesTheReference() throws {
        let doc = try Fixture.json("switch.json")
        let dt = (doc["dt"] as! NSNumber).doubleValue
        var s = PoseSwitch()
        s.request(1)
        for frame in doc["frames"] as! [[String: Any]] {
            let (swap, out, _) = s.update(dt)
            let expected = frame["values"] as! [String: NSNumber]
            #expect((swap != nil) == (frame["swap"] as! Bool))
            if expected.isEmpty {
                #expect(out == nil)
                continue
            }
            let o = try #require(out)
            #expect(close(o.spin, expected["Spin"]!.doubleValue))
            #expect(close(o.squash, expected["Squash"]!.doubleValue))
            #expect(close(o.posY, expected["PosY"]!.doubleValue))
            #expect(close(o.skirtFlare, expected["SkirtFlare"]!.doubleValue))
        }
    }

    @Test(arguments: [0, 1])
    func directorMatchesTheReference(_ index: Int) throws {
        let doc = try Fixture.json("director.json")
        let params = try JSONDecoder().decode([CharacterParam].self,
                                              from: JSONSerialization.data(withJSONObject: doc["params"]!))
        let schedule = (doc["schedule"] as! [[NSNumber]]).map { ($0[0].doubleValue, $0[1].intValue) }
        let c = (doc["cases"] as! [[String: Any]])[index]
        var d = CharacterDirector(params: params, seed: UInt64((c["seed"] as! NSNumber).intValue),
                                  out: CharacterSide(rawValue: c["armOut"] as! String)!,
                                  book: CharacterSide(rawValue: c["armBook"] as! String)!,
                                  orb: (c["orb"] as! NSNumber).doubleValue, celebrate: false)
        let frames = c["frames"] as! [[String: Any]]
        var next = 0
        var mismatches: [String] = []
        for n in 0..<(60 * 42) {
            let t = Double(n) / 60
            let level = schedule.last { t >= $0.0 }!.1
            d.setLevel(level)
            d.update(1.0 / 60)
            guard next < frames.count, (frames[next]["n"] as! Int) == n else { continue }
            let frame = frames[next]
            next += 1
            if d.activeGesture != frame["gesture"] as? String {
                mismatches.append("n=\(n) gesture \(d.activeGesture ?? "-") != \(frame["gesture"] as? String ?? "-")")
            }
            for (k, v) in frame["values"] as! [String: NSNumber] {
                let mine = d.value(k)!
                if abs(mine - v.doubleValue) > 1e-4 {
                    mismatches.append("n=\(n) \(k) \(mine) != \(v.doubleValue)")
                }
            }
            if mismatches.count > 20 { break }
        }
        #expect(next == frames.count)
        #expect(mismatches.isEmpty, "\(mismatches.prefix(20).joined(separator: "\n"))")
    }

    @Test(arguments: [0, 1, 2])
    func directorProfilesMatchTheReference(_ index: Int) throws {
        let doc = try Fixture.json("director-profiles.json")
        let params = try JSONDecoder().decode([CharacterParam].self,
                                              from: JSONSerialization.data(withJSONObject: doc["params"]!))
        let schedule = (doc["schedule"] as! [[NSNumber]]).map { ($0[0].doubleValue, $0[1].intValue) }
        var sides = (doc["poseSides"] as! [[Any]]).map { (($0[0] as! NSNumber).doubleValue, $0[1] as! String) }
        let c = (doc["cases"] as! [[String: Any]])[index]
        let profile = try JSONDecoder().decode(MotionProfile.self,
                                               from: JSONSerialization.data(withJSONObject: c["profile"]!))
        let out = CharacterSide(rawValue: c["armOut"] as! String)!, book = CharacterSide(rawValue: c["armBook"] as! String)!
        var d = CharacterDirector(params: params, seed: UInt64((c["seed"] as! NSNumber).intValue), out: out, book: book,
                                  orb: (c["orb"] as! NSNumber).doubleValue, celebrate: false, profile: profile)
        let frames = c["frames"] as! [[String: Any]]
        var next = 0
        var mismatches: [String] = []
        for n in 0..<(60 * 42) {
            let t = Double(n) / 60
            let level = schedule.last { t >= $0.0 }!.1
            d.setLevel(level)
            if let first = sides.first, t >= first.0 {
                d.setSides(out: out, book: book, poseBand: SpeedBand(rawValue: first.1)!)
                sides.removeFirst()
            }
            d.update(1.0 / 60)
            guard next < frames.count, (frames[next]["n"] as! Int) == n else { continue }
            let frame = frames[next]
            next += 1
            if d.activeGesture != frame["gesture"] as? String {
                mismatches.append("n=\(n) gesture \(d.activeGesture ?? "-") != \(frame["gesture"] as? String ?? "-")")
            }
            for (k, v) in frame["values"] as! [String: NSNumber] {
                let mine = d.value(k)!
                if abs(mine - v.doubleValue) > 1e-4 {
                    mismatches.append("n=\(n) \(k) \(mine) != \(v.doubleValue)")
                }
            }
            if mismatches.count > 20 { break }
        }
        #expect(next == frames.count)
        #expect(mismatches.isEmpty, "\(c["name"] as! String): \(mismatches.prefix(20).joined(separator: "\n"))")
    }
}
