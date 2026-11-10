import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import Observation

/// An undoable change of the orb list (DESIGN 2b, Split and merge: undo for 8 s).
struct OrbsUndo: Equatable, Identifiable {
    enum Kind: Equatable {
        case split(count: Int, limited: Bool)
        case merge
        case remove(name: String)
    }

    let id = UUID()
    let kind: Kind
    /// The list and layouts before the change, restored exactly by `OrbsController.undo()`.
    let previous: OrbsDocument
    /// The orb the desktop undo chip sits under.
    let anchorOrb: String?
    let expires: Date

    static func == (lhs: OrbsUndo, rhs: OrbsUndo) -> Bool { lhs.id == rhs.id }

    /// The localization key and arguments of the banner text.
    var messageKey: String {
        switch kind {
        case .split(_, let limited): limited ? "orbs.didSplitLimited" : "orbs.didSplit"
        case .merge: "orbs.didMerge"
        case .remove: "orbs.didRemove"
        }
    }
}

/// Something the windows animate (DESIGN 2b, Creating and Split and merge motion).
enum OrbsChange {
    /// New orbs; `from` is the orb they were created from (placement anchor); `showChip` from the menu.
    case created(ids: [String], from: String?, showChip: Bool)
    case split(original: String, children: [String])
    case merged(kept: String, removed: [String])
    case removed(id: String)
    /// Undo restored a previous list and layout.
    case restored
    case sourcesChanged(id: String)
    case resetPosition(id: String)
}

/// The floating orbs (DESIGN 2b): the persisted document, the master switch, one runtime per orb fed from every
/// snapshot, the editing operations with their 8 s undo, and the state Settings and the windows share.
@MainActor
@Observable
final class OrbsController {
    static let documentKey = "orbs"
    static let undoDuration: TimeInterval = 8

    /// The orbs, their order and their positions per display arrangement.
    private(set) var document: OrbsDocument
    /// The undo banner of the last split, merge or remove, for 8 s.
    private(set) var undoBanner: OrbsUndo?
    /// Hovering, selecting or focusing an orb row in Settings rings that orb on the desktop.
    var highlightedOrb: String?
    /// The orb Settings shows; the context menu's Settings item selects its orb here.
    var settingsSelection: String?
    /// Detected and enabled sources, in source order.
    private(set) var availability = OrbAvailability(detected: SourceKind.allCases)

    /// Called after each change the windows animate.
    @ObservationIgnored var onChange: ((OrbsChange) -> Void)?
    @ObservationIgnored private var runtimes: [String: OrbRuntime] = [:]
    @ObservationIgnored private weak var model: AppModel?
    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored private var undoTimer: Timer?
    @ObservationIgnored private var loops: [ObservationLoop] = []
    @ObservationIgnored private var listener: UUID?

    init(defaults: UserDefaults) {
        self.defaults = defaults
        if let data = defaults.data(forKey: Self.documentKey), let document = OrbsDocument(data: data) {
            self.document = document
        } else {
            document = OrbsDocument.fresh(id: UUID().uuidString)
            needsMigration = true
        }
        settingsSelection = document.orbs.first?.id
        for orb in document.orbs { runtimes[orb.id] = OrbRuntime(config: orb) }
    }

    @ObservationIgnored private var needsMigration = false

