import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import Observation

/// Something the board windows place (DESIGN 2c Data model).
enum BoardsChange {
    /// A new board, placed like a new orb next to the window it came from (`anchor`, screen points).
    case created(id: String, anchor: FloatingShape?)
}

/// Where `boards.add` was chosen: a new board is placed next to that window.
enum BoardAddOrigin: Equatable {
    case none
    case orb(String)
    case board(String)
}

/// The leaderboard boards (DESIGN 2c): the persisted document (settings key `boards`, written on every change), one
/// runtime per board fed from every snapshot, the edits of the menus and Settings, and the Settings selection.
@MainActor
@Observable
final class BoardsController {
    static let documentKey = "boards"

    private(set) var document: BoardsDocument
    /// The board the Settings editor shows; the context menu's `boards.settings` selects its board here.
    var settingsSelection: String?

    @ObservationIgnored var onChange: ((BoardsChange) -> Void)?
    @ObservationIgnored private var runtimes: [String: BoardRuntime] = [:]
    @ObservationIgnored private weak var model: AppModel?
    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored private var loops: [ObservationLoop] = []
    @ObservationIgnored private var listener: UUID?

    init(defaults: UserDefaults) {
        self.defaults = defaults
        document = defaults.data(forKey: Self.documentKey).flatMap(BoardsDocument.init(data:)) ?? .empty
        settingsSelection = document.boards.first?.id
        for board in document.boards { runtimes[board.id] = BoardRuntime(config: board) }
    }

    /// Follows the data: every snapshot (and the settings a tick depends on) reaches every board. Call once.
    func activate(model: AppModel) {
        guard self.model == nil else { return }
        self.model = model
        loops.append(ObservationLoop { [weak self] in
            guard let self, let model = self.model else { return }
            let snapshot = model.store.snapshot
            let settings = model.settings
            _ = (settings.metric, settings.redlineOutput, settings.redlineTotal, settings.celebratePeaks)
            _ = (model.l10n.language, model.cost.currency, model.cost.rate, settings.billableSources, model.titles.document)
            self.ingest(snapshot, model: model)
        })
        loops.append(ObservationLoop { [weak self] in
            guard let self, let model = self.model else { return }
            model.setBoardSubsets(Set(self.document.boards.compactMap { $0.sources.subsetKey(hosts: $0.hosts) }))
        })
        listener = model.store.addEventListener { [weak self] event in
            guard let self, case .newPeak = event else { return }
            for runtime in self.runtimes.values { runtime.engineReachedNewPeak() }
        }
    }

    func shutdown() {
        loops.forEach { $0.cancel() }
        loops.removeAll()
        if let listener { model?.store.removeEventListener(listener) }
        listener = nil
    }

    private func ingest(_ snapshot: SpeedSnapshot, model: AppModel) {
        let now = Clock.now
        for board in document.boards { runtimes[board.id]?.ingest(snapshot, model: model, now: now) }
    }

    // MARK: Reading

    var boards: [BoardConfig] { document.boards }

    func board(_ id: String) -> BoardConfig? { document.board(id) }

    func runtime(_ id: String) -> BoardRuntime? { runtimes[id] }

    var canAdd: Bool { document.canAdd }

    var anyVisible: Bool { document.anyVisible }

    /// The board's sources in words: `orbs.sources.all` or the source names joined with `list.separator`.
    func sourcesText(of board: BoardConfig, l10n: Localizer) -> String {
        let sources = board.sources.isAll ? l10n("orbs.sources.all")
            : board.sources.knownSources.map { l10n($0.titleKey) }.joined(separator: l10n("list.separator"))
        // The hosts follow as in an orb's automatic name (DESIGN 13).
        return sources + (model?.orbs.namer(l10n).hostSuffix(board.hosts) ?? "")
    }

    // MARK: Editing

    /// `boards.add`: the last used style (else neon), M, all sources; placed next to `anchor`. Nil at the limit.
    @discardableResult
    func add(near anchor: FloatingShape? = nil) -> String? {
        var next = document
        let id = UUID().uuidString
        guard next.add(id: id) != nil else { return nil }
        setDocument(next)
        settingsSelection = id
        onChange?(.created(id: id, anchor: anchor))
        return id
    }

