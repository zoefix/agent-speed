import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// The floating orbs' native menus (DESIGN 2b): the per-orb context menu and the orb items of the quick menu. Built
/// on demand, so they are always in the current language and state.
@MainActor
enum OrbMenus {
    // MARK: Context menu

    static func contextMenu(model: AppModel, orbId id: String) -> NSMenu {
        let l10n = model.l10n
        let orbs = model.orbs
        let menu = NSMenu()
        menu.autoenablesItems = false
        guard let orb = orbs.orb(id) else { return menu }

        let header = NSMenuItem(title: orbs.displayName(of: id), action: nil, keyEquivalent: "")
        header.image = dotsImage(orbs.dots(of: id))
        header.isEnabled = false
        menu.addItem(header)
        menu.addItem(.separator())
        menu.addItem(styleItem(model: model, orb: orb))
        if orb.style == .character { menu.addItem(characterItem(model: model, orb: orb)) }
        menu.addItem(sourcesItem(model: model, orb: orb))
        let hosts = model.hostChoices
        if OrbHostChoice.pickerShown(hosts) {
            menu.addItem(HostMenus.item(model: model, all: orb.hosts.isAll, entries: orb.hosts.entries(hosts),
                                        setAll: { [weak model] in model?.orbs.setHosts(.all, of: id) },
                                        toggle: { [weak model] host in model?.orbs.toggleHost(host, of: id) }))
        }
        addReadoutItems(to: menu, model: model, orb: orb)
        menu.addItem(showNameItem(model: model, orb: orb))
        menu.addItem(sizeItem(model: model, orb: orb))
        menu.addItem(MenuFactory.effectsItem(model: model))

        menu.addItem(.separator())
        menu.addItem(ActionMenuItem(l10n("menu.alwaysOnTop"), state: orb.alwaysOnTop ? .on : .off) { [weak orbs] in
            orbs?.setAlwaysOnTop(!orb.alwaysOnTop, of: id)
        })
        menu.addItem(ActionMenuItem(l10n("menu.clickThrough"), state: orb.clickThrough ? .on : .off) { [weak orbs] in
            orbs?.setClickThrough(!orb.clickThrough, of: id)
        })

        menu.addItem(.separator())
        let limit = l10n("orbs.limit", OrbsDocument.maxOrbs)
        let add = ActionMenuItem(l10n("orbs.add")) { [weak orbs] in orbs?.add(copying: id, showChip: true) }
        add.isEnabled = orbs.canAdd
        if !orbs.canAdd { add.toolTip = limit }
        menu.addItem(add)
        let duplicate = ActionMenuItem(l10n("menu.orb.duplicate")) { [weak orbs] in orbs?.duplicate(id, showChip: true) }
        duplicate.isEnabled = orbs.canAdd
        if !orbs.canAdd { duplicate.toolTip = limit }
        menu.addItem(duplicate)
        let split = ActionMenuItem(l10n("menu.orb.split")) { [weak orbs] in orbs?.split(id) }
        split.isEnabled = orbs.canSplit(id)
        if !orbs.canAdd { split.toolTip = limit }
        menu.addItem(split)
        let merge = ActionMenuItem(l10n("menu.orb.mergeAll")) { [weak orbs] in orbs?.merge(keeping: id) }
        merge.isEnabled = orbs.canMerge
        menu.addItem(merge)
        menu.addItem(BoardMenus.addItem(model: model, origin: .orb(id)))

        menu.addItem(.separator())
        let visibleCount = orbs.orbs.filter(\.visible).count
        if visibleCount <= 1 {
            menu.addItem(ActionMenuItem(l10n("menu.hideOrb")) { [weak model] in model?.commands?.setOrbsVisible(false) })
        } else {
            menu.addItem(ActionMenuItem(l10n("menu.orb.hide")) { [weak orbs] in orbs?.setVisible(false, of: id) })
        }
        if orbs.orbs.count > 1 {
            menu.addItem(ActionMenuItem(l10n("menu.orb.remove")) { [weak model] in
                guard let model else { return }
                confirmRemove(id, model: model)
            })
        }

        menu.addItem(.separator())
        menu.addItem(ActionMenuItem(l10n("menu.orb.settings"), key: ",") { [weak model] in model?.commands?.showOrbSettings(id) })
        menu.addItem(ActionMenuItem(l10n("menu.dashboard")) { [weak model] in model?.commands?.showDashboard() })
        menu.addItem(MenuFactory.quitItem(model: model))
        return menu
    }

