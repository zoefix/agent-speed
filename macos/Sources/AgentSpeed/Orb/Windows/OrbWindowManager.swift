import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// Owns one window per floating orb (DESIGN 2b): creates, updates and removes them live from the document, places
/// new orbs, keeps positions per display arrangement, animates split / merge / remove, and runs the shared overlays
/// (alignment guides, the desktop undo chip) and the frame-clock focus.
@MainActor
final class OrbWindowManager {
    private let model: AppModel
    private var windows: [String: OrbWindowController] = [:]
    /// Windows fading out after a remove or merge, kept alive for their animation.
    private var leaving: [OrbWindowController] = []
    private let guides = OrbGuidesWindow()
    private let undoChip = OrbChipPanel(interactive: true)
    /// The undo chip in a plate's metrics and material, stacked beyond its orb's name plate.
    private let plateUndoChip = OrbNameplateUndoChip()
    private lazy var nameplates: OrbNameplateCoordinator = {
        let coordinator = OrbNameplateCoordinator { [weak self] in
            guard let self else { return [] }
            return self.model.orbs.orbs.compactMap { self.windows[$0.id] }
        }
        coordinator.onResolved = { [weak self] in self?.updateUndoChip() }
        return coordinator
    }()
    private var loops: [ObservationLoop] = []
    private var arrangement: String
    nonisolated(unsafe) private var screenObserver: NSObjectProtocol?
    nonisolated(unsafe) private var menuObserver: NSObjectProtocol?
    nonisolated(unsafe) private var closeObserver: NSObjectProtocol?
    /// Ids created by an operation, placed by `handle(_:)` rather than by `sync()`.
    private var pendingPlacement: Set<String> = []
    /// The shown leaderboard boards, which orbs keep clear of (DESIGN 2c).
    var boardShapes: () -> [FloatingShape] = { [] }

