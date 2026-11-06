import Foundation

/// The single-orb settings of the versions before multiple orbs (the `orb*` keys and the per-display positions).
public struct LegacyOrbSettings: Equatable, Sendable {
    public var visible: Bool
    public var size: Double
    public var opacity: Double
    public var alwaysOnTop: Bool
    public var clickThrough: Bool
    public var fadeWhenIdle: Bool
    public var allSpaces: Bool
    /// Display key → the orb's saved position there.
    public var positions: [String: OrbPositionRecord]
    /// The display the user last placed the orb on.
    public var lastDisplay: String?

    public init(
        visible: Bool = true,
        size: Double = OrbSizes.default,
        opacity: Double = 1,
        alwaysOnTop: Bool = true,
        clickThrough: Bool = false,
        fadeWhenIdle: Bool = false,
        allSpaces: Bool = true,
        positions: [String: OrbPositionRecord] = [:],
        lastDisplay: String? = nil
    ) {
        self.visible = visible
        self.size = size
        self.opacity = opacity
        self.alwaysOnTop = alwaysOnTop
        self.clickThrough = clickThrough
        self.fadeWhenIdle = fadeWhenIdle
        self.allSpaces = allSpaces
        self.positions = positions
        self.lastDisplay = lastDisplay
    }
}

/// First launch of the multi-orb version (DESIGN 2b, Migration): one all-sources `led` orb with the old size,
/// opacity and toggles, sitting exactly where the old orb would have been placed. The old keys are read only, never
/// written again, so a downgrade restores the single orb.
public enum OrbsMigration {
    /// The single-orb version's sizes: a stored size outside them was drawn at the nearest end.
    static let legacySizes: ClosedRange<Double> = 96...240

    public static func migrate(
        _ legacy: LegacyOrbSettings,
        id: String,
        connectedDisplays: [String],
        mainDisplay: String?
    ) -> (document: OrbsDocument, masterVisible: Bool) {
        let orb = OrbConfig(
            id: id,
            sources: .all,
            style: .led,
            size: legacy.size.isFinite ? legacy.size.clampedToRange(legacySizes) : OrbSizes.default,
            opacity: legacy.opacity,
            alwaysOnTop: legacy.alwaysOnTop,
            clickThrough: legacy.clickThrough,
            fadeWhenIdle: legacy.fadeWhenIdle,
            allSpaces: legacy.allSpaces,
            visible: true
        )
        var document = OrbsDocument(orbs: [orb])
        if !legacy.positions.isEmpty { document.displayRecords[id] = legacy.positions }
        let arrangement = OrbsDocument.arrangementKey(connectedDisplays)
        if let entry = launchEntry(legacy, connectedDisplays: connectedDisplays, mainDisplay: mainDisplay) {
            document.setEntry(entry, orb: id, arrangement: arrangement)
        } else if let mainDisplay {
            // The old app put an orb without a usable record in the default corner of the main display; recording that
            // corner keeps the arrangement resolver from moving another display's record there instead.
            document.setEntry(OrbLayoutEntry(display: mainDisplay, record: .defaultCorner), orb: id, arrangement: arrangement)
        }
        return (document, legacy.visible)
    }

    /// Where the old app put the orb at launch: the last display's record when that display is connected, else the
    /// main display's record; nil means the default corner of the main display (`OrbPositionRecord.defaultCorner`).
    static func launchEntry(_ legacy: LegacyOrbSettings, connectedDisplays: [String], mainDisplay: String?) -> OrbLayoutEntry? {
        if let last = legacy.lastDisplay, connectedDisplays.contains(last), let record = legacy.positions[last] {
            return OrbLayoutEntry(display: last, record: record)
        }
        if let main = mainDisplay, let record = legacy.positions[main] {
            return OrbLayoutEntry(display: main, record: record)
        }
        return nil
    }
}
