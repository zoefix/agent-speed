import AgentSpeedCharacter
import Foundation

/// A scripted session for the `character` clip (`AGENT_SPEED_ORB_SESSION=<file.json>`): the agents' phases, waits, idle
/// time, events, clicks and the cursor of a living character (CHARACTERS 5, Behavior), in the format of the `session`
/// of spec/fixtures/characters/behavior.json, plus `speed`: `[[t, tok/s], …]` keys, linear between them (held after
/// the last), in place of the tour. Times are seconds from the clip's start; the cursor is in her heights relative to
/// her face, linear between its keys and absent outside them, and replaces the pointer.
struct CharacterSessionScript {
    var hour = 12
    var phases: [(start: Double, phase: AgentActivity, idleAt: Double)] = []
    var requests: [(start: Double, first: Double)] = []
    var idleJumps: [(at: Double, by: Double)] = []
    var events: [(at: Double, name: String)] = []
    var clicks: [Double] = []
    var cursor: [(t: Double, x: Double, y: Double)] = []
    var speed: [(t: Double, value: Double)] = []
    var seconds: Double?

    init?(json data: Data) {
        guard let doc = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return nil }
        func rows(_ key: String) -> [[Any]] { doc[key] as? [[Any]] ?? [] }
        func number(_ any: Any?) -> Double? { (any as? NSNumber)?.doubleValue }
        hour = (doc["hour"] as? NSNumber)?.intValue ?? 12
        phases = rows("phases").compactMap { row in
            guard row.count >= 2, let start = number(row[0]), let name = row[1] as? String,
                  let phase = AgentActivity(rawValue: name) else { return nil }
            return (start, phase, row.count > 2 ? number(row[2]) ?? 0 : 0)
        }
        requests = rows("requests").compactMap { row in
            guard row.count >= 2, let a = number(row[0]), let b = number(row[1]) else { return nil }
            return (a, b)
        }
        idleJumps = rows("idleJumps").compactMap { row in
            guard row.count >= 2, let a = number(row[0]), let b = number(row[1]) else { return nil }
            return (a, b)
        }
        events = rows("events").compactMap { row in
            guard row.count >= 2, let at = number(row[0]), let name = row[1] as? String else { return nil }
            return (at, name)
        }
        clicks = (doc["clicks"] as? [NSNumber] ?? []).map(\.doubleValue)
        cursor = rows("cursor").compactMap { row in
            guard row.count >= 3, let t = number(row[0]), let x = number(row[1]), let y = number(row[2]) else { return nil }
            return (t, x, y)
        }
        speed = rows("speed").compactMap { row in
            guard row.count >= 2, let t = number(row[0]), let v = number(row[1]) else { return nil }
            return (t, v)
        }
        seconds = number(doc["seconds"])
    }

    static func load(_ environment: [String: String]) -> CharacterSessionScript? {
        guard let path = environment["AGENT_SPEED_ORB_SESSION"],
              let data = try? Data(contentsOf: URL(fileURLWithPath: path)) else { return nil }
        return CharacterSessionScript(json: data)
    }

    func phase(at t: Double) -> (phase: AgentActivity, idleAt: Double, since: Double) {
        var out = (AgentActivity.idle, 0.0, 0.0)
        for p in phases where t >= p.start { out = (p.phase, p.idleAt, p.start) }
        return out
    }

    func wait(at t: Double) -> Double? {
        requests.first { $0.start <= t && t < $0.first }.map { t - $0.start }
    }

    func idle(at t: Double) -> Double {
        let p = phase(at: t)
        guard p.phase == .idle else { return 0 }
        return p.idleAt + t - p.since + idleJumps.filter { t >= $0.at }.reduce(0) { $0 + $1.by }
    }

    func cursor(at t: Double) -> (x: Double, y: Double)? {
        for (a, b) in zip(cursor, cursor.dropFirst()) where a.t <= t && t <= b.t {
            let k = (t - a.t) / max(b.t - a.t, 1e-9)
            return (a.x + (b.x - a.x) * k, a.y + (b.y - a.y) * k)
        }
        return nil
    }

    func speed(at t: Double) -> Double? {
        guard let first = speed.first else { return nil }
        if t <= first.t { return first.value }
        for (a, b) in zip(speed, speed.dropFirst()) where t <= b.t {
            return a.value + (b.value - a.value) * (t - a.t) / max(b.t - a.t, 1e-9)
        }
        return speed.last?.value
    }

    /// Feeds the step that ends at `t` (of `dt`) to the character's live inputs, on the stage clock `now`.
    @MainActor
    func apply(at t: Double, dt: Double, now: TimeInterval, to live: OrbCharacterLive, celebrate: () -> Void) {
        let p = phase(at: t)
        live.scripted = true
        live.phase = p.phase
        live.waitingSince = p.phase == .generating ? wait(at: t).map { now - $0 } : nil
        live.idleSince = now - idle(at: t)
        live.hour = hour
        live.scriptedCursor = cursor(at: t)
        for e in events where t - dt < e.at && e.at <= t {
            switch e.name {
            case "newPeak":
                live.notePeak()
                celebrate()
            case "trouble": live.noteTrouble()
            default: break
            }
        }
        if clicks.contains(where: { t - dt < $0 && $0 <= t }) { live.scriptedClick() }
    }
}