    /// Migrates the single orb of earlier versions on first launch, then follows the data. Call once.
    func activate(model: AppModel) {
        guard self.model == nil else { return }
        self.model = model
        if needsMigration {
            migrate(settings: model.settings)
            needsMigration = false
        }
        loops.append(ObservationLoop { [weak self] in
            guard let self, let model = self.model else { return }
            let snapshot = model.store.snapshot
            _ = (model.settings.metric, model.settings.redlineOutput, model.settings.redlineTotal)
            // The money unit follows the currency, language, rates and subscription marks (DESIGN 9).
            _ = (model.cost.currency, model.cost.rate, model.l10n.language, model.settings.billableSources)
            self.ingest(snapshot, model: model)
        })
        loops.append(ObservationLoop { [weak self] in
            guard let self, let model = self.model else { return }
            model.setOrbSubsets(Set(self.document.orbs.compactMap { $0.sources.subsetKey(hosts: $0.hosts) }))
        })
        loops.append(ObservationLoop { [weak self] in
            guard let self, let model = self.model else { return }
            let snapshot = model.visibleTraffic
            let settings = model.settings
            _ = (settings.trafficEnabled, settings.trafficIncludeTools, settings.trafficUnit, self.document.orbs)
            let now = Clock.now
            for runtime in self.runtimes.values { runtime.ingestTraffic(snapshot, settings: settings, now: now) }
        })
        loops.append(ObservationLoop { [weak self] in
            guard let self, let model = self.model else { return }
            let orbs = self.document.orbs
            let connected = Set(model.snapshot.hosts.filter { $0.isConnected && !$0.isLocal }.map(\.id))
            for runtime in self.runtimes.values { runtime.ingestQuota(model.quota, connected: connected) }
            // The light process listing runs only while a character orb could show the card (QUOTAS 6).
            let visible = model.settings.orbsVisible
            model.quota.wantsCard = visible && orbs.contains { orb in
                orb.visible && orb.style == .character && orb.characterReadouts.quota
                    && QuotaController.sources.contains { orb.sources.sourceSet?.contains($0) ?? true }
            }
        })
        listener = model.store.addEventListener { [weak self] event in
            guard let self, let model = self.model else { return }
            for runtime in self.runtimes.values { runtime.handle(event, isPreviewing: model.store.isPreviewing) }
        }
    }

    func shutdown() {
        loops.forEach { $0.cancel() }
        loops.removeAll()
        if let listener { model?.store.removeEventListener(listener) }
        runtimes.values.forEach { $0.stop() }
    }

    private func migrate(settings: AppSettings) {
        let storage = settings.storage
        let legacy = LegacyOrbPositions(defaults: storage)
        let settings = LegacyOrbSettings(
            visible: storage.object(forKey: "orbVisible") as? Bool ?? true,
            size: storage.object(forKey: "orbSize") as? Double ?? OrbSizes.default,
            opacity: storage.object(forKey: "orbOpacity") as? Double ?? 1,
            alwaysOnTop: storage.object(forKey: "orbAlwaysOnTop") as? Bool ?? true,
            clickThrough: storage.object(forKey: "orbClickThrough") as? Bool ?? false,
            fadeWhenIdle: storage.object(forKey: "orbFadeWhenIdle") as? Bool ?? false,
            allSpaces: storage.object(forKey: "orbAllSpaces") as? Bool ?? true,
            positions: legacy.records,
            lastDisplay: legacy.lastDisplay
        )
        let id = document.orbs.first?.id ?? UUID().uuidString
        // The old app placed its orb on `NSScreen.main ?? first`; migrate against the same display.
        let result = OrbsMigration.migrate(settings, id: id, connectedDisplays: Self.connectedDisplays,
                                           mainDisplay: (NSScreen.main ?? NSScreen.screens.first)?.orbDisplayUUID)
        self.model?.settings.orbsVisible = result.masterVisible
        setDocument(result.document)
        settingsSelection = id
    }

    // MARK: Displays

    static var connectedDisplays: [String] { NSScreen.screens.compactMap(\.orbDisplayUUID) }
    static var mainDisplay: String? { (NSScreen.screens.first ?? NSScreen.main)?.orbDisplayUUID }
    static var arrangementKey: String { OrbsDocument.arrangementKey(connectedDisplays) }

    // MARK: Data

