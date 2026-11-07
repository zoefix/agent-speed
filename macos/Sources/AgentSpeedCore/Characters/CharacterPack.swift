import Foundation

/// Pack ids (CHARACTERS 1): lowercase letters, digits and dashes, 1–64 characters, not starting or ending with a dash.
public enum CharacterID {
    public static func isValid(_ id: String) -> Bool {
        guard (1...64).contains(id.count), !id.hasPrefix("-"), !id.hasSuffix("-") else { return false }
        return id.unicodeScalars.allSatisfy { ("a"..."z").contains($0) || ("0"..."9").contains($0) || $0 == "-" }
    }
}

/// The speed bands a pack can hold a pose for (CHARACTERS 1); a missing band uses the nearest lower one.
public enum CharacterBand: String, CaseIterable, Codable, Sendable, Comparable {
    case idle, cruise, boost, overdrive, redline

    public static func < (lhs: CharacterBand, rhs: CharacterBand) -> Bool {
        allCases.firstIndex(of: lhs)! < allCases.firstIndex(of: rhs)!
    }
}

/// Display names of a pack in the four app languages.
public struct CharacterNames: Codable, Equatable, Hashable, Sendable {
    public var zhHant: String
    public var zhHans: String
    public var en: String
    public var ja: String

    enum CodingKeys: String, CodingKey {
        case zhHant = "zh-Hant", zhHans = "zh-Hans", en, ja
    }

    public init(zhHant: String, zhHans: String, en: String, ja: String) {
        self.zhHant = zhHant
        self.zhHans = zhHans
        self.en = en
        self.ja = ja
    }

    var isComplete: Bool { [zhHant, zhHans, en, ja].allSatisfy { !$0.trimmingCharacters(in: .whitespaces).isEmpty } }
}

public struct CharacterArtist: Codable, Equatable, Hashable, Sendable {
    public var name: String
    public var url: String?

    public init(name: String, url: String? = nil) {
        self.name = name
        self.url = url
    }

    /// The credit link, only when it is an https URL.
    public var link: URL? {
        guard let url, let link = URL(string: url), link.scheme?.lowercased() == "https", link.host != nil else { return nil }
        return link
    }
}

public enum CharacterPackError: Error, Equatable, Sendable {
    case format
    case invalidID
    case missingIdle
    case unsafePath(String)
    case symbolicLink(String)
    case missingFile(String)
    case tooLarge
    case newerApp(String)
    case mismatch
}

/// `manifest.json` of a pack (CHARACTERS 1).
public struct CharacterManifest: Codable, Equatable, Hashable, Sendable {
    public static let format = "agent-speed-character-pack"
    public static let version = 1

    public var format: String
    public var version: Int
    public var id: String
    public var packVersion: Int
    public var name: CharacterNames
    public var vendors: [String]
    public var accent: String
    public var artist: CharacterArtist?
    public var license: String?
    public var minApp: String?
    public var poses: [String: String]
    public var preview: String

    public init(
        id: String, packVersion: Int, name: CharacterNames, vendors: [String], accent: String,
        artist: CharacterArtist? = nil, license: String? = nil, minApp: String? = nil,
        poses: [String: String], preview: String = "preview.png"
    ) {
        format = Self.format
        version = Self.version
        self.id = id
        self.packVersion = packVersion
        self.name = name
        self.vendors = vendors
        self.accent = accent
        self.artist = artist
        self.license = license
        self.minApp = minApp
        self.poses = poses
        self.preview = preview
    }

    /// Checks everything a player relies on; `appVersion` (`x.y.z`) rejects packs that need a newer app.
    public func validate(appVersion: String?) throws {
        guard format == Self.format, version == Self.version else { throw CharacterPackError.format }
        guard CharacterID.isValid(id), packVersion >= 1, name.isComplete, Self.isColor(accent) else { throw CharacterPackError.invalidID }
        guard poses[CharacterBand.idle.rawValue] != nil else { throw CharacterPackError.missingIdle }
        for (band, path) in poses {
            guard CharacterBand(rawValue: band) != nil else { throw CharacterPackError.format }
            guard CharacterPackLayout.isSafe(path) else { throw CharacterPackError.unsafePath(path) }
        }
        guard CharacterPackLayout.isSafe(preview) else { throw CharacterPackError.unsafePath(preview) }
        if let minApp, let appVersion, let needed = SemanticVersion(minApp), let current = SemanticVersion(appVersion),
           current < needed, current != SemanticVersion(major: 0, minor: 0, patch: 0) {
            throw CharacterPackError.newerApp(minApp)
        }
    }