    init(model: AppModel) {
        self.model = model
        arrangement = OrbsController.arrangementKey
        model.orbs.onChange = { [weak self] change in self?.handle(change) }
        sync()
        loops.append(ObservationLoop { [weak self] in
            guard let self else { return }
            _ = (self.model.orbs.document, self.model.settings.orbsVisible)
            self.sync()
        })
        loops.append(ObservationLoop { [weak self] in self?.updateUndoChip() })
        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.screensChanged() }
        }
        // While any menu of the app tracks (an orb's or a board's context menu, the menu bar menu) it takes every mouse
        // event, so no orb sees the pointer move; the click that closes it is used up. Every orb re-reads the pointer
        // when the menu ends, so the next right-click on an orb the pointer now rests on reaches it.
        menuObserver = NotificationCenter.default.addObserver(
            forName: NSMenu.didEndTrackingNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.pointerMayHaveMoved() }
        }
        // A closing window may end a modal session (the remove confirmation) that hid the moves the same way.
        closeObserver = NotificationCenter.default.addObserver(
            forName: NSWindow.willCloseNotification, object: nil, queue: .main
        ) { [weak self] _ in
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.pointerMayHaveMoved() } }
        }
    }

    deinit {
        if let screenObserver { NotificationCenter.default.removeObserver(screenObserver) }
        if let menuObserver { NotificationCenter.default.removeObserver(menuObserver) }
        if let closeObserver { NotificationCenter.default.removeObserver(closeObserver) }
    }

    /// Every orb re-reads the pointer (after a menu or modal session that hid its moves).
    func pointerMayHaveMoved() {
        windows.values.forEach { $0.pointerMayHaveMoved() }
    }

    func shutdown() {
        loops.forEach { $0.cancel() }
        loops.removeAll()
        windows.values.forEach { $0.close() }
        windows.removeAll()
        undoChip.close()
        plateUndoChip.close()
        guides.hide()
    }

    func window(for id: String) -> OrbWindowController? { windows[id] }

    // MARK: Sync

    private func sync() {
        let orbs = model.orbs.orbs
        let ids = Set(orbs.map(\.id))
        for (id, window) in windows where !ids.contains(id) {
            windows[id] = nil
            leaving.append(window)
            window.dismiss(flyingTo: nil, reduceMotion: true) { [weak self, weak window] in
                self?.leaving.removeAll { $0 === window }
            }
        }
        var placedThisPass: [OrbDisc] = []
        for orb in orbs {
            guard let runtime = model.orbs.runtime(orb.id) else { continue }
            if let window = windows[orb.id] {
                window.update(config: orb)
            } else {
                let window = OrbWindowController(model: model, runtime: runtime, manager: self)
                windows[orb.id] = window
                if !pendingPlacement.contains(orb.id) {
                    let center = launchCenter(for: orb, placed: placedThisPass)
                    window.jump(to: center)
                }
            }
            if let window = windows[orb.id], !pendingPlacement.contains(orb.id) {
                placedThisPass.append(window.disc)
            }
        }
        applyVisibility()
        nameplates.setNeedsLayout()
    }

    private func applyVisibility() {
        let master = model.settings.orbsVisible
        for orb in model.orbs.orbs where !pendingPlacement.contains(orb.id) {
            windows[orb.id]?.setShown(master && orb.visible)
        }
    }

    /// Where an orb sits when its window is created at launch or after undo: its entry for this arrangement, else the
    /// arrangement resolver's choice, else the default corner (or the creation rule when another orb is there).
    private func launchCenter(for orb: OrbConfig, placed: [OrbDisc]) -> CGPoint {
        let displays = NSScreen.screens.compactMap { screen in
            screen.orbDisplayUUID.map { OrbDisplayInfo(key: $0, visible: screen.visibleFrame) }
        }
        let primary = Self.primaryScreen.flatMap { screen in
            screen.orbDisplayUUID.map { OrbDisplayInfo(key: $0, visible: screen.visibleFrame) }
        }
        let main = OrbsController.mainDisplay ?? displays.first?.key ?? ""
        return OrbLaunchPlacement.center(of: orb, document: model.orbs.document, arrangement: arrangement, displays: displays,
                                         main: main, primary: primary, placed: placed) ?? .zero
    }

    // MARK: Changes

    private var reduceMotion: Bool { model.accessibility.reduceMotion }

    private func handle(_ change: OrbsChange) {
        switch change {
        case .created(let ids, let from, let showChip):
            pendingPlacement.formUnion(ids)
            sync()
            for id in ids { placeCreated(id, from: from, showChip: showChip) }
        case .split(let original, let children):
            pendingPlacement.formUnion(children)
            sync()
            placeSplit(original: original, children: children)
        case .merged(let kept, let removed):
            let target = windows[kept]?.center
            for id in removed {
                guard let window = windows.removeValue(forKey: id) else { continue }
                leaving.append(window)
                window.dismiss(flyingTo: target, reduceMotion: reduceMotion) { [weak self, weak window] in
                    self?.leaving.removeAll { $0 === window }
                }
            }
            sync()
            windows[kept]?.pop(from: 0.85)
            windows[kept]?.showChipBriefly(after: 0.3)
        case .removed(let id):
            if let window = windows.removeValue(forKey: id) {
                leaving.append(window)
                window.dismiss(flyingTo: nil, reduceMotion: true) { [weak self, weak window] in
                    self?.leaving.removeAll { $0 === window }
                }
            }
            sync()
        case .restored:
            restoreLayout()
        case .sourcesChanged:
            break
        case .resetPosition(let id):
            resetPosition(id)
        }
    }

    private func placeCreated(_ id: String, from anchorId: String?, showChip: Bool) {
        guard let window = windows[id] else { return }
        pendingPlacement.remove(id)
        let anchor = anchorId.flatMap { windows[$0] }
        let screen = anchor?.screen ?? Self.primaryScreen
        guard let screen else { return }
        let others = otherDiscs(excluding: id, on: screen)
        var target = OrbPlacement.placeNew(radius: window.radius, near: anchor?.disc, others: others, visible: screen.visibleFrame)
        let boards = boardShapes(on: screen)
        let template = FloatingShape(OrbDisc(center: .zero, radius: window.radius))
        if !FloatingPlacement.isFree(template.moved(to: target), others: boards) {
            target = FloatingPlacement.placeNew(template, near: anchor.map { FloatingShape($0.disc) },
                                                others: others.map(FloatingShape.init) + boards, visible: screen.visibleFrame)
        }
        window.update(config: model.orbs.orb(id) ?? window.config)
        prepareShown(window)
        window.animateEntrance(from: nil, to: target)
        orbSettled(id, center: target, radius: window.radius, on: screen)
        if showChip { window.showChipBriefly(after: 0.1) }
    }

    /// Split motion (DESIGN 2b): the original dips to 0.85 and its bezel draws on; the children spring out of its center
    /// to a tidy row or column (60 ms stagger, response 0.45 s, damping 0.78), their bezels draw on over 0.3 s and their
    /// chips appear 0.28 s after landing for 2 s. Orbs in the way are pushed.
    private func placeSplit(original: String, children: [String]) {
        defer { children.forEach { pendingPlacement.remove($0) } }
        guard let first = windows[original], let screen = first.screen ?? Self.primaryScreen else { return }
        let visible = screen.visibleFrame
        let excluded = Set(children + [original])
        let otherIds = windows.keys.filter { !excluded.contains($0) && windows[$0]?.isShown == true && windows[$0]?.screen == screen }
        let others = otherIds.compactMap { windows[$0]?.disc }
        let radii = children.compactMap { windows[$0]?.radius }
        let layout = OrbPlacement.splitLayout(origin: first.disc, radii: radii, visible: visible, others: others,
                                              dockedEdge: OrbPlacement.dockedEdge(of: first.disc, visible: visible))
        for (index, center) in layout.pushed {
            guard index < otherIds.count else { continue }
            windows[otherIds[index]]?.move(to: center, persist: true)
        }
        let reduceMotion = self.reduceMotion
        first.pop(from: 0.85)
        model.orbs.runtime(original)?.drawBezelOn(reduceMotion: reduceMotion)
        first.showChipBriefly(after: reduceMotion ? 0 : 0.28)
        for (index, id) in children.enumerated() {
            guard let window = windows[id], index < layout.children.count else { continue }
            let delay = reduceMotion ? 0 : 0.06 * Double(index + 1)
            let target = layout.children[index]
            prepareShown(window)
            window.animateEntrance(from: first.center, to: target, delay: delay)
            orbSettled(id, center: target, radius: window.radius, on: screen)
            model.orbs.runtime(id)?.drawBezelOn(delay: delay, reduceMotion: reduceMotion)
            window.showChipBriefly(after: reduceMotion ? 0 : delay + 0.45 + 0.28)
        }
    }

    /// The window shows (if its orb is visible) before it animates in.
    private func prepareShown(_ window: OrbWindowController) {
        let orb = model.orbs.orb(window.id)
        let shown = model.settings.orbsVisible && (orb?.visible ?? true)
        window.setShown(shown)
    }

    /// Undo: every orb returns to the position of the restored layout.
    private func restoreLayout() {
        sync()
        var placed: [OrbDisc] = []
        for orb in model.orbs.orbs {
            guard let window = windows[orb.id] else { continue }
            let center = launchCenter(for: orb, placed: placed)
            if window.isShown {
                window.move(to: center, persist: false)
            } else {
                window.jump(to: center)
            }
            placed.append(OrbDisc(center: center, radius: window.radius))
        }
        applyVisibility()
    }

    private func resetPosition(_ id: String) {
        guard let window = windows[id], let screen = Self.primaryScreen else { return }
        let visible = screen.visibleFrame
        let others = otherDiscs(excluding: id, on: screen)
        var target = OrbSnap.defaultCenter(radius: window.radius, in: visible)
        if !OrbPlacement.isFree(target, radius: window.radius, others: others) {
            let blocker = others.first { !OrbPlacement.isClear(target, radius: window.radius, from: $0) }
            target = OrbPlacement.placeNew(radius: window.radius, near: blocker, others: others, visible: visible)
        }
        window.move(to: target, persist: true)
    }

    // MARK: Displays

    private static var primaryScreen: NSScreen? { NSScreen.main ?? NSScreen.screens.first }

    private static func screen(with uuid: String) -> NSScreen? {
        NSScreen.screens.first { $0.orbDisplayUUID == uuid }
    }

    /// Display arrangement or parameters changed (DESIGN 2b, Arrangement changes): each orb takes its record from the
    /// new arrangement's layout, else keeps its position when its display is still connected, else moves to the main
    /// display keeping its anchors; overlaps are resolved in list order. Nothing is ever placed off-screen.
    private func screensChanged() {
        guard !NSScreen.screens.isEmpty else { return }
        let next = OrbsController.arrangementKey
        let previous = arrangement
        arrangement = next
        let connected = OrbsController.connectedDisplays
        let main = OrbsController.mainDisplay ?? connected.first ?? ""
        let entries = OrbLayoutResolver.entries(document: model.orbs.document, arrangement: next,
                                                previousArrangement: previous == next ? nil : previous,
                                                connected: connected, main: main)
        var placed: [OrbDisc] = []
        for orb in model.orbs.orbs {
            guard let window = windows[orb.id] else { continue }
            var center: CGPoint
            var visible: CGRect
            if let entry = entries[orb.id] ?? nil, let screen = Self.screen(with: entry.display) ?? Self.primaryScreen {
                visible = screen.visibleFrame
                center = entry.record.center(radius: window.radius, visible: visible)
            } else if let screen = window.screen ?? Self.primaryScreen {
                visible = screen.visibleFrame
                center = OrbSnap.clamped(window.center, radius: window.radius, in: visible)
            } else {
                continue
            }
            if !OrbPlacement.isFree(center, radius: window.radius, others: placed) {
                center = OrbPlacement.resolveOverlap(center, radius: window.radius, others: placed, visible: visible)
            }
            placed.append(OrbDisc(center: center, radius: window.radius))
            window.move(to: center, persist: false)
        }
        nameplates.setNeedsLayout()
    }
}

