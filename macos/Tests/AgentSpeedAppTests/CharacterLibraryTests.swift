import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation
import Testing
@testable import AgentSpeed

/// Copies a local archive instead of downloading it.
private struct LocalFetcher: CharacterPackFetching {
    let archive: URL

    func fetch(_ url: URL, to destination: URL, expectedSize: Int64, progress: @escaping @Sendable (Double) -> Void) async throws {
        try FileManager.default.copyItem(at: archive, to: destination)
        progress(1)
    }
}

/// Copies the archive of the pack each URL names, recording the order of the requests.
private final class MapFetcher: CharacterPackFetching, @unchecked Sendable {
    let archives: [String: URL]
    let delay: Duration
    private let lock = NSLock()
    private var log: [String] = []
    private var running = 0
    private(set) var mostAtOnce = 0

    init(_ archives: [String: URL], delay: Duration = .milliseconds(20)) {
        self.archives = archives
        self.delay = delay
    }

    var order: [String] { lock.withLock { log } }

    func fetch(_ url: URL, to destination: URL, expectedSize: Int64, progress: @escaping @Sendable (Double) -> Void) async throws {
        let id = String(url.lastPathComponent.split(separator: "-")[0])
        lock.withLock {
            log.append(id)
            running += 1
            mostAtOnce = max(mostAtOnce, running)
        }
        defer { lock.withLock { running -= 1 } }
        try await Task.sleep(for: delay)
        try FileManager.default.copyItem(at: archives[id]!, to: destination)
        progress(1)
    }
}

/// Answers every catalog request with one response.
private struct FixedTransport: HTTPTransport {
    let response: HTTPFetchResponse

    func get(_ request: HTTPFetchRequest) async throws -> HTTPFetchResponse { response }
}

/// A tiny pack (no art) zipped with `ditto`, its size and SHA-256.
private struct ZippedPack {
    let root: URL
    let archive: URL
    let size: Int64
    let sha256: String

    init(id: String = "claude") async throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("character-app-tests-" + UUID().uuidString)
        let pack = root.appendingPathComponent("pack", isDirectory: true)
        let pose = pack.appendingPathComponent("poses/idle", isDirectory: true)
        try FileManager.default.createDirectory(at: pose, withIntermediateDirectories: true)
        let manifest = CharacterManifest(id: id, packVersion: 1,
                                         name: CharacterNames(zhHant: "角色", zhHans: "角色", en: "Char", ja: "キャラ"),
                                         vendors: ["anthropic"], accent: "#D97757", poses: ["idle": "poses/idle"])
        try JSONEncoder().encode(manifest).write(to: pack.appendingPathComponent("manifest.json"))
        try Data([1, 2, 3]).write(to: pack.appendingPathComponent("preview.png"))
        try Data("{}".utf8).write(to: pose.appendingPathComponent("rig.json"))
        archive = root.appendingPathComponent("pack.zip")
        let status = try await ProcessRunner.run("/usr/bin/ditto", ["-c", "-k", pack.path, archive.path])
        #expect(status == 0)
        let data = try Data(contentsOf: archive)
        size = Int64(data.count)
        sha256 = FileDigest.sha256(of: data)
    }

    func entry(id: String = "claude", sha: String? = nil, vendors: [String] = ["anthropic"]) -> CharacterCatalog.Entry {
        CharacterCatalog.Entry(id: id, packVersion: 1, name: CharacterNames(zhHant: "角色", zhHans: "角色", en: "Char", ja: "キャラ"),
                               vendors: vendors, accent: "#D97757", size: size, sha256: sha ?? sha256,
                               url: URL(string: "https://github.com/zoefix/agent-speed/releases/download/characters/\(id)-1.zip")!)
    }

    func remove() {
        try? FileManager.default.removeItem(at: root)
    }
}

@Suite struct CharacterInstallerTests {
    @Test func aVerifiedPackIsInstalled() async throws {
        let zipped = try await ZippedPack()
        defer { zipped.remove() }
        let store = CharacterStore(root: zipped.root.appendingPathComponent("Characters"), appVersion: nil)
        let installer = CharacterInstaller(store: store, fetcher: LocalFetcher(archive: zipped.archive))
        let pack = try await installer.install(zipped.entry()) { _ in }
        #expect(pack.id == "claude")
        #expect(store.installed().map(\.id) == ["claude"])
    }

