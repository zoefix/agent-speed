import AgentSpeedCore
import Foundation

/// The sources that can show data right now.
public struct OrbAvailability: Equatable, Sendable {
    /// Detected and enabled sources, in source order.
    public var detected: [SourceKind]

    public init(detected: [SourceKind]) {
        let set = Set(detected)
        self.detected = SourceKind.allCases.filter(set.contains)
    }

    /// The available members of a selection (every detected source for `.all`), in source order.
    public func available(_ selection: OrbSourceSelection) -> [SourceKind] {
        selection.isAll ? detected : detected.filter(selection.tracks)
    }
}

/// Document operations (DESIGN 2b). Each one is pure; the app keeps the previous document to undo split, merge and
/// remove for 8 s.
public enum OrbEditing {
    // MARK: Creating and removing

    /// Adds an orb after `sourceId` (or the first orb) with its style, size, opacity, toggles and title theme, tracking the first
    /// detected, enabled source no orb tracks explicitly, else all sources. Nil at eight orbs.
    public static func add(to doc: OrbsDocument, copying sourceId: String?, availability: OrbAvailability, newId: String) -> OrbsDocument? {
        guard doc.canAdd, let template = sourceId.flatMap(doc.orb) ?? doc.orbs.first else { return nil }
        let tracked = Set(doc.orbs.flatMap(\.sources.knownSources))
        let sources: OrbSourceSelection = availability.detected.first { !tracked.contains($0) }.map { .explicit([$0]) } ?? .all
        let orb = OrbConfig(
            id: newId,
            sources: sources,
            style: template.style,
            size: template.size,
            opacity: template.opacity,
            alwaysOnTop: template.alwaysOnTop,
            clickThrough: template.clickThrough,
            fadeWhenIdle: template.fadeWhenIdle,
            allSpaces: template.allSpaces,
            visible: true,
            titleTheme: template.titleTheme,
            unit: template.unit,
            traffic: template.traffic,
            showName: template.showName,
            character: template.character,
            characterReadouts: template.characterReadouts
        )
        return inserting(orb, after: template.id, in: doc)
    }

    /// An exact copy after the original with a new id; a custom name gets ` 2`. Nil at eight orbs.
    public static func duplicate(_ id: String, in doc: OrbsDocument, newId: String) -> OrbsDocument? {
        guard doc.canAdd, var copy = doc.orb(id) else { return nil }
        copy.id = newId
        copy.name = OrbNamer.duplicateName(copy.name)
        return inserting(copy, after: id, in: doc)
    }

    /// Nil when `id` is the last orb (it cannot be removed; the master switch hides it) or unknown.
    public static func remove(_ id: String, from doc: OrbsDocument) -> OrbsDocument? {
        guard doc.orbs.count > 1, doc.orb(id) != nil else { return nil }
        var next = doc
        next.removeOrb(id)
        return next
    }

    // MARK: Split and merge

    /// "Split this orb": one orb per available source of its set, in source order. The first keeps the orb's id
    /// (and so its position) and tracks the first source; the others follow it in the list with the same style,
    /// size, opacity, toggles and redlines and automatic names. Every resulting orb has an automatic name. Limited to
    /// eight orbs in total (`limited`); nil with fewer than two available sources.
    public static func split(
        _ id: String,
        in doc: OrbsDocument,
        availability: OrbAvailability,
        newIds: [String]
    ) -> (document: OrbsDocument, created: [String], limited: Bool)? {
        guard let orb = doc.orb(id) else { return nil }
        let sources = availability.available(orb.sources)
        guard sources.count >= 2 else { return nil }
        let room = OrbsDocument.maxOrbs - doc.orbs.count + 1
        let count = min(sources.count, room, newIds.count + 1)
        guard count >= 2 else { return nil }
        var next = doc
        var first = orb
        first.sources = .explicit([sources[0]])
        first.name = nil
        replace(first, in: &next)
        var created: [String] = []
        var anchor = id
        for (offset, source) in sources[1..<count].enumerated() {
            var child = orb
            child.id = newIds[offset]
            child.name = nil
            child.sources = .explicit([source])
            child.visible = true
            next = inserting(child, after: anchor, in: next)
            anchor = child.id
            created.append(child.id)
        }
        return (next, created, count < sources.count)
    }

