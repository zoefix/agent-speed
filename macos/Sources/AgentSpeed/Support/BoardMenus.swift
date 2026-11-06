import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// The boards' native menus (DESIGN 2c Interaction and window), built on demand from `BoardMenuModel`: the board's
/// context menu and the board items of the quick menu.
@MainActor
enum BoardMenus {
    // MARK: Context menu

    static func contextMenu(model: AppModel, boardId id: String) -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false
        let boards = model.boards
        guard let board = boards.board(id) else { return menu }
        let statuses = Dictionary(uniqueKeysWithValues: model.snapshot.sources.map { ($0.kind, $0) })
        var status: [SourceKind: String] = [:]
        for (kind, source) in statuses where OrbSubset.isUnavailable(source) {
            status[kind] = source.enabled ? "source.notFound" : "source.disabled"
        }
        for item in BoardMenuModel.context(board, document: boards.document, status: status, hosts: model.hostChoices) {
            menu.addItem(menuItem(item, board: board, model: model))
        }
        return menu
    }

    private static func menuItem(_ item: BoardMenuModel.Item, board: BoardConfig, model: AppModel) -> NSMenuItem {
        let l10n = model.l10n
        let id = board.id
        switch item {
        case .header(let style, _):
            let header = NSMenuItem(title: l10n(style.titleKey) + " · " + model.boards.sourcesText(of: board, l10n: l10n),
                                    action: nil, keyEquivalent: "")
            header.image = OrbMenus.dotsImage(board.sources.knownSources.map { ($0, true) })
            header.isEnabled = false
            return header
        case .styles(let selected):
            return submenu(l10n("boards.style"), BoardStyle.allCases.map { style in
                ActionMenuItem(l10n(style.titleKey), state: style == selected ? .on : .off) { [weak model] in
                    model?.boards.setStyle(style, of: id)
                }
            })
        case .sources(let all, let entries):
            var items: [NSMenuItem] = [
                ActionMenuItem(l10n("orbs.sources.all"), state: all ? .on : .off) { [weak model] in
                    model?.boards.setSources(.all, of: id)
                },
                .separator(),
            ]
            for entry in entries {
                let name = l10n(entry.source.titleKey)
                let source = entry.source
                let row = ActionMenuItem(name, state: entry.checked ? .on : .off) { [weak model] in
                    model?.boards.toggleSource(source, of: id)
                }
                row.image = OrbMenus.dotsImage([(source, true)])
                if let key = entry.statusKey { row.attributedTitle = statusTitle(name, status: l10n(key)) }
                if !entry.enabled {
                    row.isEnabled = false
                    row.toolTip = l10n("orbs.sources.lastOne")
                }
                items.append(row)
            }
            return submenu(l10n("orbs.sources"), items)
        case .hosts(let all, let entries):
            return HostMenus.item(model: model, all: all, entries: entries,
                                  setAll: { [weak model] in model?.boards.setHosts(.all, of: id) },
                                  toggle: { [weak model] host in model?.boards.toggleHost(host, of: id) })
        case .readouts(let readouts):
            // Each switch is independent and saves at once (DESIGN 2c Live metrics, *Switches*).
            return submenu(l10n("boards.readouts"), BoardReadout.allCases.map { readout in
                ActionMenuItem(l10n(readout.titleKey), state: readouts[readout] ? .on : .off) { [weak model] in
                    model?.boards.toggleReadout(readout, of: id)
                }
            })
        case .sizes(let selected):
            return submenu(l10n("menu.size"), BoardSize.allCases.map { size in
                ActionMenuItem(l10n(sizeKey(size)), state: size == selected ? .on : .off) { [weak model] in
                    model?.boards.setSize(size, of: id)
                }
            })
        case .effects:
            return MenuFactory.effectsItem(model: model)
        case .toggle(let toggle, let on):
            return ActionMenuItem(l10n(toggle.rawValue), state: on ? .on : .off) { [weak model] in
                switch toggle {
                case .alwaysOnTop: model?.boards.setAlwaysOnTop(!on, of: id)
                case .clickThrough: model?.boards.setClickThrough(!on, of: id)
                }
            }
        case .add(let enabled):
            return addItem(model: model, enabled: enabled, origin: .board(id))
        case .remove:
            return ActionMenuItem(l10n("boards.remove")) { [weak model] in
                guard let model else { return }
                confirmRemove(id, model: model)
            }
        case .dashboard:
            return ActionMenuItem(l10n("menu.dashboard")) { [weak model] in model?.commands?.showDashboard() }
        case .settings:
            return ActionMenuItem(l10n("boards.settings"), key: ",") { [weak model] in model?.commands?.showBoardSettings(id) }
        case .separator:
            return .separator()
        }
    }

    /// `boards.add`, disabled at the limit with `boards.limit` as its tooltip.
    static func addItem(model: AppModel, enabled: Bool? = nil, origin: BoardAddOrigin) -> NSMenuItem {
        let enabled = enabled ?? model.boards.canAdd
        let item = ActionMenuItem(model.l10n("boards.add")) { [weak model] in model?.commands?.addBoard(from: origin) }
        item.isEnabled = enabled
        if !enabled { item.toolTip = model.l10n("boards.limit", BoardsDocument.maxBoards) }
        return item
    }

    static func sizeKey(_ size: BoardSize) -> String {
        switch size {
        case .small: "menu.size.small"
        case .medium: "menu.size.medium"
        case .large: "menu.size.large"
        }
    }

    /// Asks `boards.remove.confirm` with a destructive `boards.remove` and Cancel.
    static func confirmRemove(_ id: String, model: AppModel) {
        let l10n = model.l10n
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = l10n("boards.remove.confirm")
        let remove = alert.addButton(withTitle: l10n("boards.remove"))
        remove.hasDestructiveAction = true
        alert.addButton(withTitle: l10n("history.cancel"))
        NSApp.activate()
        if alert.runModal() == .alertFirstButtonReturn {
            model.boards.remove(id)
        }
    }

    // MARK: Quick menu

    /// `boards.show` / `boards.hide` for every board while boards exist, then `boards.add`, apart from the orb items.
    static func addQuickItems(to menu: NSMenu, model: AppModel) {
        let l10n = model.l10n
        menu.addItem(.separator())
        for item in BoardMenuModel.quick(model.boards.document) {
            switch item {
            case .visibility(let anyVisible):
                menu.addItem(ActionMenuItem(l10n(anyVisible ? "boards.hide" : "boards.show")) { [weak model] in
                    model?.boards.setAllVisible(!anyVisible)
                })
            case .add(let enabled):
                menu.addItem(addItem(model: model, enabled: enabled, origin: .none))
            }
        }
    }

    // MARK: Helpers

    private static func submenu(_ title: String, _ items: [NSMenuItem]) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        let submenu = NSMenu()
        submenu.autoenablesItems = false
        items.forEach(submenu.addItem)
        item.submenu = submenu
        return item
    }

    private static func statusTitle(_ title: String, status: String) -> NSAttributedString {
        let text = NSMutableAttributedString(string: title, attributes: [.font: NSFont.menuFont(ofSize: 0)])
        text.append(NSAttributedString(string: "  " + status, attributes: [
            .font: NSFont.menuFont(ofSize: NSFont.smallSystemFontSize),
            .foregroundColor: NSColor.secondaryLabelColor,
        ]))
        return text
    }
}
