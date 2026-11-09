import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation
import Observation
import os

/// What the orb runtimes need to choose a character (CHARACTERS 7).
struct OrbCharacterInputs {
    var prices: PriceTable?
    var installed: [CharacterSelection.Pack]
}

/// The character packs (CHARACTERS 2): what is installed, the catalog of the `characters` release, and downloads the
/// user starts. The catalog is fetched when Settings shows the list (at most every 10 minutes) and at most once a day
/// otherwise, only while network features are on, and its failures stay quiet; download failures are shown.
@MainActor
@Observable
final class CharacterLibrary {
    enum CatalogState: Equatable {
        case idle, loading, loaded, failed
    }

    struct Download: Equatable {
        var progress: Double
        var installing = false
    }

    static let listRefresh: TimeInterval = 10 * 60
    static let backgroundRefresh: TimeInterval = 24 * 60 * 60

    private(set) var installed: [InstalledCharacter] = []
    private(set) var catalog: CharacterCatalog?
    private(set) var catalogState = CatalogState.idle
    private(set) var downloads: [String: Download] = [:]
    /// Localization keys of the last failed download per character.
    private(set) var errors: [String: String] = [:]

    @ObservationIgnored let store: CharacterStore
    @ObservationIgnored private let transport: HTTPTransport
    @ObservationIgnored private let fetcher: CharacterPackFetching
    @ObservationIgnored private let userAgent: String
    @ObservationIgnored private var etag: String?
    @ObservationIgnored private var fetchedAt: Date?
    @ObservationIgnored private var tasks: [String: Task<Void, Never>] = [:]
    @ObservationIgnored var networkEnabled: () -> Bool = { true }
    @ObservationIgnored var catalogURL = CharacterCatalog.url

    init(store: CharacterStore, transport: HTTPTransport = URLSessionTransport(),
         fetcher: CharacterPackFetching? = nil, appVersion: String = UpdateConfiguration.bundleVersion) {
        self.store = store
        self.transport = transport
        self.fetcher = fetcher ?? UpdateHTTPPackFetcher(userAgentVersion: appVersion)
        userAgent = "AgentSpeed/\(appVersion) (macOS)"
        reloadInstalled()
    }

    static func standard() -> CharacterLibrary {
        CharacterLibrary(store: .standard(appData: AppModel.appDataDirectory, appVersion: UpdateConfiguration.bundleVersion))
    }

    // MARK: Installed

    func reloadInstalled() {
        let store = self.store
        let next = store.installed()
        if next != installed { installed = next }
    }

    func installed(_ id: String) -> InstalledCharacter? {
        installed.first { $0.id == id }
    }

    /// Installed packs in catalog order (the order Auto falls back on), then the rest by id.
    var packs: [CharacterSelection.Pack] {
        let order = Dictionary(uniqueKeysWithValues: (catalog?.characters ?? []).enumerated().map { ($1.id, $0) })
        return installed
            .sorted { (order[$0.id] ?? .max, $0.id) < (order[$1.id] ?? .max, $1.id) }
            .map { CharacterSelection.Pack(id: $0.id, vendors: $0.manifest.vendors) }
    }

    func inputs(prices: PriceTable?) -> OrbCharacterInputs {
        OrbCharacterInputs(prices: prices, installed: packs)
    }

    /// A newer pack version is in the catalog.
    func hasUpdate(_ id: String) -> Bool {
        guard let pack = installed(id), !pack.isDevelopment, let entry = catalog?.characters.first(where: { $0.id == id }) else {
            return false
        }
        return entry.packVersion > pack.manifest.packVersion
    }

    // MARK: Catalog

    /// Settings shows the list: refresh unless fetched in the last 10 minutes.
    func refreshForList() {
        refreshCatalog(maxAge: Self.listRefresh)
    }

    /// At launch and daily.
    func refreshInBackground() {
        refreshCatalog(maxAge: Self.backgroundRefresh)
    }

    func refreshCatalog(maxAge: TimeInterval) {
        guard networkEnabled(), catalogState != .loading else { return }
        if let fetchedAt, Date().timeIntervalSince(fetchedAt) < maxAge, catalog != nil { return }
        catalogState = .loading
        let request = HTTPFetchRequest(url: catalogURL, ifNoneMatch: catalog == nil ? nil : etag, userAgent: userAgent,
                                       maxBytes: CharacterCatalog.maxCatalogBytes, allowedHosts: CharacterCatalog.allowedHosts)
        let transport = self.transport
        let library = self
        Task {
            let result: Result<HTTPFetchResponse, Error>
            do { result = .success(try await transport.get(request)) } catch { result = .failure(error) }
            library.finishCatalog(result)
        }
    }

    private func finishCatalog(_ result: Result<HTTPFetchResponse, Error>) {
        switch result {
        case .success(let response) where response.status == 304 && catalog != nil:
            fetchedAt = Date()
            catalogState = .loaded
        case .success(let response) where response.status == 200:
            if let next = try? CharacterCatalog.load(response.body) {
                if next != catalog { catalog = next }
                etag = response.etag
                fetchedAt = Date()
                catalogState = .loaded
            } else {
                catalogState = .failed
            }
        case .success, .failure:
            catalogState = catalog == nil ? .failed : .loaded
            Self.logger.notice("Character catalog unavailable")
        }
    }

    // MARK: Downloads

    func download(_ entry: CharacterCatalog.Entry) {
        guard tasks[entry.id] == nil else { return }
        guard networkEnabled() else {
            errors[entry.id] = "characters.error.offline"
            return
        }
        errors[entry.id] = nil
        downloads[entry.id] = Download(progress: 0)
        let installer = CharacterInstaller(store: store, fetcher: fetcher)
        let id = entry.id
        let library = self
        let report: @Sendable (Double) -> Void = { fraction in
            Task { @MainActor in library.progress(id, fraction) }
        }
        tasks[id] = Task {
            do {
                _ = try await installer.install(entry, progress: report)
                library.finishDownload(id, error: nil)
            } catch {
                library.finishDownload(id, error: error)
            }
        }
    }

    func cancel(_ id: String) {
        tasks[id]?.cancel()
    }

    func remove(_ id: String) {
        guard let pack = installed(id), !pack.isDevelopment else { return }
        do {
            try store.remove(id)
            errors[id] = nil
        } catch {
            errors[id] = "characters.error.remove"
        }
        reloadInstalled()
    }

    private func progress(_ id: String, _ fraction: Double) {
        guard var download = downloads[id] else { return }
        download.progress = min(max(fraction, 0), 1)
        download.installing = fraction >= 1
        if downloads[id] != download { downloads[id] = download }
    }

    private func finishDownload(_ id: String, error: Error?) {
        tasks[id] = nil
        downloads[id] = nil
        if let error { errors[id] = Self.errorKey(error) }
        reloadInstalled()
    }

    static func errorKey(_ error: Error) -> String? {
        switch error {
        case is CancellationError, CharacterInstallError.cancelled: nil
        case let urlError as URLError where urlError.code == .cancelled: nil
        case CharacterInstallError.checksum: "characters.error.checksum"
        case CharacterInstallError.invalidPack, CharacterInstallError.unzip: "characters.error.invalid"
        case let pack as CharacterPackError:
            if case .newerApp = pack { "characters.error.newerApp" } else { "characters.error.invalid" }
        default: "characters.error.network"
        }
    }

    private static let logger = Logger(subsystem: UpdateConfiguration.bundleIdentifier, category: "characters")
}