    /// The pose folder of a band: its own, else the nearest lower band's (idle always exists after validation).
    public func pose(for band: CharacterBand) -> String {
        for candidate in CharacterBand.allCases.reversed() where candidate <= band {
            if let path = poses[candidate.rawValue] { return path }
        }
        return poses[CharacterBand.idle.rawValue] ?? "poses/idle"
    }

    public var accentRGB: (r: Double, g: Double, b: Double)? { Self.rgb(accent) }

    static func isColor(_ text: String) -> Bool { rgb(text) != nil }

    /// `#RRGGBB` as 0...1 components; nil for anything else.
    public static func rgb(_ text: String) -> (r: Double, g: Double, b: Double)? {
        guard text.count == 7, text.hasPrefix("#"), let value = UInt32(text.dropFirst(), radix: 16) else { return nil }
        return (Double(value >> 16 & 0xFF) / 255, Double(value >> 8 & 0xFF) / 255, Double(value & 0xFF) / 255)
    }
}

/// `catalog.json` of the `characters` release (CHARACTERS 2).
public struct CharacterCatalog: Codable, Equatable, Sendable {
    public static let format = "agent-speed-character-catalog"
    public static let version = 1
    public static let url = URL(string: "https://github.com/zoefix/agent-speed/releases/download/characters/catalog.json")!
    /// Hosts a catalog or pack download may touch, redirects included.
    public static let allowedHosts: Set<String> = [
        "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    ]
    public static let maxCatalogBytes = 1 << 20
    public static let maxPackBytes: Int64 = 200 << 20

    public struct Entry: Codable, Equatable, Hashable, Sendable, Identifiable {
        public var id: String
        public var packVersion: Int
        public var name: CharacterNames
        public var vendors: [String]
        public var accent: String
        public var artist: CharacterArtist?
        public var minApp: String?
        public var size: Int64
        public var sha256: String
        public var url: URL
        public var preview: URL?

        public init(id: String, packVersion: Int, name: CharacterNames, vendors: [String], accent: String,
                    artist: CharacterArtist? = nil, minApp: String? = nil, size: Int64, sha256: String, url: URL,
                    preview: URL? = nil) {
            self.id = id
            self.packVersion = packVersion
            self.name = name
            self.vendors = vendors
            self.accent = accent
            self.artist = artist
            self.minApp = minApp
            self.size = size
            self.sha256 = sha256
            self.url = url
            self.preview = preview
        }

        public var isValid: Bool {
            CharacterID.isValid(id) && packVersion >= 1 && name.isComplete && CharacterManifest.isColor(accent)
                && (1...CharacterCatalog.maxPackBytes).contains(size) && sha256.count == 64
                && sha256.allSatisfy(\.isHexDigit) && CharacterCatalog.permits(url) && preview.map(CharacterCatalog.permits) != false
        }
    }

    public var format: String
    public var version: Int
    public var characters: [Entry]

    public init(characters: [Entry]) {
        format = Self.format
        version = Self.version
        self.characters = characters
    }

    /// Decodes and keeps only valid entries (unique ids, first wins); throws for another format or version.
    public static func load(_ data: Data) throws -> CharacterCatalog {
        guard data.count <= maxCatalogBytes else { throw CharacterPackError.tooLarge }
        var catalog = try JSONDecoder().decode(CharacterCatalog.self, from: data)
        guard catalog.format == format, catalog.version == version else { throw CharacterPackError.format }
        var seen = Set<String>()
        catalog.characters = catalog.characters.filter { $0.isValid && seen.insert($0.id).inserted }
        return catalog
    }

    /// HTTPS on the release hosts, default port, no user information.
    public static func permits(_ url: URL?) -> Bool {
        guard let url, url.scheme?.lowercased() == "https", let host = url.host(percentEncoded: false)?.lowercased(),
              allowedHosts.contains(host), url.user(percentEncoded: false) == nil else { return false }
        return url.port.map { $0 == 443 } ?? true
    }
}

/// What a pack archive may contain (CHARACTERS 1): safe relative paths, no links, a bounded size, a manifest at the top.
public enum CharacterPackLayout {
    public static let maxEntries = 4_096
    public static let maxExpandedSize: UInt64 = 400 << 20