    /// Settings → Split by source: all orbs are replaced by one per detected, enabled source, styled like the
    /// selected orb. The first reuses the first orb's id and position; `ids` supplies the others. Nil with fewer than
    /// two detected sources.
    public static func splitBySource(
        _ doc: OrbsDocument,
        selectedId: String?,
        availability: OrbAvailability,
        ids: [String]
    ) -> (document: OrbsDocument, limited: Bool)? {
        let sources = availability.detected
        guard sources.count >= 2, let firstOrb = doc.orbs.first else { return nil }
        let template = selectedId.flatMap(doc.orb) ?? firstOrb
        let count = min(sources.count, OrbsDocument.maxOrbs, ids.count + 1)
        guard count >= 2 else { return nil }
        var next = doc
        for orb in doc.orbs where orb.id != firstOrb.id { next.removeOrb(orb.id) }
        var orbs: [OrbConfig] = []
        for (index, source) in sources.prefix(count).enumerated() {
            var orb = template
            orb.id = index == 0 ? firstOrb.id : ids[index - 1]
            orb.name = nil
            orb.sources = .explicit([source])
            orb.visible = true
            orbs.append(orb)
        }
        next.orbs = orbs
        return (next, count < sources.count)
    }

    /// Merge: every orb is replaced by one all-sources orb that keeps `id`'s id (and position), style, size,
    /// opacity, toggles and redlines; its name becomes automatic. Nil with a single orb.
    public static func merge(_ doc: OrbsDocument, keeping id: String) -> OrbsDocument? {
        guard doc.orbs.count > 1, var kept = doc.orb(id) else { return nil }
        var next = doc
        for orb in doc.orbs where orb.id != id { next.removeOrb(orb.id) }
        kept.sources = .all
        kept.name = nil
        kept.visible = true
        next.orbs = [kept]
        return next
    }

    // MARK: Editing one orb

    public static func setSources(_ selection: OrbSourceSelection, of id: String, in doc: OrbsDocument) -> OrbsDocument {
        update(id, in: doc) { $0.sources = selection }
    }

    /// The hosts an orb counts (DESIGN 13).
    public static func setHosts(_ selection: OrbHostSelection, of id: String, in doc: OrbsDocument) -> OrbsDocument {
        update(id, in: doc) { $0.hosts = selection }
    }

    /// A host removed from the settings leaves every orb's selection; one left without hosts tracks all (REMOTE 2.4).
    public static func removingHost(_ hostId: String, from doc: OrbsDocument) -> OrbsDocument {
        var next = doc
        for index in next.orbs.indices { next.orbs[index].hosts = next.orbs[index].hosts.removing(hostId) }
        return next
    }

    /// Changes the style; the size stays, since every style shares one range.
    public static func setStyle(_ style: OrbStyleKind, of id: String, in doc: OrbsDocument) -> OrbsDocument {
        update(id, in: doc) { $0.style = style }
    }

    /// A pack id, or nil to follow the fastest model vendor (CHARACTERS 7).
    public static func setCharacter(_ character: String?, of id: String, in doc: OrbsDocument) -> OrbsDocument {
        update(id, in: doc) { $0.character = OrbConfig.cleanCharacter(character) }
    }

    /// One card of the `character` style (DESIGN 2a.6, Readouts).
    public static func setCharacterReadout(_ readout: CharacterReadout, _ on: Bool, of id: String,
                                           in doc: OrbsDocument) -> OrbsDocument {
        update(id, in: doc) { $0.characterReadouts[readout] = on }
    }

    public static func setSize(_ size: Double, of id: String, in doc: OrbsDocument) -> OrbsDocument {
        update(id, in: doc) { $0.size = OrbSizes.clamp(size) }
    }

    public static func setOpacity(_ opacity: Double, of id: String, in doc: OrbsDocument) -> OrbsDocument {
        update(id, in: doc) { $0.opacity = OrbConfig.cleanOpacity(opacity) }
    }

    public static func setToggle(_ key: WritableKeyPath<OrbConfig, Bool>, _ value: Bool, of id: String, in doc: OrbsDocument) -> OrbsDocument {
        update(id, in: doc) { $0[keyPath: key] = value }
    }

