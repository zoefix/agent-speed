import Foundation

/// A character's motion personality (motions/profile.py, spec/CHARACTERS.md Motion profile): the manifest's `motion`
/// block. Every key is optional; the defaults are Claude's behavior.
public struct MotionProfile: Sendable, Equatable, Decodable {
    public var tempo = 1.0
    public var energy = 1.0
    public var sway = 1.0
    public var bounce = 1.0
    public var gap = 1.0
    /// The idle groove: `read` (looking down at a book) or `rest`.
    public var idle = "read"
    public var seated = false
    public var talk = true
    public var signature: [String] = []
    public var exclude: [String] = []
    /// Fidget sets of the free hand.
    public var hands: [String] = []
    /// A hand set per pose band, played back to back on its hands.
    public var poseHands: [String: String] = [:]
    /// The ambient particle kind (`petals`, `bubbles`, `notes`, ...).
    public var particles: String?

    public init() {}

    enum CodingKeys: String, CodingKey {
        case tempo, energy, sway, bounce, gap, idle, seated, talk, signature, exclude, hands, poseHands, particles
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        func number(_ key: CodingKeys, _ fallback: Double) -> Double {
            guard let v = try? c.decodeIfPresent(Double.self, forKey: key), v.isFinite else { return fallback }
            return min(max(v, 0), 4)
        }
        tempo = max(0.25, number(.tempo, 1))
        energy = number(.energy, 1)
        sway = number(.sway, 1)
        bounce = number(.bounce, 1)
        gap = max(0.25, number(.gap, 1))
        idle = (try? c.decodeIfPresent(String.self, forKey: .idle)) ?? "read"
        seated = (try? c.decodeIfPresent(Bool.self, forKey: .seated)) ?? false
        talk = (try? c.decodeIfPresent(Bool.self, forKey: .talk)) ?? true
        signature = ((try? c.decodeIfPresent([String].self, forKey: .signature)) ?? [])
            .filter { MotionProfile.actionBands[$0] != nil }
        exclude = (try? c.decodeIfPresent([String].self, forKey: .exclude)) ?? []
        hands = ((try? c.decodeIfPresent([String].self, forKey: .hands)) ?? []).filter { HandActor.sets.contains($0) }
        let raw = (try? c.decodeIfPresent([String: String].self, forKey: .poseHands)) ?? [:]
        poseHands = raw.filter { SpeedBand(rawValue: $0.key) != nil && HandActor.sets.contains($0.value) }
        particles = try? c.decodeIfPresent(String.self, forKey: .particles)
    }

    /// The bands an action fits in, lowest and highest.
    static let actionBands: [String: (SpeedBand, SpeedBand)] = [
        "yawn": (.idle, .cruise), "stretch": (.idle, .cruise), "ponder": (.idle, .cruise),
        "bow": (.idle, .boost), "curtsy": (.idle, .boost), "nod": (.idle, .boost), "tilt": (.idle, .boost),
        "lookaround": (.idle, .boost), "shake": (.idle, .boost), "foottap": (.idle, .boost),
        "floatbob": (.idle, .boost), "hum": (.cruise, .boost), "sway": (.cruise, .boost),
        "tiptoe": (.cruise, .boost), "peek": (.cruise, .boost), "giggle": (.cruise, .overdrive),
        "doubletake": (.cruise, .overdrive), "step": (.idle, .overdrive), "swagger": (.cruise, .overdrive),
        "hop": (.cruise, .redline), "hops": (.boost, .redline), "spin": (.boost, .redline),
        "pounce": (.boost, .redline), "run": (.overdrive, .redline), "jump": (.overdrive, .redline),
        "jumpjoy": (.overdrive, .redline), "tailswish": (.idle, .redline), "wingflap": (.idle, .redline),
        "eartwitch": (.idle, .redline),
    ]
    static let seatedSkip: Set<String> = ["hop", "jump", "hops", "spin", "step", "run", "jumpjoy", "tiptoe", "foottap",
                                          "curtsy", "pounce", "floatbob"]
    static let seatedFreeze = ["PosX", "PosY", "Squash", "Lean", "Hip", "FootLift", "FootPoint", "BodySway",
                               "SkirtFlare"]
    static let silentSkip: Set<String> = ["hum", "giggle"]
    /// Sets played on an instrument held in both hands: while one plays, the arms keep their pose so the hands never
    /// slide along the instrument, and the body carries both with it.
    static let twoHanded: Set<String> = ["flute", "grip"]
    /// Sets played on an instrument at the lips: the head keeps its pose too, so the lips never slide along it.
    static let atMouth: Set<String> = ["flute"]
    static let headParams = ["AngleX", "AngleY", "AngleZ"]
    static let armParams = ["Arm.", "Forearm.", "Shoulder.", "Lift.", "Bend."]

    func allows(_ action: String) -> Bool {
        if exclude.contains(action) { return false }
        if seated, MotionProfile.seatedSkip.contains(action) { return false }
        if !talk, MotionProfile.silentSkip.contains(action) { return false }
        return true
    }

    /// The band's pool: the base actions it allows, then each signature action that fits the band three times in all.
    func pool(_ base: [String], _ band: SpeedBand) -> [String] {
        var out = base.filter { allows($0) }
        for a in signature {
            guard let (lo, hi) = MotionProfile.actionBands[a], allows(a), lo <= band, band <= hi else { continue }
            out += Array(repeating: a, count: base.contains(a) ? 2 : 3)
        }
        return out
    }

    /// How a groove's value for `name` scales: bounce, sway or energy (motions/rhythm.py, groove_scale).
    func grooveScale(_ name: String) -> Double {
        if ["PosY", "Squash", "FootPoint", "FootLift", "SkirtFlare"].contains(name) { return bounce }
        if ["Hip", "BodyAngleZ", "Lean", "AngleZ"].contains(name) { return sway }
        if name.hasPrefix("Arm.") || name.hasPrefix("Forearm.") || name.hasPrefix("Shoulder.") { return energy }
        return 1
    }
}