    private func ingest(_ snapshot: SpeedSnapshot, model: AppModel) {
        let detected = snapshot.sources.filter { $0.enabled && !OrbSubset.isUnavailable($0) }.map(\.kind)
        let next = OrbAvailability(detected: detected)
        if next != availability { availability = next }
        let previewing = model.store.isPreviewing
        let now = Clock.now
        let characters = document.orbs.contains { $0.style == .character } ? model.characterInputs : nil
        for orb in document.orbs {
            runtimes[orb.id]?.ingest(snapshot, settings: model.settings, cost: model.cost, isPreviewing: previewing, now: now,
                                     characters: characters)
        }
    }

    func runtime(_ id: String) -> OrbRuntime? { runtimes[id] }

    var orbs: [OrbConfig] { document.orbs }

    func orb(_ id: String) -> OrbConfig? { document.orb(id) }

    /// The master switch: hides or shows every orb; turning it on while every orb is hidden shows them all.
    var masterVisible: Bool {
        get { model?.settings.orbsVisible ?? true }
        set {
            guard let settings = model?.settings else { return }
            if newValue { setDocument(OrbEditing.showAll(document)) }
            settings.orbsVisible = newValue
        }
    }

    /// Orbs shown on the desktop right now.
    var shownOrbs: [OrbConfig] { masterVisible ? document.orbs.filter(\.visible) : [] }

    // MARK: Names

    func namer(_ l10n: Localizer) -> OrbNamer {
        let names = Dictionary(uniqueKeysWithValues: SourceKind.allCases.map { ($0, l10n($0.titleKey)) })
        let pair = l10n("orbs.name.pair")
        let more = l10n("orbs.name.more")
        return OrbNamer(
            sourceName: { names[$0] ?? $0.rawValue },
            allSources: l10n("orbs.sources.all"),
            pair: { String(format: pair, $0, $1) },
            more: { String(format: more, $0, $1) },
            hosts: model?.hostChoices ?? []
        )
    }

    /// The custom name, else the automatic one (DESIGN 2b, Data model).
    func displayName(of id: String) -> String {
        guard let model, let orb = document.orb(id) else { return "" }
        return namer(model.l10n).displayName(orb)
    }

    /// The automatic name of an orb (Settings placeholder).
    func automaticName(of id: String) -> String {
        guard let model, let orb = document.orb(id) else { return "" }
        return namer(model.l10n).automaticName(orb.sources, hosts: orb.hosts)
    }

    /// The colour dots of an orb: its known sources with their availability; empty for all sources.
    func dots(of id: String) -> [(source: SourceKind, available: Bool)] {
        guard let orb = document.orb(id), !orb.sources.isAll else { return [] }
        let unavailable = runtimes[id]?.reading.unavailable ?? []
        return orb.sources.knownSources.map { ($0, !unavailable.contains($0)) }
    }

    // MARK: Editing

    var canAdd: Bool { document.canAdd }
    var canMerge: Bool { document.orbs.count > 1 }

    /// Split is offered for an orb tracking two or more available sources (all sources: two or more detected).
    func canSplit(_ id: String) -> Bool {
        guard document.canAdd, let orb = document.orb(id) else { return false }
        return availability.available(orb.sources).count >= 2
    }

    var canSplitBySource: Bool { availability.detected.count >= 2 }

    /// Adds an orb copying `sourceId` (the selected or clicked orb); returns its id, nil at eight orbs.
    @discardableResult
    func add(copying sourceId: String?, showChip: Bool = false) -> String? {
        let id = UUID().uuidString
        guard let next = OrbEditing.add(to: document, copying: sourceId, availability: availability, newId: id) else { return nil }
        setDocument(next)
        settingsSelection = showChip ? settingsSelection : id
        onChange?(.created(ids: [id], from: sourceId, showChip: showChip))
        return id
    }

    @discardableResult
    func duplicate(_ id: String, showChip: Bool = false) -> String? {
        let newId = UUID().uuidString
        guard let next = OrbEditing.duplicate(id, in: document, newId: newId) else { return nil }
        setDocument(next)
        if !showChip { settingsSelection = newId }
        onChange?(.created(ids: [newId], from: id, showChip: showChip))
        return newId
    }