    public static func isSafe(_ path: String) -> Bool { ArchiveLayout.isSafe(path) }

    public static func validate(_ entries: [ZipEntry]) throws {
        guard !entries.isEmpty, entries.count <= maxEntries else { throw CharacterPackError.tooLarge }
        var total: UInt64 = 0
        for entry in entries {
            guard isSafe(entry.path) else { throw CharacterPackError.unsafePath(entry.path) }
            guard !entry.isSymbolicLink else { throw CharacterPackError.symbolicLink(entry.path) }
            let (sum, overflow) = total.addingReportingOverflow(entry.uncompressedSize)
            guard !overflow, sum <= maxExpandedSize else { throw CharacterPackError.tooLarge }
            total = sum
        }
        guard entries.contains(where: { $0.path == "manifest.json" }) else { throw CharacterPackError.missingFile("manifest.json") }
    }
}

/// Live output per model vendor and the fastest model of a set of agents (CHARACTERS 7).
public struct VendorReading: Equatable, Sendable {
    /// Summed live output tok/s per vendor (`providers` keys of the price table).
    public var rates: [String: Double]
    /// The fastest generating agent's model: its price-table name (else the raw id) and vendor.
    public var fastestModel: String?
    public var fastestVendor: String?

    public init(rates: [String: Double] = [:], fastestModel: String? = nil, fastestVendor: String? = nil) {
        self.rates = rates
        self.fastestModel = fastestModel
        self.fastestVendor = fastestVendor
    }

    public init(agents: [AgentStatus], prices: PriceTable?) {
        var rates: [String: Double] = [:]
        var best: (rate: Double, name: String?, vendor: String?)?
        for agent in agents {
            let rate = agent.live.output
            guard rate > 0, rate.isFinite else { continue }
            let (vendor, name) = CharacterVendors.resolve(model: agent.model, source: agent.source, prices: prices)
            if let vendor { rates[vendor, default: 0] += rate }
            if best == nil || rate > best!.rate { best = (rate, name, vendor) }
        }
        self.init(rates: rates, fastestModel: best?.name, fastestVendor: best?.vendor)
    }
}

/// The model vendor a source stands for when its model id does not resolve to a priced vendor (CHARACTERS 7).
public enum CharacterVendors {
    /// Resellers whose models belong to another vendor's character; the source decides instead.
    static let resellers: Set<String> = ["opencode"]

    /// Model families the price table does not name a vendor for, by the start of the id's last path segment.
    static let families: [(prefix: String, vendor: String)] = [
        ("doubao", "bytedance"), ("seed-", "bytedance"), ("ui-tars", "bytedance"), ("llama", "meta"),
        ("sonar", "perplexity"), ("pplx", "perplexity"), ("r1-1776", "perplexity"), ("ernie", "baidu"),
        ("hunyuan", "tencent"), ("rwkv", "rwkv"),
    ]

    /// The vendor and display name of a model: the price table's provider and name when the id resolves, else the
    /// vendor of its model family, else the source's vendor, with the raw id.
    public static func resolve(model: String?, source: SourceKind, prices: PriceTable?) -> (vendor: String?, name: String?) {
        if let prices, case .priced(let index, _) = prices.resolver.resolve(model: model, provider: nil),
           prices.models.indices.contains(index) {
            let entry = prices.models[index]
            let vendor = resellers.contains(entry.provider) ? family(of: model) ?? fallback(for: source) : entry.provider
            return (vendor, entry.name)
        }
        let raw = model?.trimmingCharacters(in: .whitespacesAndNewlines)
        return (family(of: raw) ?? fallback(for: source), raw?.isEmpty == false ? raw : nil)
    }

    /// `meta-llama/Llama-3.3-70B` → `meta`, `doubao-seed-1.6` → `bytedance`; nil for any other id.
    public static func family(of model: String?) -> String? {
        guard let model, let last = model.split(separator: "/").last else { return nil }
        let id = last.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return families.first { id.hasPrefix($0.prefix) }?.vendor
    }

    public static func fallback(for source: SourceKind) -> String? {
        switch source {
        case .claudeCode, .claudeDesktop: "anthropic"
        case .codex: "openai"
        case .gemini, .antigravity: "google"
        case .deepseek: "deepseek"
        case .kimi: "moonshot"
        case .grokBuild: "xai"
        case .zcode: "zhipu"
        case .workbuddy, .workbuddyAi: "tencent"
        default: nil
        }
    }
}
