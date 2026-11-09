import Foundation

/// `rig.json` of a character package (characters/README.md, format `agent-speed-character` v1). Unknown keys are
/// ignored; landmarks the player does not use are skipped.
public struct RigDocument: Decodable, Sendable {
    public static let format = "agent-speed-character"
    public static let version = 1

    public struct Atlas: Decodable, Sendable {
        public var file: String
        public var size: [Int]
    }

    public struct Bone: Decodable, Sendable {
        public var name: String
        public var parent: String?
        public var pivot: [Double]
    }

    public struct Driver: Decodable, Sendable {
        public var param: String
        public var bone: String
        public var channel: String
        public var keys: [[Double]]
    }

    public struct PartDriver: Decodable, Sendable {
        public var param: String
        public var part: String
        public var keys: [[Double]]
    }

    public struct Part: Decodable, Sendable {
        public var name: String
        public var tag: String
        public var order: Double
        public var vertices: [Double]
        public var uv: [Double]
        public var triangles: [Int]
        public var influences: Int
        public var bones: [Int]
        public var weights: [Double]
    }

    public struct PhysicsInput: Decodable, Sendable {
        public var param: String
        public var x: Double?
        public var y: Double?
    }

    public struct PhysicsOutput: Decodable, Sendable {
        public var param: String
        public var segment: Int
        public var degrees: Double
    }

    public struct Chain: Decodable, Sendable {
        public var name: String
        public var anchor: [Double]
        public var segments: [Double]
        public var inputs: [PhysicsInput]
        public var outputs: [PhysicsOutput]
        public var stiffness: Double
        public var damping: Double
        public var wind: Double
    }

    public struct Hand: Decodable, Sendable {
        public var wrist: [Double]
        public var size: Double
        public var open: Double?
    }

    public struct Landmarks: Decodable, Sendable {
        public var face: [Double]?
        public var faceWidth: Double?
        public var feet: [Double]?
        public var waist: [Double]?
        public var chin: Double?
        public var hem: Double?
        public var torsoLength: Double?
        public var objectSide: String?
        public var hands: [String: Hand]?
    }

    public var format: String
    public var version: Int
    public var name: String
    public var canvas: [Double]
    public var atlas: Atlas
    public var landmarks: Landmarks
    public var bones: [Bone]
    public var params: [CharacterParam]
    public var drivers: [Driver]
    public var physics: [Chain]
    public var partDrivers: [PartDriver]
    public var parts: [Part]

    enum CodingKeys: String, CodingKey {
        case format, version, name, canvas, atlas, landmarks, bones, params, drivers, physics, partDrivers, parts
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        format = try c.decode(String.self, forKey: .format)
        version = try c.decode(Int.self, forKey: .version)
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? ""
        canvas = try c.decode([Double].self, forKey: .canvas)
        atlas = try c.decode(Atlas.self, forKey: .atlas)
        landmarks = try c.decodeIfPresent(Landmarks.self, forKey: .landmarks) ?? Landmarks()
        bones = try c.decode([Bone].self, forKey: .bones)
        params = try c.decode([CharacterParam].self, forKey: .params)
        drivers = try c.decodeIfPresent([Driver].self, forKey: .drivers) ?? []
        physics = try c.decodeIfPresent([Chain].self, forKey: .physics) ?? []
        partDrivers = try c.decodeIfPresent([PartDriver].self, forKey: .partDrivers) ?? []
        parts = try c.decode([Part].self, forKey: .parts)
    }

    public static func decode(_ data: Data) throws -> RigDocument {
        try JSONDecoder().decode(RigDocument.self, from: data)
    }
}

/// Why a package cannot be played.
public enum CharacterRigError: Error, Equatable, Sendable {
    case unsupported(format: String, version: Int)
    case badBone(String)
    case parentsNotFirst(String)
    case badDriver(String)
    case badPart(String)
    case badChain(String)
}