    @Test func aChecksumMismatchInstallsNothing() async throws {
        let zipped = try await ZippedPack()
        defer { zipped.remove() }
        let store = CharacterStore(root: zipped.root.appendingPathComponent("Characters"), appVersion: nil)
        let installer = CharacterInstaller(store: store, fetcher: LocalFetcher(archive: zipped.archive))
        await #expect(throws: CharacterInstallError.checksum) {
            _ = try await installer.install(zipped.entry(sha: String(repeating: "0", count: 64))) { _ in }
        }
        #expect(store.installed().isEmpty)
    }

    @Test func aPackUnderAnotherIDIsRefused() async throws {
        let zipped = try await ZippedPack(id: "gpt")
        defer { zipped.remove() }
        let store = CharacterStore(root: zipped.root.appendingPathComponent("Characters"), appVersion: nil)
        let installer = CharacterInstaller(store: store, fetcher: LocalFetcher(archive: zipped.archive))
        await #expect(throws: CharacterPackError.mismatch) {
            _ = try await installer.install(zipped.entry(id: "claude")) { _ in }
        }
        #expect(store.installed().isEmpty)
    }
}

@MainActor
@Suite struct CharacterLibraryTests {
    @Test func theCatalogLoadsAndDownloadsInstall() async throws {
        let zipped = try await ZippedPack()
        defer { zipped.remove() }
        let catalog = CharacterCatalog(characters: [zipped.entry()])
        let transport = FixedTransport(response: HTTPFetchResponse(status: 200, body: try JSONEncoder().encode(catalog)))
        let library = CharacterLibrary(store: CharacterStore(root: zipped.root.appendingPathComponent("Characters"), appVersion: nil),
                                       transport: transport, fetcher: LocalFetcher(archive: zipped.archive), appVersion: "1.7.0")
        library.refreshForList()
        for _ in 0..<200 where library.catalogState == .loading { try await Task.sleep(for: .milliseconds(10)) }
        #expect(library.catalog?.characters.map(\.id) == ["claude"])

        library.download(zipped.entry())
        for _ in 0..<500 where library.installed.isEmpty { try await Task.sleep(for: .milliseconds(10)) }
        #expect(library.installed.map(\.id) == ["claude"])
        #expect(library.packs == [CharacterSelection.Pack(id: "claude", vendors: ["anthropic"])])
        #expect(library.errors.isEmpty && library.downloads.isEmpty)
        #expect(!library.hasUpdate("claude"))

        library.remove("claude")
        #expect(library.installed.isEmpty)
    }

    @Test func nothingIsFetchedWhileNetworkFeaturesAreOff() {
        let library = CharacterLibrary(store: CharacterStore(root: URL(fileURLWithPath: "/nonexistent-characters"), appVersion: nil),
                                       transport: FixedTransport(response: HTTPFetchResponse(status: 500)),
                                       fetcher: LocalFetcher(archive: URL(fileURLWithPath: "/nonexistent")))
        library.networkEnabled = { false }
        library.refreshForList()
        #expect(library.catalogState == .idle)
    }

    @Test func downloadErrorsMapToMessages() {
        #expect(CharacterLibrary.errorKey(CharacterInstallError.checksum) == "characters.error.checksum")
        #expect(CharacterLibrary.errorKey(CharacterPackError.newerApp("9.0.0")) == "characters.error.newerApp")
        #expect(CharacterLibrary.errorKey(CancellationError()) == nil)
        #expect(CharacterLibrary.errorKey(URLError(.timedOut)) == "characters.error.network")
    }

