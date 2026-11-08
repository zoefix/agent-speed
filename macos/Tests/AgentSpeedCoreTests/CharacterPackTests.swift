import Foundation
import Testing
@testable import AgentSpeedCore

/// Builds unpacked character packs in a temporary folder (no art: a few bytes stand in for images).
struct PackFixture {
    let root: URL

    init() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("character-tests-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    static func manifest(id: String = "claude", packVersion: Int = 1, poses: [String: String] = ["idle": "poses/idle"],
                         minApp: String? = nil) -> CharacterManifest {
        CharacterManifest(id: id, packVersion: packVersion,
                          name: CharacterNames(zhHant: "Claude 娘", zhHans: "Claude 娘", en: "Claude-chan", ja: "Claudeちゃん"),
                          vendors: ["anthropic"], accent: "#D97757", artist: CharacterArtist(name: "ZipZipPipe"),
                          minApp: minApp, poses: poses)
    }

    /// `<root>/<folder>/` with manifest.json, preview.png and a rig.json per pose.
    @discardableResult
    func pack(_ manifest: CharacterManifest, folder: String? = nil) throws -> URL {
        let dir = root.appendingPathComponent(folder ?? manifest.id, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try JSONEncoder().encode(manifest).write(to: dir.appendingPathComponent("manifest.json"))
        try Data([0x89, 0x50, 0x4E, 0x47]).write(to: dir.appendingPathComponent("preview.png"))
        for path in Set(manifest.poses.values) {
            let pose = dir.appendingPathComponent(path, isDirectory: true)
            try FileManager.default.createDirectory(at: pose, withIntermediateDirectories: true)
            try Data(#"{"format":"agent-speed-character","version":1}"#.utf8).write(to: pose.appendingPathComponent("rig.json"))
        }
        return dir
    }

    func remove() {
        try? FileManager.default.removeItem(at: root)
    }
}

@Suite struct CharacterManifestTests {
    @Test func idsAreLowercaseLettersDigitsAndDashes() {
        #expect(CharacterID.isValid("claude"))
        #expect(CharacterID.isValid("gpt-5"))
        #expect(!CharacterID.isValid("Claude"))
        #expect(!CharacterID.isValid("-x"))
        #expect(!CharacterID.isValid("../x"))
        #expect(!CharacterID.isValid(""))
        #expect(!CharacterID.isValid(String(repeating: "a", count: 65)))
    }

    @Test func aValidManifestPasses() throws {
        try PackFixture.manifest().validate(appVersion: "1.7.0")
    }

    @Test func badManifestsAreRefused() {
        var manifest = PackFixture.manifest()
        manifest.format = "other"
        #expect(throws: CharacterPackError.format) { try manifest.validate(appVersion: nil) }
        #expect(throws: CharacterPackError.missingIdle) {
            try PackFixture.manifest(poses: ["boost": "poses/boost"]).validate(appVersion: nil)
        }
        #expect(throws: CharacterPackError.unsafePath("../poses")) {
            try PackFixture.manifest(poses: ["idle": "../poses"]).validate(appVersion: nil)
        }
        #expect(throws: CharacterPackError.newerApp("9.0.0")) {
            try PackFixture.manifest(minApp: "9.0.0").validate(appVersion: "1.7.0")
        }
        try? PackFixture.manifest(minApp: "9.0.0").validate(appVersion: "0.0.0")
    }

    @Test func aMissingBandUsesTheNearestLowerOne() {
        let manifest = PackFixture.manifest(poses: ["idle": "poses/idle", "boost": "poses/boost"])
        #expect(manifest.pose(for: .idle) == "poses/idle")
        #expect(manifest.pose(for: .cruise) == "poses/idle")
        #expect(manifest.pose(for: .boost) == "poses/boost")
        #expect(manifest.pose(for: .redline) == "poses/boost")
    }

    @Test func accentsParse() {
        let rgb = CharacterManifest.rgb("#D97757")
        #expect(rgb != nil && abs(rgb!.r - 217.0 / 255) < 1e-9)
        #expect(CharacterManifest.rgb("D97757") == nil)
    }
}

@Suite struct CharacterCatalogTests {
    static func entry(id: String = "claude", url: String = "https://github.com/zoefix/agent-speed/releases/download/characters/claude-1.zip",
                      sha: String = String(repeating: "a", count: 64)) -> String {
        ##"{"id":"\##(id)","packVersion":1,"name":{"zh-Hant":"a","zh-Hans":"a","en":"a","ja":"a"},"vendors":["anthropic"],"accent":"#D97757","size":10,"sha256":"\##(sha)","url":"\##(url)"}"##
    }

    @Test func invalidAndDuplicateEntriesAreDropped() throws {
        let json = #"{"format":"agent-speed-character-catalog","version":1,"characters":["#
            + [Self.entry(), Self.entry(), Self.entry(id: "evil", url: "https://example.com/x.zip"),
               Self.entry(id: "short", sha: "abc"), Self.entry(id: "gpt")].joined(separator: ",") + "]}"
        let catalog = try CharacterCatalog.load(Data(json.utf8))
        #expect(catalog.characters.map(\.id) == ["claude", "gpt"])
    }