    private static func styleItem(model: AppModel, orb: OrbConfig) -> NSMenuItem {
        let item = NSMenuItem(title: model.l10n("orbs.style"), action: nil, keyEquivalent: "")
        let submenu = NSMenu()
        submenu.autoenablesItems = false
        for style in OrbStyleKind.allCases {
            submenu.addItem(ActionMenuItem(model.l10n(style.titleKey), state: orb.style == style ? .on : .off) { [weak model] in
                model?.orbs.setStyle(style, of: orb.id)
            })
        }
        item.submenu = submenu
        return item
    }

    /// `characters.title` ▸ Auto, every installed character, then "Download more…" opening the orb's Settings
    /// (DESIGN 2b, Context menu; CHARACTERS 7).
    private static func characterItem(model: AppModel, orb: OrbConfig) -> NSMenuItem {
        let l10n = model.l10n
        let item = NSMenuItem(title: l10n("characters.title"), action: nil, keyEquivalent: "")
        let submenu = NSMenu()
        submenu.autoenablesItems = false
        submenu.addItem(ActionMenuItem(l10n("characters.auto"), state: orb.character == nil ? .on : .off) { [weak model] in
            model?.orbs.setCharacter(nil, of: orb.id)
        })
        let installed = model.characters.installed
        if !installed.isEmpty { submenu.addItem(.separator()) }
        for pack in installed {
            let name = OrbCharacterSection.name(pack.manifest.name, l10n.language)
            submenu.addItem(ActionMenuItem(name, state: orb.character == pack.id ? .on : .off) { [weak model] in
                model?.orbs.setCharacter(pack.id, of: orb.id)
            })
        }
        submenu.addItem(.separator())
        submenu.addItem(ActionMenuItem(l10n("characters.more")) { [weak model] in model?.commands?.showOrbSettings(orb.id) })
        item.submenu = submenu
        return item
    }

    /// `orbs.sources.all`, then every source with its colour dot and a check when tracked; unavailable sources show
    /// their status and stay selectable; the last tracked source cannot be unchecked (DESIGN 2b).
    private static func sourcesItem(model: AppModel, orb: OrbConfig) -> NSMenuItem {
        let l10n = model.l10n
        let item = NSMenuItem(title: l10n("orbs.sources"), action: nil, keyEquivalent: "")
        let submenu = NSMenu()
        submenu.autoenablesItems = false
        submenu.addItem(ActionMenuItem(l10n("orbs.sources.all"), state: orb.sources.isAll ? .on : .off) { [weak model] in
            model?.orbs.setSources(.all, of: orb.id)
        })
        submenu.addItem(.separator())
        let statuses = Dictionary(uniqueKeysWithValues: model.snapshot.sources.map { ($0.kind, $0) })
        for source in SourceKind.allCases {
            let tracked = !orb.sources.isAll && orb.sources.tracks(source)
            let entry = ActionMenuItem(l10n(source.titleKey), state: tracked ? .on : .off) { [weak model] in
                model?.orbs.toggleSource(source, of: orb.id)
            }
            entry.image = dotsImage([(source, true)])
            if let status = statuses[source], OrbSubset.isUnavailable(status) {
                let reason = l10n(status.enabled ? "source.notFound" : "source.disabled")
                entry.attributedTitle = statusTitle(l10n(source.titleKey), status: reason)
            }
            if tracked, orb.sources.toggling(source) == nil {
                entry.isEnabled = false
                entry.toolTip = l10n("orbs.sources.lastOne")
            }
            submenu.addItem(entry)
        }
        item.submenu = submenu
        return item
    }