    public static func setVisible(_ visible: Bool, of id: String, in doc: OrbsDocument) -> OrbsDocument {
        update(id, in: doc) { $0.visible = visible }
    }

    /// tok/s or the money unit (DESIGN 9).
    public static func setUnit(_ unit: OrbUnit, of id: String, in doc: OrbsDocument) -> OrbsDocument {
        update(id, in: doc) { $0.unit = unit }
    }

    /// The traffic readout (DESIGN 2b, 10).
    public static func setTraffic(_ traffic: OrbTraffic, of id: String, in doc: OrbsDocument) -> OrbsDocument {
        update(id, in: doc) { $0.traffic = traffic }
    }

    /// nil follows the global name plate setting (DESIGN 2b, Name plate).
    public static func setShowName(_ showName: Bool?, of id: String, in doc: OrbsDocument) -> OrbsDocument {
        update(id, in: doc) { $0.showName = showName }
    }

    /// Trimmed and limited; empty means automatic.
    public static func setName(_ name: String?, of id: String, in doc: OrbsDocument) -> OrbsDocument {
        update(id, in: doc) { $0.name = OrbConfig.cleanName(name) }
    }

    /// The orb's title theme: nil follows the General theme (DESIGN 2b `titleTheme`).
    public static func setTitleTheme(_ theme: String?, of id: String, in doc: OrbsDocument) -> OrbsDocument {
        var next = doc
        guard let index = next.index(of: id) else { return doc }
        next.orbs[index].titleTheme = OrbConfig.cleanThemeID(theme)
        return next
    }

    /// Every orb naming the title theme `old` names `replacement` instead (a deleted custom set → its base preset).
    public static func replacingTitleTheme(_ old: String, with replacement: String?, in doc: OrbsDocument) -> OrbsDocument {
        var next = doc
        for index in next.orbs.indices where next.orbs[index].titleTheme == old {
            next.orbs[index].titleTheme = OrbConfig.cleanThemeID(replacement)
        }
        return next
    }

    /// nil follows the General redline of that metric.
    public static func setRedline(_ value: Double?, metric: SpeedMetric, of id: String, in doc: OrbsDocument) -> OrbsDocument {
        update(id, in: doc) { orb in
            switch metric {
            case .output: orb.redlineOutput = OrbConfig.cleanRedline(value)
            case .total: orb.redlineTotal = OrbConfig.cleanRedline(value)
            }
        }
    }

    /// Reorders the list (Settings drag), with the semantics of SwiftUI's `move(fromOffsets:toOffset:)`.
    public static func move(fromOffsets source: IndexSet, toOffset destination: Int, in doc: OrbsDocument) -> OrbsDocument {
        var next = doc
        let valid = source.filter { doc.orbs.indices.contains($0) }
        guard !valid.isEmpty else { return doc }
        let moving = valid.map { doc.orbs[$0] }
        let before = valid.filter { $0 < destination }.count
        var remaining = doc.orbs
        for index in valid.sorted(by: >) { remaining.remove(at: index) }
        let insertAt = min(max(0, destination - before), remaining.count)
        remaining.insert(contentsOf: moving, at: insertAt)
        next.orbs = remaining
        return next
    }

    /// The master switch turned on: when every orb is individually hidden, all of them become visible.
    public static func showAll(_ doc: OrbsDocument) -> OrbsDocument {
        guard !doc.orbs.isEmpty, doc.orbs.allSatisfy({ !$0.visible }) else { return doc }
        var next = doc
        for index in next.orbs.indices { next.orbs[index].visible = true }
        return next
    }

    // MARK: Helpers

    static func update(_ id: String, in doc: OrbsDocument, _ change: (inout OrbConfig) -> Void) -> OrbsDocument {
        guard let index = doc.index(of: id) else { return doc }
        var next = doc
        change(&next.orbs[index])
        return next
    }

    static func replace(_ orb: OrbConfig, in doc: inout OrbsDocument) {
        guard let index = doc.index(of: orb.id) else { return }
        doc.orbs[index] = orb
    }

    static func inserting(_ orb: OrbConfig, after id: String, in doc: OrbsDocument) -> OrbsDocument {
        var next = doc
        let index = doc.index(of: id).map { $0 + 1 } ?? doc.orbs.count
        next.orbs.insert(orb, at: index)
        return next
    }
}
