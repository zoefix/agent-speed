import Foundation

/// An installed character pack: its manifest and the folder that holds it (CHARACTERS 2).
public struct InstalledCharacter: Equatable, Hashable, Sendable, Identifiable {
    public var manifest: CharacterManifest
    public var folder: URL
    /// Listed from the `AGENT_SPEED_CHARACTERS` development folder; it cannot be removed or updated from the app.
    public var isDevelopment: Bool

    public var id: String { manifest.id }

    public init(manifest: CharacterManifest, folder: URL, isDevelopment: Bool = false) {
        self.manifest = manifest
        self.folder = folder
        self.isDevelopment = isDevelopment
    }

    public var previewURL: URL { folder.appendingPathComponent(manifest.preview, isDirectory: false) }

    /// The folder holding `rig.json` and the atlas of a band's pose.
    public func poseFolder(for band: CharacterBand) -> URL {
        folder.appendingPathComponent(manifest.pose(for: band), isDirectory: true)
    }
}

/// Installed packs on disk: `<root>/<id>/` (CHARACTERS 2). Installing and removing replace whole folders, so a
/// player never sees a half-written pack.
public struct CharacterStore: Sendable {
    public static let developmentVariable = "AGENT_SPEED_CHARACTERS"
    static let manifestLimit = 64 << 10

    public let root: URL
    public let development: URL?
    public let appVersion: String?

    public init(root: URL, development: URL? = nil, appVersion: String? = nil) {
        self.root = root
        self.development = development
        self.appVersion = appVersion
    }

    /// `<app data>/Characters`, with the development folder from the environment when set.
    public static func standard(appData: URL, appVersion: String?,
                                environment: [String: String] = ProcessInfo.processInfo.environment) -> CharacterStore {
        let dev = environment[developmentVariable].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0, isDirectory: true) }
        return CharacterStore(root: appData.appendingPathComponent("Characters", isDirectory: true), development: dev,
                              appVersion: appVersion)
    }

    /// Every valid pack, development ones first, then installed ones by id; a development pack hides an installed one
    /// with the same id.
    public func installed() -> [InstalledCharacter] {
        var out: [InstalledCharacter] = []
        var seen = Set<String>()
        for (folder, dev) in [(development, true), (root, false)] {
            guard let folder else { continue }
            for pack in Self.scan(folder, isDevelopment: dev, appVersion: appVersion) where seen.insert(pack.id).inserted {
                out.append(pack)
            }
        }
        return out
    }

    public func installed(_ id: String) -> InstalledCharacter? {
        installed().first { $0.id == id }
    }

    /// Reads and validates one unpacked pack folder; nil when anything required is missing or invalid.
    public static func read(_ folder: URL, isDevelopment: Bool = false, appVersion: String?) -> InstalledCharacter? {
        guard let manifest = try? manifest(in: folder, appVersion: appVersion) else { return nil }
        return InstalledCharacter(manifest: manifest, folder: folder, isDevelopment: isDevelopment)
    }

    /// The validated manifest of an unpacked pack whose every pose holds `rig.json` and the preview exists.
    public static func manifest(in folder: URL, appVersion: String?) throws -> CharacterManifest {
        let file = folder.appendingPathComponent("manifest.json", isDirectory: false)
        let values = try? file.resourceValues(forKeys: [.fileSizeKey, .isSymbolicLinkKey, .isRegularFileKey])
        guard values?.isRegularFile == true, values?.isSymbolicLink != true, (values?.fileSize ?? .max) <= manifestLimit else {
            throw CharacterPackError.missingFile("manifest.json")
        }
        let manifest = try JSONDecoder().decode(CharacterManifest.self, from: Data(contentsOf: file))
        try manifest.validate(appVersion: appVersion)
        for path in Set(manifest.poses.values) {
            let rig = folder.appendingPathComponent(path, isDirectory: true).appendingPathComponent("rig.json")
            guard FileManager.default.fileExists(atPath: rig.path) else { throw CharacterPackError.missingFile(path + "/rig.json") }
        }
        guard FileManager.default.fileExists(atPath: folder.appendingPathComponent(manifest.preview).path) else {
            throw CharacterPackError.missingFile(manifest.preview)
        }
        return manifest
    }

    /// Moves a validated, unpacked pack into place, replacing an older copy atomically. `expectedID` must match.
    @discardableResult
    public func install(from unpacked: URL, expectedID: String) throws -> InstalledCharacter {
        let manifest = try Self.manifest(in: unpacked, appVersion: appVersion)
        guard manifest.id == expectedID else { throw CharacterPackError.mismatch }
        try Self.verifyTree(unpacked)
        let manager = FileManager.default
        try manager.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let target = root.appendingPathComponent(manifest.id, isDirectory: true)
        let staged = root.appendingPathComponent(".incoming-" + UUID().uuidString, isDirectory: true)
        try manager.moveItem(at: unpacked, to: staged)
        if manager.fileExists(atPath: target.path) {
            _ = try manager.replaceItemAt(target, withItemAt: staged)
        } else {
            try manager.moveItem(at: staged, to: target)
        }
        return InstalledCharacter(manifest: manifest, folder: target)
    }

    /// Deletes an installed (non-development) pack.
    public func remove(_ id: String) throws {
        guard CharacterID.isValid(id) else { throw CharacterPackError.invalidID }
        let target = root.appendingPathComponent(id, isDirectory: true)
        guard FileManager.default.fileExists(atPath: target.path) else { return }
        try FileManager.default.removeItem(at: target)
    }

    /// Only regular files and folders, no links, within the size limit.
    public static func verifyTree(_ folder: URL) throws {
        let keys: [URLResourceKey] = [.isRegularFileKey, .isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey]
        guard let walker = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: keys, options: [],
                                                          errorHandler: { _, _ in false }) else {
            throw CharacterPackError.missingFile(folder.lastPathComponent)
        }
        var total: UInt64 = 0
        for case let item as URL in walker {
            let values = try item.resourceValues(forKeys: Set(keys))
            if values.isSymbolicLink == true { throw CharacterPackError.symbolicLink(item.lastPathComponent) }
            if values.isRegularFile == true {
                total += UInt64(values.fileSize ?? 0)
                guard total <= CharacterPackLayout.maxExpandedSize else { throw CharacterPackError.tooLarge }
            } else if values.isDirectory != true {
                throw CharacterPackError.unsafePath(item.lastPathComponent)
            }
        }
    }

    private static func scan(_ folder: URL, isDevelopment: Bool, appVersion: String?) -> [InstalledCharacter] {
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: folder.path) else { return [] }
        return names.sorted().compactMap { name in
            guard CharacterID.isValid(name) else { return nil }
            guard let pack = read(folder.appendingPathComponent(name, isDirectory: true), isDevelopment: isDevelopment,
                                  appVersion: appVersion), pack.id == name else { return nil }
            return pack
        }
    }
}
