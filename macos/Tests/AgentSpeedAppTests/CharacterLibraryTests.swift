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

    func entry(id: String = "claude", sha: String? = nil) -> CharacterCatalog.Entry {
        CharacterCatalog.Entry(id: id, packVersion: 1, name: CharacterNames(zhHant: "角色", zhHans: "角色", en: "Char", ja: "キャラ"),
                               vendors: ["anthropic"], accent: "#D97757", size: size, sha256: sha ?? sha256,
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
}
