import AgentSpeedCore
import Foundation

/// One floating orb (DESIGN 2b, Data model). Decoding is tolerant: missing fields take their defaults and
/// out-of-range values are clamped, so a hand-edited or newer document never fails to load.
public struct OrbConfig: Equatable, Hashable, Identifiable, Sendable {
    public static let nameLimit = 32

    /// UUID string, never reused.
    public var id: String
    /// Custom name (1–32 characters, trimmed); nil = automatic.
    public var name: String?
    public var sources: OrbSourceSelection
    public var style: OrbStyleKind
    /// Diameter in points, within the style's range.
    public var size: Double
    public var opacity: Double
    public var alwaysOnTop: Bool
    public var clickThrough: Bool
    public var fadeWhenIdle: Bool
    /// macOS only.
    public var allSpaces: Bool
    public var visible: Bool
    /// nil = the General redline of that metric. Stored as given; the app snaps it to its slider steps.
    public var redlineOutput: Double?
    public var redlineTotal: Double?
    /// nil = the General title theme; else a preset id or a custom set id (DESIGN 2b; an unknown id counts as nil).
    public var titleTheme: String?
    /// tok/s or the money unit (DESIGN 2b, 9).
    public var unit: OrbUnit
    /// The traffic readout (DESIGN 2b, 10).
    public var traffic: OrbTraffic
    /// nil follows the global `orbNames`; true / false shows or hides this orb's name plate (DESIGN 2b, Name plate).
    public var showName: Bool?
    /// The hosts it tracks (DESIGN 13); absent in the document = all.
    public var hosts: OrbHostSelection
    /// The `character` style's character: a pack id, or nil to follow the fastest model vendor (CHARACTERS 7).
    public var character: String?
    /// The cards the `character` style floats beside her (DESIGN 2a.6, Readouts).
    public var characterReadouts: CharacterReadouts

    /// The diameter of the disc the orb occupies: its size × the style's footprint (DESIGN 2a.6).
    public var footprintDiameter: Double { size * style.footprint }

    public init(
        id: String,
        name: String? = nil,
        sources: OrbSourceSelection = .all,
        style: OrbStyleKind = .led,
        size: Double = OrbSizes.default,
        opacity: Double = 1,
        alwaysOnTop: Bool = true,
        clickThrough: Bool = false,
        fadeWhenIdle: Bool = false,
        allSpaces: Bool = true,
        visible: Bool = true,
        redlineOutput: Double? = nil,
        redlineTotal: Double? = nil,
        titleTheme: String? = nil,
        unit: OrbUnit = .tokens,
        traffic: OrbTraffic = .sources,
        showName: Bool? = nil,
        hosts: OrbHostSelection = .all,
        character: String? = nil,
        characterReadouts: CharacterReadouts = .default
    ) {
        self.id = id
        self.name = Self.cleanName(name)
        self.sources = sources
        self.style = style
        self.size = OrbSizes.clamp(size)
        self.opacity = Self.cleanOpacity(opacity)
        self.alwaysOnTop = alwaysOnTop
        self.clickThrough = clickThrough
        self.fadeWhenIdle = fadeWhenIdle
        self.allSpaces = allSpaces
        self.visible = visible
        self.redlineOutput = Self.cleanRedline(redlineOutput)
        self.redlineTotal = Self.cleanRedline(redlineTotal)
        self.titleTheme = Self.cleanThemeID(titleTheme)
        self.unit = unit
        self.traffic = traffic
        self.showName = showName
        self.hosts = hosts
        self.character = Self.cleanCharacter(character)
        self.characterReadouts = characterReadouts
    }

    /// The orb of a fresh install: all sources, `led`, 148 pt, at the default position.
    public static func makeDefault(id: String) -> OrbConfig {
        OrbConfig(id: id)
    }

