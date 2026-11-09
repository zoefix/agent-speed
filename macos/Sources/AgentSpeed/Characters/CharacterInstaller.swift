import AgentSpeedCore
import Foundation

/// Fetches a pack's bytes; the app streams them with the update transport, tests copy a local file.
protocol CharacterPackFetching: Sendable {
    func fetch(_ url: URL, to destination: URL, expectedSize: Int64, progress: @escaping @Sendable (Double) -> Void) async throws
}

/// The update transport (UPDATES 1, 6): GitHub hosts only on every hop, an exact size, no cookies or cache.
struct UpdateHTTPPackFetcher: CharacterPackFetching {
    let userAgentVersion: String

    func fetch(_ url: URL, to destination: URL, expectedSize: Int64, progress: @escaping @Sendable (Double) -> Void) async throws {
        guard CharacterCatalog.permits(url) else { throw CharacterInstallError.disallowedURL }
        let http = UpdateHTTP(configuration: UpdateConfiguration(feedURL: url, policy: .standard, currentVersion: userAgentVersion))
        try await http.download(http.assetRequest(url), to: destination, expectedSize: expectedSize, progress: progress)
    }
}

enum CharacterInstallError: Error, Equatable {
    case disallowedURL
    case checksum
    case invalidPack
    case unzip
    case cancelled
}

/// Downloads, verifies and installs one pack (CHARACTERS 2): the exact size and SHA-256 of the catalog entry, safe
/// archive entries, `ditto` into a private temporary folder, a valid manifest with the entry's id, then an atomic move
/// into the store. Nothing is left behind on failure.
struct CharacterInstaller: Sendable {
    let store: CharacterStore
    let fetcher: CharacterPackFetching

    func install(_ entry: CharacterCatalog.Entry, progress: @escaping @Sendable (Double) -> Void) async throws -> InstalledCharacter {
        guard entry.isValid else { throw CharacterInstallError.invalidPack }
        let workspace = try UpdateWorkspace.create()
        defer { workspace.remove() }
        let archive = workspace.file("pack.zip")
        try await fetcher.fetch(entry.url, to: archive, expectedSize: entry.size, progress: progress)
        try Task.checkCancellation()
        let digest = try FileDigest.sha256(of: archive)
        guard ChecksumList.matches(entry.sha256, digest) else { throw CharacterInstallError.checksum }
        do {
            try CharacterPackLayout.validate(ZipDirectory.entries(of: archive))
        } catch {
            throw CharacterInstallError.invalidPack
        }
        let unpacked = try workspace.folder("pack")
        let status = try await ProcessRunner.run("/usr/bin/ditto", ["-x", "-k", "--noqtn", archive.path, unpacked.path])
        guard status == 0 else { throw CharacterInstallError.unzip }
        try Task.checkCancellation()
        return try store.install(from: unpacked, expectedID: entry.id)
    }
}