    /// Removes an orb (never the last one) with an 8 s undo.
    func remove(_ id: String) {
        guard let next = OrbEditing.remove(id, from: document) else { return }
        let name = displayName(of: id)
        let previous = document
        let index = document.index(of: id) ?? 0
        setDocument(next)
        if settingsSelection == id { settingsSelection = next.orbs[min(index, next.orbs.count - 1)].id }
        offerUndo(.remove(name: name), previous: previous, anchor: next.orbs.first?.id)
        onChange?(.removed(id: id))
    }

    /// "Split this orb" (menu): one orb per available source; the first keeps the id and position.
    func split(_ id: String) {
        let ids = (0..<OrbsDocument.maxOrbs).map { _ in UUID().uuidString }
        guard let result = OrbEditing.split(id, in: document, availability: availability, newIds: ids) else { return }
        let previous = document
        setDocument(result.document)
        offerUndo(.split(count: result.created.count + 1, limited: result.limited), previous: previous, anchor: id)
        onChange?(.split(original: id, children: result.created))
    }

    /// Settings → Split by source: every orb is replaced by one per detected, enabled source.
    func splitBySource(selected: String?) {
        let ids = (0..<OrbsDocument.maxOrbs).map { _ in UUID().uuidString }
        guard let result = OrbEditing.splitBySource(document, selectedId: selected, availability: availability, ids: ids),
              let first = result.document.orbs.first else { return }
        let previous = document
        let removed = previous.orbs.map(\.id).filter { $0 != first.id }
        setDocument(result.document)
        settingsSelection = first.id
        offerUndo(.split(count: result.document.orbs.count, limited: result.limited), previous: previous, anchor: first.id)
        if !removed.isEmpty { onChange?(.merged(kept: first.id, removed: removed)) }
        onChange?(.split(original: first.id, children: result.document.orbs.dropFirst().map(\.id)))
    }

    /// Merge: one all-sources orb keeping `id`'s id, style, size, toggles and position.
    func merge(keeping id: String) {
        guard let next = OrbEditing.merge(document, keeping: id) else { return }
        let previous = document
        let removed = previous.orbs.map(\.id).filter { $0 != id }
        setDocument(next)
        settingsSelection = id
        offerUndo(.merge, previous: previous, anchor: id)
        onChange?(.merged(kept: id, removed: removed))
    }

    func setSources(_ selection: OrbSourceSelection, of id: String) {
        guard document.orb(id)?.sources != selection else { return }
        setDocument(OrbEditing.setSources(selection, of: id, in: document))
        onChange?(.sourcesChanged(id: id))
    }

    /// The context-menu rule: all ↔ one source, toggling within an explicit set, never the last known source.
    func toggleSource(_ kind: SourceKind, of id: String) {
        guard let orb = document.orb(id), let next = orb.sources.toggling(kind) else { return }
        setSources(next, of: id)
    }

    /// The hosts an orb counts (DESIGN 13), kept in settings order.
    func setHosts(_ selection: OrbHostSelection, of id: String) {
        let ordered = selection.ordered(by: model?.hostOrder ?? [RemoteHost.localId])
        guard document.orb(id)?.hosts != ordered else { return }
        setDocument(OrbEditing.setHosts(ordered, of: id, in: document))
        onChange?(.sourcesChanged(id: id))
    }

    /// As sources: all ↔ one host, toggling within an explicit set, never the last known host.
    func toggleHost(_ hostId: String, of id: String) {
        guard let orb = document.orb(id),
              let next = orb.hosts.toggling(hostId, known: model?.hostOrder ?? [RemoteHost.localId]) else { return }
        setHosts(next, of: id)
    }

    /// A host removed from the settings leaves every orb (REMOTE 2.4).
    func removeHost(_ hostId: String) {
        setDocument(OrbEditing.removingHost(hostId, from: document))
    }