    @Test func otherFormatsAreRefused() {
        #expect(throws: CharacterPackError.format) {
            try CharacterCatalog.load(Data(#"{"format":"x","version":1,"characters":[]}"#.utf8))
        }
    }

    @Test func onlyReleaseHostsOverHTTPS() {
        #expect(CharacterCatalog.permits(URL(string: "https://github.com/a")))
        #expect(CharacterCatalog.permits(URL(string: "https://objects.githubusercontent.com/a")))
        #expect(!CharacterCatalog.permits(URL(string: "http://github.com/a")))
        #expect(!CharacterCatalog.permits(URL(string: "https://raw.githubusercontent.com/a")))
        #expect(!CharacterCatalog.permits(URL(string: "https://user@github.com/a")))
        #expect(!CharacterCatalog.permits(URL(string: "https://github.com:8443/a")))
    }

    @Test func archiveEntriesMustBeSafeAndHoldAManifest() throws {
        try CharacterPackLayout.validate([ZipEntry(path: "manifest.json"), ZipEntry(path: "poses/idle/rig.json")])
        #expect(throws: CharacterPackError.unsafePath("../x")) {
            try CharacterPackLayout.validate([ZipEntry(path: "manifest.json"), ZipEntry(path: "../x")])
        }
        #expect(throws: CharacterPackError.symbolicLink("link")) {
            try CharacterPackLayout.validate([ZipEntry(path: "manifest.json"), ZipEntry(path: "link", isSymbolicLink: true)])
        }
        #expect(throws: CharacterPackError.missingFile("manifest.json")) {
            try CharacterPackLayout.validate([ZipEntry(path: "poses/idle/rig.json")])
        }
    }
}

@Suite struct CharacterStoreTests {
    @Test func installListsAndRemoves() throws {
        let fixture = try PackFixture()
        defer { fixture.remove() }
        let store = CharacterStore(root: fixture.root.appendingPathComponent("Characters"), appVersion: "1.7.0")
        #expect(store.installed().isEmpty)
        let unpacked = try fixture.pack(PackFixture.manifest(), folder: "incoming")
        let installed = try store.install(from: unpacked, expectedID: "claude")
        #expect(installed.folder.lastPathComponent == "claude")
        #expect(store.installed().map(\.id) == ["claude"])
        #expect(!FileManager.default.fileExists(atPath: unpacked.path))

        let newer = try fixture.pack(PackFixture.manifest(packVersion: 2), folder: "incoming2")
        try store.install(from: newer, expectedID: "claude")
        #expect(store.installed("claude")?.manifest.packVersion == 2)

        try store.remove("claude")
        #expect(store.installed().isEmpty)
    }

    @Test func aPackWithAnotherIDIsRefused() throws {
        let fixture = try PackFixture()
        defer { fixture.remove() }
        let store = CharacterStore(root: fixture.root.appendingPathComponent("Characters"), appVersion: nil)
        let unpacked = try fixture.pack(PackFixture.manifest(id: "gpt"), folder: "incoming")
        #expect(throws: CharacterPackError.mismatch) { try store.install(from: unpacked, expectedID: "claude") }
    }

    @Test func linksInsideAPackAreRefused() throws {
        let fixture = try PackFixture()
        defer { fixture.remove() }
        let store = CharacterStore(root: fixture.root.appendingPathComponent("Characters"), appVersion: nil)
        let unpacked = try fixture.pack(PackFixture.manifest(), folder: "incoming")
        try FileManager.default.createSymbolicLink(atPath: unpacked.appendingPathComponent("escape").path, withDestinationPath: "/etc")
        #expect(throws: CharacterPackError.symbolicLink("escape")) { try store.install(from: unpacked, expectedID: "claude") }
    }

    @Test func developmentPacksAreListedFirstAndHideInstalledOnes() throws {
        let fixture = try PackFixture()
        defer { fixture.remove() }
        let dev = fixture.root.appendingPathComponent("dev")
        let installedRoot = fixture.root.appendingPathComponent("Characters")
        let devFixture = PackFixture(root: dev)
        try devFixture.pack(PackFixture.manifest(id: "claude", packVersion: 9))
        try PackFixture(root: installedRoot).pack(PackFixture.manifest(id: "claude"))
        try PackFixture(root: installedRoot).pack(PackFixture.manifest(id: "gpt"))
        try PackFixture(root: installedRoot).pack(PackFixture.manifest(id: "broken", poses: ["idle": "missing"]), folder: "broken")
        try FileManager.default.removeItem(at: installedRoot.appendingPathComponent("broken/missing"))
        let store = CharacterStore.standard(appData: fixture.root, appVersion: nil,
                                            environment: [CharacterStore.developmentVariable: dev.path])
        let packs = store.installed()
        #expect(packs.map(\.id) == ["claude", "gpt"])
        #expect(packs[0].isDevelopment && packs[0].manifest.packVersion == 9)
    }