// MARK: - OrbWindowManaging

extension OrbWindowManager: OrbWindowManaging {
    func otherDiscs(excluding id: String, on screen: NSScreen) -> [OrbDisc] {
        windows.values.compactMap { window in
            guard window.id != id, window.isShown, window.screen == screen else { return nil }
            return window.disc
        }
    }

    func boardShapes(on screen: NSScreen) -> [FloatingShape] {
        boardShapes().filter { screen.frame.contains($0.center) }
    }

    func orbSettled(_ id: String, center: CGPoint, radius: CGFloat, on screen: NSScreen) {
        guard let display = screen.orbDisplayUUID else { return }
        let record = OrbPositionRecord(center: center, radius: radius, visible: screen.visibleFrame)
        model.orbs.setPlacement(OrbLayoutEntry(display: display, record: record), of: id, arrangement: arrangement)
    }

    func orbInteracted(_ id: String) {
        guard let window = windows[id] else { return }
        window.orderFront()
        OrbFrameClock.shared.focused = ObjectIdentifier(window.host.canvas)
        if undoChip.isShown { undoChip.panel.orderFrontRegardless() }
        if plateUndoChip.isShown { plateUndoChip.panel.orderFrontRegardless() }
    }

    func orbHoverChanged(_ id: String, hovered: Bool) {
        guard hovered, let window = windows[id] else { return }
        OrbFrameClock.shared.focused = ObjectIdentifier(window.host.canvas)
    }

