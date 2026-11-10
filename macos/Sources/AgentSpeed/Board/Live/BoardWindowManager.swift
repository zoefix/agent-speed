import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// Owns one window per board (DESIGN 2c): creates, updates and closes them live from the document, places new boards
/// like new orbs next to the window they came from, keeps positions per display arrangement, and applies the global
/// look — Appearance, language, Effects intensity, Celebrate new peaks, Reduce Motion and Increase Contrast. Boards and
/// orbs never overlap: the shown orbs are obstacles for placement and snapping.
@MainActor
final class BoardWindowManager {
    private let model: AppModel
    /// The shown orbs, as obstacles (screen points).
    private let orbShapes: () -> [FloatingShape]
    private var presenters: [String: BoardPresenter] = [:]
    private var loops: [ObservationLoop] = []
    private var arrangement: String
    nonisolated(unsafe) private var screenObserver: NSObjectProtocol?
    private var appearanceObservation: NSKeyValueObservation?
    private var pendingPlacement: Set<String> = []

    init(model: AppModel, orbShapes: @escaping () -> [FloatingShape]) {
        self.model = model
        self.orbShapes = orbShapes
        arrangement = OrbsController.arrangementKey
        model.boards.onChange = { [weak self] change in self?.handle(change) }
        sync()
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            _ = self.model.boards.document.boards
            self.sync()
        })
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            _ = (self.model.settings.effects, self.model.settings.celebratePeaks, self.model.accessibility.reduceMotion,
                 self.model.accessibility.increaseContrast, self.model.effectsLevel, self.model.l10n.language)
            self.applyLook()
        })
        appearanceObservation = NSApp.observe(\.effectiveAppearance) { [weak self] _, _ in
            MainActor.assumeIsolated { self?.applyLook() }
        }
        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.screensChanged() }
        }
    }

    deinit {
        if let screenObserver { NotificationCenter.default.removeObserver(screenObserver) }
    }

    func shutdown() {
        loops.forEach { $0.cancel() }
        loops.removeAll()
        appearanceObservation = nil
        presenters.values.forEach { $0.close() }
        presenters.removeAll()
    }

    func window(for id: String) -> BoardWindowController? { presenters[id]?.window }

    /// The shown boards, as obstacles for the orbs and for each other.
    func shapes(excluding id: String? = nil) -> [FloatingShape] {
        presenters.values.compactMap { presenter in
            guard presenter.id != id, presenter.window.panel.isVisible else { return nil }
            return presenter.window.shape
        }
    }

    private static var isDark: Bool {
        NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
    }

    // MARK: Sync

    private func sync() {
        let boards = model.boards.boards
        let ids = Set(boards.map(\.id))
        for (id, presenter) in presenters where !ids.contains(id) {
            presenter.close()
            presenters[id] = nil
        }
        var placed: [FloatingShape] = []
        for board in boards {
            guard let runtime = model.boards.runtime(board.id) else { continue }
            if let presenter = presenters[board.id] {
                presenter.apply(board)
            } else {
                let center = pendingPlacement.contains(board.id) ? .zero : launchCenter(for: board, placed: placed)
                let presenter = BoardPresenter(model: model, runtime: runtime, center: center, dark: Self.isDark)
                presenter.window.obstacles = { [weak self] in
                    guard let self else { return [] }
                    return self.orbShapes() + self.shapes(excluding: board.id)
                }
                presenter.window.onMoved = { [weak self] center in self?.settled(board.id, center: center) }
                presenter.window.onContextMenu = { [weak self] event in self?.showMenu(board.id, event: event) }
                presenters[board.id] = presenter
                applyLook(to: presenter)
            }
            if let presenter = presenters[board.id], !pendingPlacement.contains(board.id) {
                placed.append(presenter.window.shape)
                board.visible ? presenter.window.show() : presenter.window.hide()
            }
        }
    }

    private func applyLook() {
        presenters.values.forEach(applyLook(to:))
    }

    private func applyLook(to presenter: BoardPresenter) {
        let accessibility = model.accessibility
        presenter.setLook(dark: Self.isDark, language: model.l10n.language)
        presenter.setEffects(BoardEffectsPlayer.intensity(model.effectsLevel), celebrations: model.settings.celebratePeaks,
                             reduceMotion: accessibility.reduceMotion, highContrast: accessibility.increaseContrast)
    }

    private func showMenu(_ id: String, event: NSEvent) {
        guard let window = presenters[id]?.window else { return }
        let menu = BoardMenus.contextMenu(model: model, boardId: id)
        let point = event.window?.convertPoint(toScreen: event.locationInWindow) ?? NSEvent.mouseLocation
        menu.popUpDetached(at: point, appearance: window.host.effectiveAppearance)
    }

    // MARK: Placement

    private static var primaryScreen: NSScreen? { NSScreen.main ?? NSScreen.screens.first }

    private static func screen(with uuid: String) -> NSScreen? {
        NSScreen.screens.first { $0.orbDisplayUUID == uuid }
    }

    private static func screen(containing point: CGPoint) -> NSScreen? {
        NSScreen.screens.first { $0.frame.contains(point) } ?? primaryScreen
    }

    private func template(for board: BoardConfig) -> FloatingShape {
        .board(center: .zero, side: CGFloat(board.size.side), corner: CGFloat(board.style.cornerRadius))
    }

    /// Its entry for this arrangement (or the arrangement resolver's choice), else placed like a new board without
    /// an anchor, clear of the orbs and the boards placed before it.
    private func launchCenter(for board: BoardConfig, placed: [FloatingShape]) -> CGPoint {
        let connected = OrbsController.connectedDisplays
        let main = OrbsController.mainDisplay ?? connected.first ?? ""
        let entries = OrbLayoutResolver.entries(document: model.boards.document, arrangement: arrangement,
                                                previousArrangement: nil, connected: connected, main: main)
        let half = CGFloat(board.size.side) / 2
        if let entry = entries[board.id] ?? nil, let screen = Self.screen(with: entry.display) ?? Self.primaryScreen {
            let visible = screen.visibleFrame
            let center = OrbSnap.clamped(entry.record.center(radius: half, visible: visible), radius: half, in: visible)
            let shape = template(for: board).moved(to: center)
            let others = orbShapes() + placed
            return FloatingPlacement.isFree(shape, others: others) ? center
                : FloatingPlacement.resolveOverlap(shape, others: others, visible: visible)
        }
        guard let screen = Self.primaryScreen else { return .zero }
        return FloatingPlacement.placeNew(template(for: board), near: nil, others: orbShapes() + placed,
                                          visible: screen.visibleFrame)
    }

    private func handle(_ change: BoardsChange) {
        switch change {
        case .created(let id, let anchor):
            pendingPlacement.insert(id)
            sync()
            pendingPlacement.remove(id)
            placeCreated(id, anchor: anchor)
        }
    }

    /// `boards.add` (DESIGN 2c): on the display of the window it came from, its first free touching slot, else the
    /// nearest free grid point, else cascaded; it springs in from the default corner when nothing anchors it.
    private func placeCreated(_ id: String, anchor: FloatingShape?) {
        guard let presenter = presenters[id], let board = model.boards.board(id) else { return }
        let screen = anchor.map { Self.screen(containing: $0.center) } ?? Self.primaryScreen
        guard let screen else { return }
        let others = orbShapes() + shapes(excluding: id)
        let target = FloatingPlacement.placeNew(template(for: board), near: anchor, others: others, visible: screen.visibleFrame)
        presenter.window.move(to: target, animated: false)
        if board.visible {
            presenter.window.show()
            presenter.window.fadeIn(duration: model.accessibility.reduceMotion ? 0.2 : 0.3)
        }
        settled(id, center: target)
    }

    private func settled(_ id: String, center: CGPoint) {
        guard let window = presenters[id]?.window, let screen = Self.screen(containing: center),
              let display = screen.orbDisplayUUID else { return }
        let record = OrbPositionRecord(center: center, radius: window.side / 2, visible: screen.visibleFrame)
        model.boards.setPlacement(OrbLayoutEntry(display: display, record: record), of: id, arrangement: arrangement)
    }

    /// Display arrangement or parameters changed: as the orbs (DESIGN 2b Arrangement changes), in list order, never
    /// off-screen and clear of the orbs.
    private func screensChanged() {
        guard !NSScreen.screens.isEmpty else { return }
        let next = OrbsController.arrangementKey
        let previous = arrangement
        arrangement = next
        let connected = OrbsController.connectedDisplays
        let main = OrbsController.mainDisplay ?? connected.first ?? ""
        let entries = OrbLayoutResolver.entries(document: model.boards.document, arrangement: next,
                                                previousArrangement: previous == next ? nil : previous,
                                                connected: connected, main: main)
        var placed = orbShapes()
        for board in model.boards.boards {
            guard let window = presenters[board.id]?.window else { continue }
            let half = window.side / 2
            var center: CGPoint
            var visible: CGRect
            if let entry = entries[board.id] ?? nil, let screen = Self.screen(with: entry.display) ?? Self.primaryScreen {
                visible = screen.visibleFrame
                center = entry.record.center(radius: half, visible: visible)
            } else if let screen = Self.screen(containing: window.center) {
                visible = screen.visibleFrame
                center = window.center
            } else {
                continue
            }
            center = OrbSnap.clamped(center, radius: half, in: visible)
            let shape = window.shape.moved(to: center)
            if !FloatingPlacement.isFree(shape, others: placed) {
                center = FloatingPlacement.resolveOverlap(shape, others: placed, visible: visible)
            }
            placed.append(window.shape.moved(to: center))
            window.move(to: center, animated: false)
        }
    }
}
