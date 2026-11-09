import Foundation

/// `manifest.json` of a character pack (spec/CHARACTERS.md, Character packs).
public struct PackManifest: Decodable, Sendable, Equatable {
    public static let format = "agent-speed-character-pack"
    public static let version = 1

    public struct Artist: Decodable, Sendable, Equatable {
        public var name: String
        public var url: String?
    }

    public var format: String
    public var version: Int
    public var id: String
    public var packVersion: Int
    /// Display names by app language (`zh-Hant`, `zh-Hans`, `en`, `ja`).
    public var name: [String: String]
    /// `providers` keys of prices.json this character stands for.
    public var vendors: [String]
    /// `#RRGGBB`.
    public var accent: String
    public var artist: Artist?
    public var license: String?
    public var minApp: String?
    /// Band raw value → folder inside the pack.
    public var poses: [String: String]
    public var preview: String
    /// The character's motion personality; nil moves like Claude.
    public var motion: MotionProfile?

    enum CodingKeys: String, CodingKey {
        case format, version, id, packVersion, name, vendors, accent, artist, license, minApp, poses, preview, motion
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        format = try c.decode(String.self, forKey: .format)
        version = try c.decode(Int.self, forKey: .version)
        id = try c.decode(String.self, forKey: .id)
        packVersion = try c.decode(Int.self, forKey: .packVersion)
        name = try c.decode([String: String].self, forKey: .name)
        vendors = try c.decode([String].self, forKey: .vendors)
        accent = try c.decode(String.self, forKey: .accent)
        artist = try? c.decodeIfPresent(Artist.self, forKey: .artist)
        license = try? c.decodeIfPresent(String.self, forKey: .license)
        minApp = try? c.decodeIfPresent(String.self, forKey: .minApp)
        poses = try c.decode([String: String].self, forKey: .poses)
        preview = try c.decode(String.self, forKey: .preview)
        motion = try? c.decodeIfPresent(MotionProfile.self, forKey: .motion)
    }

    /// Whether an app of `version` (dotted numbers) can play the pack.
    public func playable(byApp version: String) -> Bool {
        guard let minApp else { return true }
        func parts(_ s: String) -> [Int] { s.split(separator: ".").map { Int($0) ?? 0 } }
        let a = parts(version), b = parts(minApp)
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0, y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return true
    }
}

/// Why a pack cannot be loaded.
public enum PackLoadError: Error, Equatable, Sendable {
    case unsupported(format: String, version: Int)
    case badID(String)
    case unsafePath(String)
    case missingIdlePose
    case unreadable(String)
    case rig(band: String, CharacterRigError)
}

/// One band's illustration: its rig and the atlas image the renderer loads.
public struct CharacterPose: Sendable {
    public let band: SpeedBand
    public let rig: CharacterRig
    public let atlasURL: URL
}

/// An unpacked character pack: its manifest and one rig per band present (idle is required).
public struct CharacterPack: Sendable {
    public let root: URL
    public let manifest: PackManifest
    /// Present bands in band order; the first is idle.
    public let poses: [CharacterPose]

    public var id: String { manifest.id }
    public var previewURL: URL { root.appendingPathComponent(manifest.preview) }

    /// Loads and validates a pack folder: known format, a safe id, relative paths that stay inside the folder (no
    /// `..`, no absolute paths, no symbolic links leading out), and rigs the player understands.
    public static func load(from root: URL) throws -> CharacterPack {
        let root = root.standardizedFileURL.resolvingSymlinksInPath()
        let manifest: PackManifest
        do {
            manifest = try JSONDecoder().decode(PackManifest.self,
                                                from: Data(contentsOf: root.appendingPathComponent("manifest.json")))
        } catch {
            throw PackLoadError.unreadable("manifest.json")
        }
        guard manifest.format == PackManifest.format, manifest.version == PackManifest.version else {
            throw PackLoadError.unsupported(format: manifest.format, version: manifest.version)
        }
        guard isSafeID(manifest.id) else { throw PackLoadError.badID(manifest.id) }
        _ = try resolve(manifest.preview, in: root)
        var poses: [CharacterPose] = []
        for band in SpeedBand.allCases {
            guard let folder = manifest.poses[band.rawValue] else { continue }
            let dir = try resolve(folder, in: root)
            let rigURL = try resolve("rig.json", in: dir)
            let doc: RigDocument
            do {
                doc = try RigDocument.decode(Data(contentsOf: rigURL))
            } catch {
                throw PackLoadError.unreadable("\(folder)/rig.json")
            }
            let rig: CharacterRig
            do {
                rig = try CharacterRig(doc)
            } catch let error as CharacterRigError {
                throw PackLoadError.rig(band: band.rawValue, error)
            }
            let atlas = try resolve(rig.atlasFile, in: dir)
            poses.append(CharacterPose(band: band, rig: rig, atlasURL: atlas))
        }
        guard poses.first?.band == .idle else { throw PackLoadError.missingIdlePose }
        return CharacterPack(root: root, manifest: manifest, poses: poses)
    }

    static func isSafeID(_ id: String) -> Bool {
        !id.isEmpty && id.count <= 64 && id.allSatisfy { ($0.isLowercase && $0.isASCII) || $0.isNumber || $0 == "-" }
    }

    /// `relative` inside `root`, refusing absolute paths, `..`, empty components and links that lead outside.
    static func resolve(_ relative: String, in root: URL) throws -> URL {
        let parts = relative.split(separator: "/", omittingEmptySubsequences: false)
        guard !relative.isEmpty, !relative.hasPrefix("/"), !relative.contains("\\"),
              parts.allSatisfy({ !$0.isEmpty && $0 != "." && $0 != ".." })
        else { throw PackLoadError.unsafePath(relative) }
        let url = root.appendingPathComponent(relative).standardizedFileURL.resolvingSymlinksInPath()
        let base = root.path.hasSuffix("/") ? root.path : root.path + "/"
        guard url.path.hasPrefix(base) else { throw PackLoadError.unsafePath(relative) }
        return url
    }
}