    func showGuides(_ guides: [OrbGuide], on screen: NSScreen?) {
        self.guides.show(guides, on: screen)
    }

    func orbMoved(_ id: String) {
        guard model.orbs.undoBanner?.anchorOrb == id else { return }
        if undoChip.isShown { undoChip.reposition { [weak self] size in self?.undoChipFrame(size) ?? .zero } }
        if plateUndoChip.isShown { plateUndoChip.reposition { [weak self] size in self?.plateUndoChipFrame(size) ?? .zero } }
    }

    func nameplatesNeedLayout() {
        nameplates.setNeedsLayout()
    }

    // MARK: Undo chip

    /// The desktop undo chip under the first resulting orb of a split, merge or remove, for 8 s (DESIGN 2b); beyond
    /// that orb's name plate, in the plate's metrics and material, while it shows one.
    private func updateUndoChip() {
        let orbs = model.orbs
        guard let banner = orbs.undoBanner, let anchor = banner.anchorOrb.flatMap({ windows[$0] }) ?? windows.values.first,
              anchor.isShown else {
            undoChip.hide()
            plateUndoChip.hide()
            return
        }
        let l10n = model.l10n
        let message = orbs.undoMessage(l10n)
        if anchor.nameplate.restFrame != nil, let metrics = anchor.nameplate.content?.metrics {
            undoChip.hide()
            plateUndoChip.show(message: message, action: l10n("orbs.undo"), metrics: metrics, tokens: anchor.nameplate.tokens,
                               level: anchor.level, onAction: { [weak orbs] in orbs?.undo() }) { [weak self] size in
                self?.plateUndoChipFrame(size) ?? .zero
            }
            return
        }
        plateUndoChip.hide()
        let view = OrbChipView(items: [OrbChipItem(id: "undo", color: nil, text: message, secondary: false)],
                               trailing: (l10n("orbs.undo"), { [weak orbs] in orbs?.undo() }))
        undoChip.show(view, level: anchor.level, appearance: anchor.host.effectiveAppearance) { [weak self] size in
            self?.undoChipFrame(size) ?? .zero
        }
    }

    private func undoChipFrame(_ size: CGSize) -> CGRect {
        guard let anchorId = model.orbs.undoBanner?.anchorOrb, let anchor = windows[anchorId] ?? windows.values.first,
              let screen = anchor.screen else { return CGRect(origin: .zero, size: size) }
        let others = otherDiscs(excluding: anchor.id, on: screen)
        return OrbChipPlacement.frame(size: size, disc: anchor.disc, visible: screen.visibleFrame, others: others, stack: 22)
    }

    /// 4 pt beyond the anchor's plate: above it when the plate is above, else under it; inside the usable area.
    private func plateUndoChipFrame(_ size: CGSize) -> CGRect {
        guard let anchorId = model.orbs.undoBanner?.anchorOrb, let anchor = windows[anchorId] ?? windows.values.first,
              let plate = anchor.nameplate.restFrame, let screen = anchor.screen else { return CGRect(origin: .zero, size: size) }
        let gap = OrbNameplateUndoChip.stackGap
        let y = anchor.nameplate.restSide == .above ? plate.maxY + gap : plate.minY - gap - size.height
        let area = screen.visibleFrame.insetBy(dx: NameplateMetrics.areaInset, dy: NameplateMetrics.areaInset)
        let x = min(max(plate.midX - size.width / 2, area.minX), max(area.minX, area.maxX - size.width))
        return CGRect(x: x, y: min(max(y, area.minY), max(area.minY, area.maxY - size.height)), width: size.width, height: size.height)
    }
}
