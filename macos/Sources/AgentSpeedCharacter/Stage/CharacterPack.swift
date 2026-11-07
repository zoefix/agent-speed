import Foundation

/// `manifest.json` of a character pack (spec/CHARACTERS.md, Character packs).
public struct CharacterManifest: Decodable, Sendable, Equatable {
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
public enum CharacterPackError: Error, Equatable, Sendable {
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
    public let manifest: CharacterManifest
    /// Present bands in band order; the first is idle.
    public let poses: [CharacterPose]

    public var id: String { manifest.id }
    public var previewURL: URL { root.appendingPathComponent(manifest.preview) }

    /// Loads and validates a pack folder: known format, a safe id, relative paths that stay inside the folder (no
    /// `..`, no absolute paths, no symbolic links leading out), and rigs the player understands.
    public static func load(from root: URL) throws -> CharacterPack {
        let root = root.standardizedFileURL.resolvingSymlinksInPath()
        let manifest: CharacterManifest
        do {
            manifest = try JSONDecoder().decode(CharacterManifest.self,
                                                from: Data(contentsOf: root.appendingPathComponent("manifest.json")))
        } catch {
            throw CharacterPackError.unreadable("manifest.json")
        }
        guard manifest.format == CharacterManifest.format, manifest.version == CharacterManifest.version else {
            throw CharacterPackError.unsupported(format: manifest.format, version: manifest.version)
        }
        guard isSafeID(manifest.id) else { throw CharacterPackError.badID(manifest.id) }
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
                throw CharacterPackError.unreadable("\(folder)/rig.json")
            }
            let rig: CharacterRig
            do {
                rig = try CharacterRig(doc)
            } catch let error as CharacterRigError {
                throw CharacterPackError.rig(band: band.rawValue, error)
            }
            let atlas = try resolve(rig.atlasFile, in: dir)
            poses.append(CharacterPose(band: band, rig: rig, atlasURL: atlas))
        }
        guard poses.first?.band == .idle else { throw CharacterPackError.missingIdlePose }
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
        else { throw CharacterPackError.unsafePath(relative) }
        let url = root.appendingPathComponent(relative).standardizedFileURL.resolvingSymlinksInPath()
        let base = root.path.hasSuffix("/") ? root.path : root.path + "/"
        guard url.path.hasPrefix(base) else { throw CharacterPackError.unsafePath(relative) }
        return url
    }
}