    @Test func aFolderNamedDifferentlyFromItsIDIsIgnored() throws {
        let fixture = try PackFixture()
        defer { fixture.remove() }
        try fixture.pack(PackFixture.manifest(id: "gpt"), folder: "claude")
        #expect(CharacterStore(root: fixture.root, appVersion: nil).installed().isEmpty)
    }
}

extension PackFixture {
    init(root: URL) {
        self.root = root
        try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }
}

@Suite struct VendorReadingTests {
    func agent(_ source: SourceKind, _ model: String?, _ rate: Double) -> AgentStatus {
        AgentStatus(id: UUID().uuidString, source: source, project: "p", model: model, phase: .generating, isSubagent: false,
                    live: Rate(output: rate, total: rate), recentSpeed: nil, lastActivity: Date(timeIntervalSince1970: 0))
    }

    @Test func ratesSumPerVendorAndTheFastestModelIsNamed() {
        let prices = PriceBook.bundled
        let reading = VendorReading(agents: [
            agent(.claudeCode, "claude-opus-4-5", 40), agent(.claudeCode, "claude-sonnet-4-5", 30),
            agent(.codex, "gpt-5.1-codex", 55), agent(.omp, nil, 9),
        ], prices: prices)
        #expect(reading.rates["anthropic"] == 70)
        #expect(reading.rates["openai"] == 55)
        #expect(reading.rates.count == 2)
        #expect(reading.fastestVendor == "openai")
        #expect(reading.fastestModel != nil && reading.fastestModel != "gpt-5.1-codex")
    }

    @Test func modelFamiliesNameTheirVendor() {
        let cases: [(String?, String?)] = [
            ("doubao-seed-1.6", "bytedance"), ("seed-oss-36b", "bytedance"), ("bytedance/UI-TARS-1.5-7B", "bytedance"),
            ("meta-llama/Llama-3.3-70B-Instruct", "meta"), ("llama3.2:latest", "meta"), ("sonar-pro", "perplexity"),
            ("pplx-70b-online", "perplexity"), ("perplexity/r1-1776", "perplexity"), ("ERNIE-4.5-Turbo", "baidu"),
            ("hunyuan-t1-latest", "tencent"), ("rwkv-7-world", "rwkv"), ("codellama-13b", nil), ("seedream", nil),
            ("jev", nil), (nil, nil), ("", nil),
        ]
        for (model, vendor) in cases {
            #expect(CharacterVendors.family(of: model) == vendor, "\(model ?? "nil")")
        }
        let reading = VendorReading(agents: [agent(.omp, "meta-llama/llama-4-maverick", 20), agent(.gemini, "ernie-x1", 8)],
                                    prices: nil)
        #expect(reading.rates == ["meta": 20, "baidu": 8])
        #expect(reading.fastestVendor == "meta")
    }

    /// Every roster character's spec names the vendor its models resolve to, so Auto picks her.
    @Test func everyRosterCharacterIsPickedForItsModels() throws {
        let specs = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("characters/specs")
        var vendorsOf: [String: [String]] = [:]
        for file in try FileManager.default.contentsOfDirectory(at: specs, includingPropertiesForKeys: nil)
        where file.pathExtension == "json" {
            let doc = try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as? [String: Any])
            vendorsOf[try #require(doc["id"] as? String)] = doc["vendors"] as? [String] ?? []
        }
        let cases: [(String, String)] = [
            ("claude-opus-4-5", "claude"), ("gpt-5.1-codex", "gpt"), ("grok-4", "grok"), ("gemini-2.5-pro", "gemini"),
            ("deepseek-chat", "deepseek"), ("kimi-k2-thinking", "kimi"), ("glm-4.6", "glm"),
            ("qwen3-coder-plus", "qwen"), ("doubao-seed-1.6", "seed"), ("MiniMax-M2", "minimax"),
            ("meta-llama/llama-4-maverick", "llama"), ("mistral-large-latest", "mistral"), ("sonar-pro", "perplexity"),
            ("ernie-4.5-turbo", "wenxin"), ("hunyuan-t1-latest", "hunyuan"), ("rwkv-7-world", "rwkv"),
        ]
        for (model, id) in cases {
            let vendor = VendorReading(agents: [agent(.omp, model, 30)], prices: PriceBook.bundled).fastestVendor
            let standing = vendorsOf.filter { $0.value.contains(vendor ?? "") }.map(\.key)
            #expect(standing == [id], "\(model) -> \(vendor ?? "nil") -> \(standing)")
        }
        #expect(vendorsOf["jev"] == [], "JEV stands for no model; she is shown only when locked")
    }

    @Test func unknownModelsFallBackToTheSourcesVendor() {
        let reading = VendorReading(agents: [agent(.gemini, "some-future-model", 12), agent(.kimi, nil, 0)], prices: nil)
        #expect(reading.rates == ["google": 12])
        #expect(reading.fastestModel == "some-future-model")
        #expect(reading.fastestVendor == "google")
    }
}