    /// The unit and the traffic readout; a `character` orb shows everything at once, so it has its cards instead.
    private static func addReadoutItems(to menu: NSMenu, model: AppModel, orb: OrbConfig) {
        if orb.style == .character {
            menu.addItem(characterReadoutsItem(model: model, orb: orb))
        } else {
            menu.addItem(unitItem(model: model, orb: orb))
            menu.addItem(trafficItem(model: model, orb: orb))
        }
    }

    /// `character.readouts` ▸ a check per card, then the network card's scope: this orb's agents or the whole computer
    /// (DESIGN 2a.6, Readouts). Each switch saves at once; the network items are disabled while traffic is not measured.
    static func characterReadoutsItem(model: AppModel, orb: OrbConfig) -> NSMenuItem {
        let l10n = model.l10n
        let item = NSMenuItem(title: l10n("character.readouts"), action: nil, keyEquivalent: "")
        let submenu = NSMenu()
        submenu.autoenablesItems = false
        let measured = model.settings.trafficEnabled
        for readout in CharacterReadout.allCases {
            let on = orb.characterReadouts[readout]
            let entry = ActionMenuItem(l10n(readout.titleKey), state: on ? .on : .off) { [weak model] in
                model?.orbs.setCharacterReadout(readout, !on, of: orb.id)
            }
            if readout == .network, !measured {
                entry.isEnabled = false
                entry.toolTip = l10n("settings.traffic.enabled")
            }
            submenu.addItem(entry)
        }
        submenu.addItem(.separator())
        submenu.addItem(NSMenuItem.sectionHeader(title: l10n("character.readouts.networkScope")))
        let machine = orb.traffic == .machine
        let unavailable = processTrafficUnavailable(model) ? l10n("traffic.unavailable.short") : nil
        for scope in [OrbTraffic.sources, .machine] {
            let entry = ActionMenuItem(l10n(scope.titleKey), state: (scope == .machine) == machine ? .on : .off) { [weak model] in
                model?.orbs.setTraffic(scope, of: orb.id)
            }
            entry.isEnabled = measured && orb.characterReadouts.network
            if let unavailable, scope == .sources {
                entry.attributedTitle = statusTitle(l10n(scope.titleKey), status: unavailable)
            }
            submenu.addItem(entry)
        }
        item.submenu = submenu
        return item
    }

    /// `orbs.unit` ▸ tok/s or the money unit, check on the current one (DESIGN 9).
    static func unitItem(model: AppModel, orb: OrbConfig) -> NSMenuItem {
        let l10n = model.l10n
        let item = NSMenuItem(title: l10n("orbs.unit"), action: nil, keyEquivalent: "")
        let submenu = NSMenu()
        submenu.autoenablesItems = false
        let titles: [(OrbUnit, String)] = [(.tokens, l10n("orbs.unit.tokens")), (.cost, l10n("orbs.unit.cost", model.cost.symbol))]
        for (unit, title) in titles {
            submenu.addItem(ActionMenuItem(title, state: orb.unit == unit ? .on : .off) { [weak model] in
                model?.orbs.setUnit(unit, of: orb.id)
            })
        }
        item.submenu = submenu
        return item
    }

