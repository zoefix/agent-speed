import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// The *Hosts* submenu of the orb and board context menus (DESIGN 13): `remote.hosts.all`, then every host with its
/// status dot and a check when tracked; the last tracked host cannot be unchecked. Only while remote hosts exist.
@MainActor
enum HostMenus {
    static func item(model: AppModel, all: Bool, entries: [OrbHostEntry],
                     setAll: @escaping @MainActor () -> Void,
                     toggle: @escaping @MainActor (String) -> Void) -> NSMenuItem {
        let l10n = model.l10n
        let item = NSMenuItem(title: l10n("remote.hosts"), action: nil, keyEquivalent: "")
        let submenu = NSMenu()
        submenu.autoenablesItems = false
        submenu.addItem(ActionMenuItem(l10n("remote.hosts.all"), state: all ? .on : .off, handler: setAll))
        submenu.addItem(.separator())
        for entry in entries {
            let id = entry.id
            let row = ActionMenuItem(entry.name, state: entry.checked ? .on : .off) { toggle(id) }
            row.image = model.hostDotKind(id).menuImage
            if !entry.enabled {
                row.isEnabled = false
                row.toolTip = l10n("remote.hosts.lastOne")
            }
            submenu.addItem(row)
        }
        item.submenu = submenu
        return item
    }
}
