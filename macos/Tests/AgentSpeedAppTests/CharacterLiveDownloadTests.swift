import AgentSpeedCharacter
import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeed

/// `AGENT_SPEED_LIVE_CHARACTERS=1`: the app's own catalog fetch and download path against the published `characters`
/// release, into a temporary folder: the catalog loads, a pack downloads, passes its size and SHA-256 checks, unpacks
/// and loads with every pose.
@Suite("Character packs, live")
@MainActor
struct CharacterLiveDownloadTests {
    @Test(.enabled(if: ProcessInfo.processInfo.environment["AGENT_SPEED_LIVE_CHARACTERS"] == "1"))
    func downloadsAndInstallsFromTheRelease() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("character-live-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let library = CharacterLibrary(store: CharacterStore(root: root), appVersion: "1.6.3")
        library.refreshCatalog(maxAge: 0)
        for _ in 0..<300 where library.catalogState == .loading { try await Task.sleep(for: .milliseconds(100)) }
        let catalog = try #require(library.catalog, "catalog: \(library.catalogState)")
        #expect(catalog.characters.count == 17)
        let ids = (ProcessInfo.processInfo.environment["AGENT_SPEED_LIVE_IDS"] ?? "kimi").split(separator: ",").map(String.init)
        for id in ids {
            let entry = try #require(catalog.characters.first { $0.id == id })
            library.download(entry)
            for _ in 0..<1200 where library.downloads[id] != nil { try await Task.sleep(for: .milliseconds(100)) }
            #expect(library.errors[id] == nil, "\(id): \(library.errors[id] ?? "")")
            let installed = try #require(library.installed(id))
            let pack = try CharacterPack.load(from: installed.folder)
            #expect(pack.poses.count >= 3, "\(id) poses")
            print("live \(id): \(entry.size) bytes, \(pack.poses.count) poses, folder \(installed.folder.lastPathComponent)")
        }
    }
}