    /// Switching to `character` starts the automatic download of every character, this orb's live vendors first
    /// (CHARACTERS 2).
    func setStyle(_ style: OrbStyleKind, of id: String) {
        let previous = document.orb(id)?.style
        setDocument(OrbEditing.setStyle(style, of: id, in: document))
        guard style == .character, previous != .character, let model else { return }
        let reading = VendorReading(agents: runtimes[id]?.reading.agents ?? [], prices: model.priceBook.current)
        model.characters.downloadAll(preferring: CharacterAutoDownload.vendors(reading))
    }

    /// A pack id, or nil to follow the fastest model vendor (CHARACTERS 7).
    func setCharacter(_ character: String?, of id: String) {
        setDocument(OrbEditing.setCharacter(character, of: id, in: document))
    }

    /// tok/s or the money unit (DESIGN 9); persisted at once.
    func setUnit(_ unit: OrbUnit, of id: String) {
        setDocument(OrbEditing.setUnit(unit, of: id, in: document))
    }

    func setTraffic(_ traffic: OrbTraffic, of id: String) {
        setDocument(OrbEditing.setTraffic(traffic, of: id, in: document))
    }

    func setShowName(_ showName: Bool?, of id: String) {
        setDocument(OrbEditing.setShowName(showName, of: id, in: document))
    }

    /// A `character` orb keeps tok/s: its cost has a card of its own (DESIGN 2a.6).
    func toggleUnit(of id: String) {
        guard let orb = document.orb(id), orb.style != .character else { return }
        setUnit(orb.unit.toggled, of: id)
    }

    func setCharacterReadout(_ readout: CharacterReadout, _ on: Bool, of id: String) {
        setDocument(OrbEditing.setCharacterReadout(readout, on, of: id, in: document))
    }

    func setSize(_ size: Double, of id: String) {
        setDocument(OrbEditing.setSize(size, of: id, in: document))
    }

    func setOpacity(_ opacity: Double, of id: String) {
        setDocument(OrbEditing.setOpacity(opacity, of: id, in: document))
    }

    func setAlwaysOnTop(_ value: Bool, of id: String) {
        setDocument(OrbEditing.setToggle(\.alwaysOnTop, value, of: id, in: document))
    }

    func setClickThrough(_ value: Bool, of id: String) {
        setDocument(OrbEditing.setToggle(\.clickThrough, value, of: id, in: document))
    }

    func setFadeWhenIdle(_ value: Bool, of id: String) {
        setDocument(OrbEditing.setToggle(\.fadeWhenIdle, value, of: id, in: document))
    }

    func setAllSpaces(_ value: Bool, of id: String) {
        setDocument(OrbEditing.setToggle(\.allSpaces, value, of: id, in: document))
    }

    /// Hides or shows one orb; hiding the only visible one turns the master switch off instead (DESIGN 2b, menu).
    func setVisible(_ visible: Bool, of id: String) {
        if !visible, masterVisible, document.orbs.filter(\.visible).count == 1, document.orb(id)?.visible == true {
            masterVisible = false
            return
        }
        setDocument(OrbEditing.setVisible(visible, of: id, in: document))
        if visible, !masterVisible { model?.settings.orbsVisible = true }
    }

    func setName(_ name: String?, of id: String) {
        setDocument(OrbEditing.setName(name, of: id, in: document))
    }

    /// nil follows the General redline of that metric.
    func setRedline(_ value: Double?, metric: SpeedMetric, of id: String) {
        let snapped = value.map { SettingsLimits.snapRedline($0, metric: metric) }
        setDocument(OrbEditing.setRedline(snapped, metric: metric, of: id, in: document))
    }

    /// nil follows the General title theme (DESIGN 2b `titleTheme`).
    func setTitleTheme(_ theme: String?, of id: String) {
        setDocument(OrbEditing.setTitleTheme(theme, of: id, in: document))
    }