    /// Three packs, a library over a fresh defaults suite with their catalog loaded.
    fileprivate static func run(fetchDelay: Duration = .milliseconds(20)) async throws
        -> (library: CharacterLibrary, fetcher: MapFetcher, packs: [ZippedPack], defaults: UserDefaults) {
        let ids = ["claude", "gpt", "kimi"]
        let vendors = ["claude": ["anthropic"], "gpt": ["openai"], "kimi": ["moonshot"]]
        var packs: [ZippedPack] = []
        for id in ids { packs.append(try await ZippedPack(id: id)) }
        let catalog = CharacterCatalog(characters: zip(ids, packs).map { $1.entry(id: $0, vendors: vendors[$0]!) })
        let fetcher = MapFetcher(Dictionary(uniqueKeysWithValues: zip(ids, packs.map(\.archive))), delay: fetchDelay)
        let suite = "character-auto-" + UUID().uuidString
        let defaults = try #require(UserDefaults(suiteName: suite))
        let library = CharacterLibrary(store: CharacterStore(root: packs[0].root.appendingPathComponent("Characters"), appVersion: nil),
                                       transport: FixedTransport(response: HTTPFetchResponse(status: 200, body: try JSONEncoder().encode(catalog))),
                                       fetcher: fetcher, appVersion: "1.7.0", defaults: defaults)
        return (library, fetcher, packs, defaults)
    }

    static func settle(_ library: CharacterLibrary) async throws {
        for _ in 0..<1000 where library.autoTotal > 0 || library.catalogState == .loading || !library.downloads.isEmpty {
            try await Task.sleep(for: .milliseconds(10))
        }
    }

    @Test func switchingDownloadsEveryPackOneAtATimeLiveVendorFirst() async throws {
        let (library, fetcher, packs, _) = try await Self.run()
        defer { packs.forEach { $0.remove() } }
        library.downloadAll(preferring: ["moonshot"])
        try await Self.settle(library)
        #expect(fetcher.order == ["kimi", "claude", "gpt"])
        #expect(fetcher.mostAtOnce == 1)
        #expect(Set(library.installed.map(\.id)) == ["claude", "gpt", "kimi"])
        #expect(library.autoTotal == 0 && library.autoQueue.isEmpty && library.autoCurrent == nil)
    }

    @Test func removedPacksStayAsideUntilDownloadedByHand() async throws {
        let (library, fetcher, packs, defaults) = try await Self.run()
        defer { packs.forEach { $0.remove() } }
        library.downloadAll(preferring: [])
        try await Self.settle(library)
        library.remove("gpt")
        #expect(defaults.stringArray(forKey: CharacterAutoDownload.setAsideKey) == ["gpt"])
        library.downloadAll(preferring: [])
        try await Self.settle(library)
        #expect(library.installed("gpt") == nil && fetcher.order.count == 3)
        // A new library over the same settings remembers it.
        let again = CharacterLibrary(store: library.store, transport: FixedTransport(response: HTTPFetchResponse(status: 500)),
                                     fetcher: fetcher, appVersion: "1.7.0", defaults: defaults)
        #expect(again.setAside == ["gpt"])
        library.download(try #require(library.catalog?.characters.first { $0.id == "gpt" }))
        try await Self.settle(library)
        #expect(library.installed("gpt") != nil && library.setAside.isEmpty)
    }

    @Test func stopSetsAsideWhatIsLeft() async throws {
        // The first download stays in flight until Stop cancels it, so the run is always stopped mid-download.
        let (library, fetcher, packs, _) = try await Self.run(fetchDelay: .seconds(30))
        defer { packs.forEach { $0.remove() } }
        library.downloadAll(preferring: [])
        for _ in 0..<500 where library.autoCurrent == nil || fetcher.order.isEmpty { try await Task.sleep(for: .milliseconds(2)) }
        library.stopAutomatic()
        try await Self.settle(library)
        #expect(library.setAside == ["claude", "gpt", "kimi"].filter { library.installed($0) == nil }.reduce(into: Set<String>()) { $0.insert($1) })
        #expect(library.installed.count <= 1 && fetcher.order.count == 1)
    }

    @Test func nothingRunsWhileNetworkFeaturesAreOff() async throws {
        let (library, fetcher, packs, _) = try await Self.run()
        defer { packs.forEach { $0.remove() } }
        library.networkEnabled = { false }
        library.downloadAll(preferring: [])
        try await Self.settle(library)
        #expect(fetcher.order.isEmpty && library.catalogState == .idle)
    }
}