    /// `orbs.traffic` ▸ this orb's agents / the whole computer / live intelligence / off, check on the current one
    /// (DESIGN 10). The network choices are disabled while traffic is not measured; without per-process data the
    /// `sources` choice, and the item while the orb uses it, show `traffic.unavailable.short`. Intelligence needs
    /// neither, so it is always available.
    static func trafficItem(model: AppModel, orb: OrbConfig) -> NSMenuItem {
        let l10n = model.l10n
        let unavailable = processTrafficUnavailable(model) ? l10n("traffic.unavailable.short") : nil
        let item = NSMenuItem(title: l10n("orbs.traffic"), action: nil, keyEquivalent: "")
        if let unavailable, orb.traffic == .sources {
            item.attributedTitle = statusTitle(l10n("orbs.traffic"), status: unavailable)
        }
        let submenu = NSMenu()
        submenu.autoenablesItems = false
        for traffic in OrbTraffic.allCases {
            let entry = ActionMenuItem(l10n(traffic.titleKey), state: orb.traffic == traffic ? .on : .off) { [weak model] in
                model?.orbs.setTraffic(traffic, of: orb.id)
            }
            entry.isEnabled = !traffic.isNetwork || model.settings.trafficEnabled
            if let unavailable, traffic == .sources {
                entry.attributedTitle = statusTitle(l10n(traffic.titleKey), status: unavailable)
            }
            submenu.addItem(entry)
        }
        item.submenu = submenu
        return item
    }

    /// Measuring is on but per-process data is not (`off` while enabled only means no state is known yet).
    private static func processTrafficUnavailable(_ model: AppModel) -> Bool {
        let state = model.traffic.snapshot.processState
        return model.settings.trafficEnabled && !state.isAvailable && state != .off
    }

    /// `orbs.showName`, checked when the orb's name plate is on; choosing it stores the opposite for this orb
    /// (automatic is restored in Settings).
    static func showNameItem(model: AppModel, orb: OrbConfig) -> NSMenuItem {
        let orbs = model.orbs
        let isOn = NameplateVisibility.isOn(orb, mode: model.settings.orbNames, shownOrbs: orbs.shownOrbs.count)
        return ActionMenuItem(model.l10n("orbs.showName"), state: isOn ? .on : .off) { [weak orbs] in
            orbs?.setShowName(!isOn, of: orb.id)
        }
    }

    /// A menu title followed by a status in small secondary text.
    private static func statusTitle(_ title: String, status: String) -> NSAttributedString {
        let text = NSMutableAttributedString(string: title, attributes: [.font: NSFont.menuFont(ofSize: 0)])
        text.append(NSAttributedString(string: "  " + status, attributes: [
            .font: NSFont.menuFont(ofSize: NSFont.smallSystemFontSize),
            .foregroundColor: NSColor.secondaryLabelColor,
        ]))
        return text
    }

    /// `menu.size` ▸ the S / M / L presets, a separator and the continuous size slider (DESIGN 2b).
    private static func sizeItem(model: AppModel, orb: OrbConfig) -> NSMenuItem {
        let l10n = model.l10n
        let item = NSMenuItem(title: l10n("menu.size"), action: nil, keyEquivalent: "")
        let submenu = NSMenu()
        submenu.autoenablesItems = false
        func state(_ preset: OrbSizePreset, _ size: Double) -> NSControl.StateValue {
            abs(size - preset.diameter) < 0.5 ? .on : .off
        }
        let presets = OrbSizePreset.allCases.map { preset in
            ActionMenuItem(l10n(preset.titleKey), state: state(preset, orb.size)) { [weak model] in
                model?.orbs.setSize(preset.diameter, of: orb.id)
            }
        }
        presets.forEach(submenu.addItem)
        submenu.addItem(.separator())
        let slider = OrbSizeSliderItem(size: orb.size, label: l10n("settings.orb.size"),
                                       valueText: { l10n("mac.settings.orb.sizeValue", $0) }) { [weak model] size in
            model?.orbs.setSize(size, of: orb.id)
            for (item, preset) in zip(presets, OrbSizePreset.allCases) { item.state = state(preset, size) }
        }
        submenu.addItem(slider)
        item.submenu = submenu
        return item
    }