    /// Orbs naming a deleted custom title set take its base preset; returns the document before, for undo.
    @discardableResult
    func replaceTitleTheme(_ old: String, with replacement: String?) -> OrbsDocument {
        let previous = document
        setDocument(OrbEditing.replacingTitleTheme(old, with: replacement, in: document))
        return previous
    }

    /// Restores the title-theme references of a document saved before a title-set deletion (its undo).
    func restoreTitleThemes(from previous: OrbsDocument) {
        var next = document
        for index in next.orbs.indices {
            if let old = previous.orb(next.orbs[index].id) { next.orbs[index].titleTheme = old.titleTheme }
        }
        setDocument(next)
    }

    func move(fromOffsets source: IndexSet, toOffset destination: Int) {
        setDocument(OrbEditing.move(fromOffsets: source, toOffset: destination, in: document))
    }

    /// Returns the orb to the default corner (or the creation placement when another orb is there).
    func resetPosition(_ id: String) {
        var next = document
        for arrangement in Array(next.layouts.keys) { next.clearEntry(orb: id, arrangement: arrangement) }
        next.displayRecords[id] = nil
        setDocument(next)
        onChange?(.resetPosition(id: id))
    }

    // MARK: Positions

    /// Stores where an orb rests in the current arrangement (no undo, persisted).
    func setPlacement(_ entry: OrbLayoutEntry, of id: String, arrangement: String) {
        guard document.orb(id) != nil, document.entry(orb: id, arrangement: arrangement) != entry else { return }
        var next = document
        next.setEntry(entry, orb: id, arrangement: arrangement)
        setDocument(next)
    }

    // MARK: Undo

    func undo() {
        guard let banner = undoBanner else { return }
        undoTimer?.invalidate()
        undoTimer = nil
        undoBanner = nil
        setDocument(banner.previous)
        if document.orb(settingsSelection ?? "") == nil { settingsSelection = document.orbs.first?.id }
        onChange?(.restored)
    }

    func dismissUndo() {
        undoTimer?.invalidate()
        undoTimer = nil
        undoBanner = nil
    }

    /// The banner text of the current undo.
    func undoMessage(_ l10n: Localizer) -> String {
        guard let banner = undoBanner else { return "" }
        switch banner.kind {
        case .split(let count, _): return l10n(banner.messageKey, count)
        case .merge: return l10n(banner.messageKey)
        case .remove(let name): return l10n(banner.messageKey, name)
        }
    }

    private func offerUndo(_ kind: OrbsUndo.Kind, previous: OrbsDocument, anchor: String?) {
        undoTimer?.invalidate()
        let banner = OrbsUndo(kind: kind, previous: previous, anchorOrb: anchor, expires: Date().addingTimeInterval(Self.undoDuration))
        undoBanner = banner
        let timer = Timer(timeInterval: Self.undoDuration, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.undoBanner?.id == banner.id else { return }
                self.undoBanner = nil
                self.undoTimer = nil
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        undoTimer = timer
    }

    // MARK: Storage

    private func setDocument(_ next: OrbsDocument) {
        guard next != document else { return }
        document = next
        defaults.set(next.data(), forKey: Self.documentKey)
        syncRuntimes()
    }

    private func syncRuntimes() {
        let ids = Set(document.orbs.map(\.id))
        for (id, runtime) in runtimes where !ids.contains(id) {
            runtime.stop()
            runtimes[id] = nil
        }
        for orb in document.orbs {
            if let runtime = runtimes[orb.id] {
                if runtime.config != orb { runtime.update(config: orb) }
            } else {
                runtimes[orb.id] = OrbRuntime(config: orb)
            }
        }
        if let model {
            let snapshot = model.store.snapshot
            let characters = document.orbs.contains { $0.style == .character } ? model.characterInputs : nil
            for orb in document.orbs {
                runtimes[orb.id]?.ingest(snapshot, settings: model.settings, cost: model.cost, isPreviewing: model.store.isPreviewing,
                                         now: Clock.now, characters: characters)
            }
            if characters != nil { model.characters.refreshInBackground() }
        }
    }
}