    /// Trimmed and limited to 32 characters; empty means automatic (nil).
    public static func cleanName(_ name: String?) -> String? {
        guard let trimmed = name?.trimmingCharacters(in: .whitespacesAndNewlines), !trimmed.isEmpty else { return nil }
        guard trimmed.count > nameLimit else { return trimmed }
        return String(trimmed.prefix(nameLimit)).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    public static func cleanOpacity(_ opacity: Double) -> Double {
        opacity.isFinite ? opacity.clampedToRange(OrbSizes.opacityRange) : 1
    }

    static func cleanThemeID(_ id: String?) -> String? {
        guard let trimmed = id?.trimmingCharacters(in: .whitespacesAndNewlines), !trimmed.isEmpty else { return nil }
        return trimmed
    }

    /// A pack id (lowercase letters, digits and dashes, CHARACTERS 1); anything else means automatic (nil).
    public static func cleanCharacter(_ id: String?) -> String? {
        guard let trimmed = id?.trimmingCharacters(in: .whitespacesAndNewlines), CharacterID.isValid(trimmed) else { return nil }
        return trimmed
    }

    static func cleanRedline(_ value: Double?) -> Double? {
        guard let value, value.isFinite, value > 0 else { return nil }
        return value
    }
}

extension OrbConfig: Codable {
    enum CodingKeys: String, CodingKey {
        case id, name, sources, style, size, opacity, alwaysOnTop, clickThrough, fadeWhenIdle, allSpaces, visible
        case redlineOutput, redlineTotal, titleTheme, unit, traffic, showName, hosts, character, characterReadouts
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let id = try container.decode(String.self, forKey: .id).trimmingCharacters(in: .whitespacesAndNewlines)
        guard !id.isEmpty else {
            throw DecodingError.dataCorruptedError(forKey: .id, in: container, debugDescription: "empty id")
        }
        func value<T: Decodable>(_ type: T.Type, _ key: CodingKeys) -> T? {
            (try? container.decodeIfPresent(type, forKey: key)) ?? nil
        }
        self.init(
            id: id,
            name: value(String.self, .name),
            sources: value(OrbSourceSelection.self, .sources) ?? .all,
            style: value(OrbStyleKind.self, .style) ?? .led,
            size: value(Double.self, .size) ?? OrbSizes.default,
            opacity: value(Double.self, .opacity) ?? 1,
            alwaysOnTop: value(Bool.self, .alwaysOnTop) ?? true,
            clickThrough: value(Bool.self, .clickThrough) ?? false,
            fadeWhenIdle: value(Bool.self, .fadeWhenIdle) ?? false,
            allSpaces: value(Bool.self, .allSpaces) ?? true,
            visible: value(Bool.self, .visible) ?? true,
            redlineOutput: value(Double.self, .redlineOutput),
            redlineTotal: value(Double.self, .redlineTotal),
            titleTheme: value(String.self, .titleTheme),
            unit: value(OrbUnit.self, .unit) ?? .tokens,
            traffic: Self.decodeTraffic(container),
            showName: value(Bool.self, .showName),
            hosts: value(OrbHostSelection.self, .hosts) ?? .all,
            character: value(String.self, .character),
            characterReadouts: value(CharacterReadouts.self, .characterReadouts) ?? .default
        )
    }

    /// Absent or null (orbs saved by older builds) → `sources`; unknown → `off` (DESIGN 2b).
    private static func decodeTraffic(_ container: KeyedDecodingContainer<CodingKeys>) -> OrbTraffic {
        guard container.contains(.traffic), (try? container.decodeNil(forKey: .traffic)) == false else { return .sources }
        return (try? container.decode(OrbTraffic.self, forKey: .traffic)) ?? .off
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(id, forKey: .id)
        try container.encode(name, forKey: .name)
        try container.encode(sources, forKey: .sources)
        try container.encode(style, forKey: .style)
        try container.encode(size, forKey: .size)
        try container.encode(opacity, forKey: .opacity)
        try container.encode(alwaysOnTop, forKey: .alwaysOnTop)
        try container.encode(clickThrough, forKey: .clickThrough)
        try container.encode(fadeWhenIdle, forKey: .fadeWhenIdle)
        try container.encode(allSpaces, forKey: .allSpaces)
        try container.encode(visible, forKey: .visible)
        try container.encode(redlineOutput, forKey: .redlineOutput)
        try container.encode(redlineTotal, forKey: .redlineTotal)
        try container.encode(titleTheme, forKey: .titleTheme)
        try container.encode(unit, forKey: .unit)
        try container.encode(traffic, forKey: .traffic)
        try container.encode(showName, forKey: .showName)
        if !hosts.isAll { try container.encode(hosts, forKey: .hosts) }
        if let character { try container.encode(character, forKey: .character) }
        if characterReadouts != .default { try container.encode(characterReadouts, forKey: .characterReadouts) }
    }
}
