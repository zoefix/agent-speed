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

/// The character packs (CHARACTERS 2): what is installed, the catalog of the `characters` release, downloads the user
/// starts, and the automatic download of every pack the user has not set aside (run when an orb switches to the
/// `character` style and at launch while one uses it, one pack at a time). The catalog is fetched when Settings shows
/// the list (at most every 10 minutes), when a run needs it, and at most once a day otherwise, only while network
/// features are on, and its failures stay quiet; download failures are shown.
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
    /// The automatic run: packs still queued, the one downloading and how many the run began with.
    private(set) var autoQueue: [String] = []
    private(set) var autoCurrent: String?
    private(set) var autoTotal = 0
    /// Packs the user removed or cancelled: a run never downloads them (CHARACTERS 2, *Automatic download*).
    private(set) var setAside: Set<String>

    /// The run's position for its status line: the pack downloading now, counted from 1.
    var autoPosition: Int { autoTotal - autoQueue.count }

    @ObservationIgnored let store: CharacterStore
    @ObservationIgnored private let transport: HTTPTransport
    @ObservationIgnored private let fetcher: CharacterPackFetching
    @ObservationIgnored private let userAgent: String
    @ObservationIgnored private var etag: String?
    @ObservationIgnored private var fetchedAt: Date?
    @ObservationIgnored private var tasks: [String: Task<Void, Never>] = [:]
    @ObservationIgnored var networkEnabled: () -> Bool = { true }
    @ObservationIgnored var catalogURL = CharacterCatalog.url
    @ObservationIgnored private let defaults: UserDefaults?
    /// Packs that failed their checks in this run, and the vendors of a run waiting for the catalog.
    @ObservationIgnored private var skipped: Set<String> = []
    @ObservationIgnored private var pendingRun: [String]?

    init(store: CharacterStore, transport: HTTPTransport = URLSessionTransport(),
         fetcher: CharacterPackFetching? = nil, appVersion: String = UpdateConfiguration.bundleVersion,
         defaults: UserDefaults? = nil) {
        self.store = store
        self.transport = transport
        self.fetcher = fetcher ?? UpdateHTTPPackFetcher(userAgentVersion: appVersion)
        self.defaults = defaults
        userAgent = "AgentSpeed/\(appVersion) (macOS)"
        setAside = Set(defaults?.stringArray(forKey: CharacterAutoDownload.setAsideKey) ?? [])
        reloadInstalled()
    }

    static func standard(defaults: UserDefaults) -> CharacterLibrary {
        CharacterLibrary(store: .standard(appData: AppModel.appDataDirectory, appVersion: UpdateConfiguration.bundleVersion),
                         defaults: defaults)
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

    /// The accent of the character a model vendor stands for (the models card's dots, DESIGN 2a.6): an installed
    /// pack's, else the catalog's; nil when no character stands for it.
    func accent(vendor: String) -> RGBColor? {
        let hex = installed.first { $0.manifest.vendors.contains(vendor) }?.manifest.accent
            ?? catalog?.characters.first { $0.vendors.contains(vendor) }?.accent
        return hex.flatMap(CharacterManifest.rgb).map { RGBColor(red: $0.r, green: $0.g, blue: $0.b) }
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
        if let vendors = pendingRun {
            pendingRun = nil
            if catalog != nil { downloadAll(preferring: vendors) }
        }
    }

    // MARK: Automatic download

    /// Queues every pack that is neither installed, set aside, downloading nor queued — the packs of `vendors` first
    /// (fastest first), then catalog order — and downloads them one at a time. Waits for the catalog when it is not
    /// loaded yet; does nothing while network features are off.
    func downloadAll(preferring vendors: [String]) {
        guard networkEnabled() else { return }
        guard let catalog else {
            pendingRun = vendors
            refreshCatalog(maxAge: Self.listRefresh)
            return
        }
        let busy = Set(downloads.keys).union(autoQueue)
        let fresh = CharacterAutoDownload.queue(catalog, installed: Set(installed.map(\.id)), setAside: setAside,
                                                skipped: skipped.union(busy), vendors: vendors)
        guard !fresh.isEmpty else { return }
        if autoCurrent == nil, autoQueue.isEmpty {
            autoTotal = 0
            skipped = []
        }
        autoQueue += fresh
        autoTotal += fresh.count
        startNextAutomatic()
    }

    /// Stop: every pack still queued, and the one downloading, is set aside.
    func stopAutomatic() {
        let ids = autoQueue + (autoCurrent.map { [$0] } ?? [])
        autoQueue = []
        setAside(ids, true)
        if let current = autoCurrent { tasks[current]?.cancel() } else { autoTotal = 0 }
    }

    private func startNextAutomatic() {
        guard autoCurrent == nil else { return }
        while networkEnabled(), !autoQueue.isEmpty {
            let id = autoQueue.removeFirst()
            guard installed(id) == nil, !setAside.contains(id), tasks[id] == nil,
                  let entry = catalog?.characters.first(where: { $0.id == id }) else { continue }
            autoCurrent = id
            start(entry)
            return
        }
        autoQueue = []
        autoTotal = 0
    }

    private func setAside(_ ids: [String], _ aside: Bool) {
        var next = setAside
        for id in ids { if aside { next.insert(id) } else { next.remove(id) } }
        guard next != setAside else { return }
        setAside = next
        defaults?.set(next.sorted(), forKey: CharacterAutoDownload.setAsideKey)
    }

    // MARK: Downloads

    /// The user's Download or Update: also takes the pack back from the set-aside list.
    func download(_ entry: CharacterCatalog.Entry) {
        setAside([entry.id], false)
        start(entry)
    }

    private func start(_ entry: CharacterCatalog.Entry) {
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

    /// The user's cancel sets the pack aside, so a run does not fetch it again.
    func cancel(_ id: String) {
        setAside([id], true)
        tasks[id]?.cancel()
    }

    /// Removing sets the pack aside as well.
    func remove(_ id: String) {
        guard let pack = installed(id), !pack.isDevelopment else { return }
        setAside([id], true)
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
        let key = error.flatMap(Self.errorKey)
        if let key { errors[id] = key }
        reloadInstalled()
        guard id == autoCurrent else { return }
        autoCurrent = nil
        if key == "characters.error.network" {
            // The connection is down: end the run; the next launch or style switch starts it again.
            autoQueue = []
        } else if key != nil {
            skipped.insert(id)
        }
        startNextAutomatic()
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
