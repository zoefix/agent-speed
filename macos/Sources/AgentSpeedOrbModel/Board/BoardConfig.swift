import Foundation

/// One leaderboard board (DESIGN 2c, Data model). Decoding is tolerant like an orb's: missing fields take their
/// defaults, unknown style and size fall back, and the opacity is clamped.
public struct BoardConfig: Equatable, Hashable, Identifiable, Sendable {
    /// UUID string, never reused.
    public var id: String
    public var style: BoardStyle
    public var size: BoardSize
    public var sources: OrbSourceSelection
    public var opacity: Double
    public var alwaysOnTop: Bool
    public var clickThrough: Bool
    /// Fade when idle (DESIGN 2, 2c); off for a new board and for one saved before the switch existed.
    public var fadeWhenIdle: Bool
    /// macOS only.
    public var allSpaces: Bool
    public var visible: Bool
    /// The live metrics switches (DESIGN 2c Live metrics); every one on for a new board.
    public var readouts: BoardReadouts
    /// The hosts it tracks (DESIGN 13); absent in the document = all.
    public var hosts: OrbHostSelection

    public init(
        id: String,
        style: BoardStyle = .neon,
        size: BoardSize = .medium,
        sources: OrbSourceSelection = .all,
        opacity: Double = 1,
        alwaysOnTop: Bool = true,
        clickThrough: Bool = false,
        fadeWhenIdle: Bool = false,
        allSpaces: Bool = true,
        visible: Bool = true,
        readouts: BoardReadouts = .all,
        hosts: OrbHostSelection = .all
    ) {
        self.id = id
        self.style = style
        self.size = size
        self.sources = sources
        self.opacity = OrbConfig.cleanOpacity(opacity)
        self.alwaysOnTop = alwaysOnTop
        self.clickThrough = clickThrough
        self.fadeWhenIdle = fadeWhenIdle
        self.allSpaces = allSpaces
        self.visible = visible
        self.readouts = readouts
        self.hosts = hosts
    }
}

extension BoardConfig: Codable {
    enum CodingKeys: String, CodingKey {
        case id, style, size, sources, opacity, alwaysOnTop, clickThrough, fadeWhenIdle, allSpaces, visible, readouts, hosts
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
            style: value(BoardStyle.self, .style) ?? .glass,
            size: value(BoardSize.self, .size) ?? .medium,
            sources: value(OrbSourceSelection.self, .sources) ?? .all,
            opacity: value(Double.self, .opacity) ?? 1,
            alwaysOnTop: value(Bool.self, .alwaysOnTop) ?? true,
            clickThrough: value(Bool.self, .clickThrough) ?? false,
            fadeWhenIdle: value(Bool.self, .fadeWhenIdle) ?? false,
            allSpaces: value(Bool.self, .allSpaces) ?? true,
            visible: value(Bool.self, .visible) ?? true,
            readouts: value(BoardReadouts.self, .readouts) ?? .all,
            hosts: value(OrbHostSelection.self, .hosts) ?? .all
        )
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(id, forKey: .id)
        try container.encode(style, forKey: .style)
        try container.encode(size, forKey: .size)
        try container.encode(sources, forKey: .sources)
        try container.encode(opacity, forKey: .opacity)
        try container.encode(alwaysOnTop, forKey: .alwaysOnTop)
        try container.encode(clickThrough, forKey: .clickThrough)
        try container.encode(fadeWhenIdle, forKey: .fadeWhenIdle)
        try container.encode(allSpaces, forKey: .allSpaces)
        try container.encode(visible, forKey: .visible)
        try container.encode(readouts, forKey: .readouts)
        if !hosts.isAll { try container.encode(hosts, forKey: .hosts) }
    }
}
