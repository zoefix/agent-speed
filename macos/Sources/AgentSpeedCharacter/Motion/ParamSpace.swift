import Foundation

/// A rig parameter: its id, range and default.
public struct CharacterParam: Sendable, Equatable, Codable {
    public var id: String
    public var min: Double
    public var max: Double
    public var `default`: Double

    public init(id: String, min: Double, max: Double, default value: Double) {
        self.id = id
        self.min = min
        self.max = max
        self.default = value
    }
}

/// The parameters the director writes: the rig's own, then every name a motion may write, each at a fixed slot.
/// Values live in dense arrays indexed by slot; names are resolved once, never per frame.
public struct ParamSpace: Sendable {
    public private(set) var names: [String] = []
    public private(set) var params: [CharacterParam?] = []
    public private(set) var springs: [SpringTuning?] = []
    private var index: [String: Int] = [:]

    public init(params: [CharacterParam]) {
        for p in params where index[p.id] == nil {
            add(p.id, p)
        }
        for name in ParamSpace.vocabulary where index[name] == nil {
            add(name, nil)
        }
    }

    private mutating func add(_ name: String, _ param: CharacterParam?) {
        index[name] = names.count
        names.append(name)
        params.append(param)
        springs.append(SpringTuning.of(name))
    }

    public var count: Int { names.count }

    public func slot(_ name: String) -> Int? { index[name] }

    public func isParam(_ slot: Int) -> Bool { params[slot] != nil }

    public func isParam(_ name: String) -> Bool { index[name].map { params[$0] != nil } ?? false }

    /// Every parameter a motion of the reference can write.
    public static let vocabulary: [String] = {
        var names = ["AngleX", "AngleY", "AngleZ", "BodyAngleZ", "BodySway", "Breath", "EyeOpen.left", "EyeOpen.right",
                     "EyeBallX", "EyeBallY", "Wind", "PosX", "PosY", "Squash", "Lean", "Spin", "Hip", "SkirtFlare",
                     "FootLift", "FootPoint", "TailSwish", "WingFlap", "EarTwitch.left", "EarTwitch.right"]
        for side in CharacterSide.allCases {
            let s = side.rawValue
            names += ["Shoulder.\(s)", "Arm.\(s)", "Forearm.\(s)", "Lift.\(s)", "Bend.\(s)", "Wrist.\(s)", "Spread.\(s)"]
            names += (0..<5).map { "Finger.\(s).\($0)" }
        }
        return names
    }()
}

/// An image side of the character (the reference's `left` / `right`).
public enum CharacterSide: String, Sendable, CaseIterable, Codable {
    case left, right

    public var other: CharacterSide { self == .left ? .right : .left }
}

/// The slots the motions write, resolved once from a `ParamSpace`.
struct Slots: Sendable {
    struct Side: Sendable {
        let shoulder, arm, forearm, lift, bend, wrist, spread: Int
        let fingers: [Int]
    }

    let angleX, angleY, angleZ, bodyAngleZ, bodySway, breath, eyeBallX, eyeBallY, wind: Int
    let posX, posY, squash, lean, spin, hip, skirtFlare, footLift, footPoint: Int
    let tailSwish, wingFlap: Int
    let eyeOpen: [Int]
    let left: Side
    let right: Side

    init(_ space: ParamSpace) {
        func s(_ name: String) -> Int { space.slot(name)! }
        angleX = s("AngleX"); angleY = s("AngleY"); angleZ = s("AngleZ")
        bodyAngleZ = s("BodyAngleZ"); bodySway = s("BodySway"); breath = s("Breath")
        eyeBallX = s("EyeBallX"); eyeBallY = s("EyeBallY"); wind = s("Wind")
        posX = s("PosX"); posY = s("PosY"); squash = s("Squash"); lean = s("Lean"); spin = s("Spin"); hip = s("Hip")
        skirtFlare = s("SkirtFlare"); footLift = s("FootLift"); footPoint = s("FootPoint")
        tailSwish = s("TailSwish"); wingFlap = s("WingFlap")
        eyeOpen = [s("EyeOpen.left"), s("EyeOpen.right")]
        func side(_ x: String) -> Side {
            Side(shoulder: s("Shoulder.\(x)"), arm: s("Arm.\(x)"), forearm: s("Forearm.\(x)"), lift: s("Lift.\(x)"),
                 bend: s("Bend.\(x)"), wrist: s("Wrist.\(x)"), spread: s("Spread.\(x)"),
                 fingers: (0..<5).map { s("Finger.\(x).\($0)") })
        }
        left = side("left")
        right = side("right")
    }

    func side(_ side: CharacterSide) -> Side { side == .left ? left : right }
}
