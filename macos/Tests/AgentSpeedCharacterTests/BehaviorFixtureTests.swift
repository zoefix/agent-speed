import Foundation
import Testing
@testable import AgentSpeedCharacter

/// spec/fixtures/characters/behavior.json: a living director through a scripted session (spec/CHARACTERS.md,
/// Behavior; characters/tests/make_fixtures.py, behavior_cases).
struct BehaviorSession {
    let doc: [String: Any]

    private func rows(_ key: String) -> [[Any]] { doc[key] as? [[Any]] ?? [] }
    private func number(_ any: Any) -> Double { (any as! NSNumber).doubleValue }

    var seconds: Double { number(doc["seconds"]!) }

    func level(at t: Double) -> Int {
        rows("levels").last { t >= number($0[0]) }.map { ($0[1] as! NSNumber).intValue } ?? 0
    }

    func inputs(at t: Double, dt: Double) -> BehaviorInputs {
        var phase = "idle", idleAt = 0.0, since = 0.0
        for row in rows("phases") where t >= number(row[0]) {
            phase = row[1] as! String
            idleAt = number(row[2])
            since = number(row[0])
        }
        let wait = rows("requests").first { number($0[0]) <= t && t < number($0[1]) }.map { t - number($0[0]) }
        var idle = 0.0
        if phase == "idle" {
            var jumps = 0.0
            for row in rows("idleJumps") where t >= number(row[0]) { jumps += number(row[1]) }
            idle = idleAt + t - since + jumps
        }
        let keys = rows("cursor")
        var cursor: (x: Double, y: Double)?
        for (a, b) in zip(keys, keys.dropFirst()) {
            let t0 = number(a[0]), t1 = number(b[0])
            if t0 <= t, t <= t1 {
                let k = (t - t0) / (t1 - t0)
                cursor = (number(a[1]) + (number(b[1]) - number(a[1])) * k, number(a[2]) + (number(b[2]) - number(a[2])) * k)
                break
            }
        }
        let box = Fixture.doubles(doc["over"])
        let over = cursor.map { box[0] <= $0.x && $0.x <= box[1] && box[2] <= $0.y && $0.y <= box[3] } ?? false
        func fires(_ at: Double) -> Bool { t - dt < at && at <= t }
        let events = rows("events").filter { fires(number($0[0])) }.map { $0[1] as! String }
        let click = over && (doc["clicks"] as! [NSNumber]).contains { fires($0.doubleValue) }
        return BehaviorInputs(phase: AgentActivity(rawValue: phase)!, wait: wait, idle: idle,
                              hour: (doc["hour"] as! NSNumber).intValue, newPeak: events.contains("newPeak"),
                              trouble: events.contains("trouble"), cursor: cursor, over: over, click: click)
    }
}

@Suite struct BehaviorFixtureTests {
    @Test(arguments: [0, 1])
    func behaviorMatchesTheReference(_ index: Int) throws {
        let doc = try Fixture.json("behavior.json")
        let session = BehaviorSession(doc: doc["session"] as! [String: Any])
        let c = (doc["cases"] as! [[String: Any]])[index]
        let variants = try JSONDecoder().decode([RigDocument.Variant].self,
                                                from: JSONSerialization.data(withJSONObject: c["variants"]!))
        var params = try JSONDecoder().decode([CharacterParam].self,
                                              from: JSONSerialization.data(withJSONObject: doc["params"]!))
        for v in variants {
            params.append(CharacterParam(id: v.param, min: 0, max: 1, default: 0))
            if v.kind == "arm" {
                for pid in [v.arm!, v.forearm!] { params.append(CharacterParam(id: pid, min: -1, max: 1, default: 0)) }
            }
        }
        var d = CharacterDirector(params: params, seed: UInt64((c["seed"] as! NSNumber).intValue),
                                  out: CharacterSide(rawValue: c["armOut"] as! String)!,
                                  book: CharacterSide(rawValue: c["armBook"] as! String)!,
                                  orb: (c["orb"] as! NSNumber).doubleValue, celebrate: false, variants: variants,
                                  states: c["states"] as! [String])
        #expect(d.isLiving)
        let frames = c["frames"] as! [[String: Any]]
        let dt = 1.0 / 60
        var next = 0
        var mismatches: [String] = []
        for n in 0..<Int(session.seconds * 60) {
            let t = Double(n) / 60
            d.setLevel(session.level(at: t))
            d.update(dt, inputs: session.inputs(at: t, dt: dt))
            guard next < frames.count, (frames[next]["n"] as! Int) == n else { continue }
            let frame = frames[next]
            next += 1
            let pose: String? = switch d.poseWanted() {
            case .keep: "keep"
            case .state(let name): name
            case nil: nil
            }
            let mine: [(String, String?)] = [("mood", d.mood?.rawValue), ("pose", pose), ("face", d.expression),
                                             ("gaze", d.gazePatch), ("mouth", d.behavior?.talk.shape),
                                             ("gesture", d.activeGesture)]
            for (key, value) in mine where value != frame[key] as? String {
                mismatches.append("n=\(n) \(key) \(value ?? "-") != \(frame[key] as? String ?? "-")")
            }
            for (k, v) in frame["values"] as! [String: NSNumber] {
                let value = d.value(k)!
                if abs(value - v.doubleValue) > 1e-4 {
                    mismatches.append("n=\(n) \(k) \(value) != \(v.doubleValue)")
                }
            }
            if mismatches.count > 20 { break }
        }
        #expect(next == frames.count)
        #expect(mismatches.isEmpty, "\(c["name"] as! String): \(mismatches.prefix(20).joined(separator: "\n"))")
    }
}