    func remove(_ id: String) {
        var next = document
        next.remove(id)
        setDocument(next)
        if settingsSelection == id { settingsSelection = document.boards.first?.id }
    }

    func edit(_ id: String, _ change: (inout BoardsDocument) -> Void) {
        var next = document
        change(&next)
        setDocument(next)
    }

    func setStyle(_ style: BoardStyle, of id: String) { edit(id) { $0.setStyle(style, of: id) } }
    func setSize(_ size: BoardSize, of id: String) { edit(id) { $0.setSize(size, of: id) } }
    func setSources(_ sources: OrbSourceSelection, of id: String) { edit(id) { $0.setSources(sources, of: id) } }
    func toggleSource(_ kind: SourceKind, of id: String) { edit(id) { $0.toggleSource(kind, of: id) } }
    func setHosts(_ hosts: OrbHostSelection, of id: String) {
        let order = model?.hostOrder ?? [RemoteHost.localId]
        edit(id) { $0.setHosts(hosts.ordered(by: order), of: id) }
    }
    func toggleHost(_ hostId: String, of id: String) {
        let order = model?.hostOrder ?? [RemoteHost.localId]
        edit(id) { $0.toggleHost(hostId, known: order, of: id) }
    }
    /// A host removed from the settings leaves every board (REMOTE 2.4).
    func removeHost(_ hostId: String) {
        var next = document
        next.removeHost(hostId)
        setDocument(next)
    }
    func setOpacity(_ opacity: Double, of id: String) { edit(id) { $0.setOpacity(opacity, of: id) } }
    func setAlwaysOnTop(_ value: Bool, of id: String) { edit(id) { $0.setAlwaysOnTop(value, of: id) } }
    func setClickThrough(_ value: Bool, of id: String) { edit(id) { $0.setClickThrough(value, of: id) } }
    func setFadeWhenIdle(_ value: Bool, of id: String) { edit(id) { $0.setFadeWhenIdle(value, of: id) } }
    func setAllSpaces(_ value: Bool, of id: String) { edit(id) { $0.setAllSpaces(value, of: id) } }
    func setVisible(_ value: Bool, of id: String) { edit(id) { $0.setVisible(value, of: id) } }
    func setReadouts(_ readouts: BoardReadouts, of id: String) { edit(id) { $0.setReadouts(readouts, of: id) } }
    func toggleReadout(_ readout: BoardReadout, of id: String) { edit(id) { $0.toggleReadout(readout, of: id) } }

    /// `boards.show` / `boards.hide`.
    func setAllVisible(_ visible: Bool) {
        var next = document
        next.setAllVisible(visible)
        setDocument(next)
    }

    func move(fromOffsets source: IndexSet, toOffset destination: Int) {
        var next = document
        next.move(fromOffsets: source, toOffset: destination)
        setDocument(next)
    }

    // MARK: Positions

    /// Stores where a board rests in an arrangement (persisted).
    func setPlacement(_ entry: OrbLayoutEntry, of id: String, arrangement: String) {
        guard document.board(id) != nil, document.entry(board: id, arrangement: arrangement) != entry else { return }
        var next = document
        next.setEntry(entry, board: id, arrangement: arrangement)
        setDocument(next)
    }

    // MARK: Storage

    private func setDocument(_ next: BoardsDocument) {
        guard next != document else { return }
        document = next
        defaults.set(next.data(), forKey: Self.documentKey)
        syncRuntimes()
    }

    private func syncRuntimes() {
        let ids = Set(document.boards.map(\.id))
        for id in runtimes.keys where !ids.contains(id) { runtimes[id] = nil }
        var changed: [BoardRuntime] = []
        for board in document.boards {
            if let runtime = runtimes[board.id] {
                guard runtime.config != board else { continue }
                runtime.update(config: board)
                changed.append(runtime)
            } else {
                let runtime = BoardRuntime(config: board)
                runtimes[board.id] = runtime
                changed.append(runtime)
            }
        }
        guard let model else { return }
        let snapshot = model.store.snapshot
        for runtime in changed { runtime.ingest(snapshot, model: model, now: Clock.now) }
    }
}