    /// The menu asks before removing (it has no undo banner of its own; the desktop undo chip still appears).
    static func confirmRemove(_ id: String, model: AppModel) {
        let l10n = model.l10n
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = l10n("orbs.remove.confirm", model.orbs.displayName(of: id))
        alert.informativeText = l10n("orbs.remove.detail")
        let remove = alert.addButton(withTitle: l10n("orbs.remove.button"))
        remove.hasDestructiveAction = true
        alert.addButton(withTitle: l10n("history.cancel"))
        NSApp.activate()
        if alert.runModal() == .alertFirstButtonReturn {
            model.orbs.remove(id)
        }
    }

    // MARK: Quick menu

    /// With one orb, its unit / traffic / name / size / always-on-top / click-through items act on it; with several, a
    /// `settings.orb` submenu lists each orb with Show, Click-through, unit, traffic, name and its settings — the way
    /// back to a click-through orb.
    static func addQuickItems(to menu: NSMenu, model: AppModel) {
        let l10n = model.l10n
        let orbs = model.orbs
        guard orbs.orbs.count > 1 else {
            guard let orb = orbs.orbs.first else { return }
            addReadoutItems(to: menu, model: model, orb: orb)
            menu.addItem(showNameItem(model: model, orb: orb))
            menu.addItem(sizeItem(model: model, orb: orb))
            menu.addItem(ActionMenuItem(l10n("menu.alwaysOnTop"), state: orb.alwaysOnTop ? .on : .off) { [weak orbs] in
                orbs?.setAlwaysOnTop(!orb.alwaysOnTop, of: orb.id)
            })
            menu.addItem(ActionMenuItem(l10n("menu.clickThrough"), state: orb.clickThrough ? .on : .off) { [weak orbs] in
                orbs?.setClickThrough(!orb.clickThrough, of: orb.id)
            })
            return
        }
        let item = NSMenuItem(title: l10n("settings.orb"), action: nil, keyEquivalent: "")
        let submenu = NSMenu()
        submenu.autoenablesItems = false
        for orb in orbs.orbs {
            let entry = NSMenuItem(title: orbs.displayName(of: orb.id), action: nil, keyEquivalent: "")
            entry.image = dotsImage(orbs.dots(of: orb.id))
            let actions = NSMenu()
            actions.autoenablesItems = false
            actions.addItem(ActionMenuItem(l10n("orbs.visible"), state: orb.visible && orbs.masterVisible ? .on : .off) { [weak orbs] in
                guard let orbs else { return }
                let shown = orb.visible && orbs.masterVisible
                orbs.setVisible(!shown, of: orb.id)
            })
            actions.addItem(ActionMenuItem(l10n("menu.clickThrough"), state: orb.clickThrough ? .on : .off) { [weak orbs] in
                orbs?.setClickThrough(!orb.clickThrough, of: orb.id)
            })
            addReadoutItems(to: actions, model: model, orb: orb)
            actions.addItem(showNameItem(model: model, orb: orb))
            actions.addItem(.separator())
            actions.addItem(ActionMenuItem(l10n("menu.orb.settings")) { [weak model] in model?.commands?.showOrbSettings(orb.id) })
            entry.submenu = actions
            submenu.addItem(entry)
        }
        item.submenu = submenu
        menu.addItem(item)
    }

    // MARK: Dots

    /// Colour dots of an orb's sources (6 pt, 3 pt apart); unavailable ones faded. Nil for all sources.
    static func dotsImage(_ dots: [(source: SourceKind, available: Bool)]) -> NSImage? {
        guard !dots.isEmpty else { return nil }
        let diameter: CGFloat = 6
        let spacing: CGFloat = 3
        let width = CGFloat(dots.count) * diameter + CGFloat(dots.count - 1) * spacing
        let image = NSImage(size: NSSize(width: width, height: 10), flipped: false) { _ in
            for (index, dot) in dots.enumerated() {
                dot.source.tint.nsColor.withAlphaComponent(dot.available ? 1 : 0.35).setFill()
                NSBezierPath(ovalIn: NSRect(x: CGFloat(index) * (diameter + spacing), y: 2, width: diameter, height: diameter)).fill()
            }
            return true
        }
        image.accessibilityDescription = nil
        return image
    }
}
