import CoreGraphics
import Foundation

/// The instrument a floating orb draws (DESIGN 2a). Unknown values from a newer version decode as `led`.
public enum OrbStyleKind: String, CaseIterable, Codable, Sendable, Identifiable {
    case led, needle, liquid, reactor, minimal, character

    public var id: String { rawValue }

    public init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = OrbStyleKind(rawValue: raw) ?? .led
    }

    public var titleKey: String { "style.\(rawValue)" }
    public var descriptionKey: String { "style.\(rawValue).desc" }

    /// The disc the style occupies, × the orb size S: the character stands in a disc twice as wide (DESIGN 2a.6).
    /// Placement, snapping, magnets and the window follow this disc.
    public var footprint: Double { self == .character ? 2 : 1 }

    /// Where a click lands on the orb, in its footprint disc (DESIGN 2a.6).
    public var hitShape: OrbHitShape { self == .character ? .character : .disc }
}

/// The clickable part of an orb's footprint disc of diameter D.
public enum OrbHitShape: Equatable, Sendable {
    /// The whole disc.
    case disc
    /// An upright capsule around the character's body: 0.42 D wide, 0.9 D tall, its center 0.04 D below the disc's.
    case character

    public static let characterWidth: CGFloat = 0.42
    public static let characterHeight: CGFloat = 0.9
    public static let characterDrop: CGFloat = 0.04

    /// Whether a point given relative to the disc center (y up, as in AppKit) is inside, with `tolerance` points of slack.
    public func contains(dx: CGFloat, dy: CGFloat, diameter d: CGFloat, tolerance: CGFloat = 0) -> Bool {
        switch self {
        case .disc:
            return hypot(dx, dy) <= d / 2 + tolerance
        case .character:
            let radius = Self.characterWidth * d / 2 + tolerance
            let half = max(0, Self.characterHeight * d / 2 + tolerance - radius)
            let y = dy + Self.characterDrop * d
            let nearest = min(max(y, -half), half)
            return hypot(dx, y - nearest) <= radius
        }
    }

    /// The shape's bounding box relative to the disc center (y up).
    public func bounds(diameter d: CGFloat) -> CGRect {
        switch self {
        case .disc:
            return CGRect(x: -d / 2, y: -d / 2, width: d, height: d)
        case .character:
            let w = Self.characterWidth * d, h = Self.characterHeight * d
            return CGRect(x: -w / 2, y: -h / 2 - Self.characterDrop * d, width: w, height: h)
        }
    }
}

/// Size and opacity limits shared by every orb (DESIGN 2, 2b, 5).
public enum OrbSizes {
    /// S / M / L presets of the context menu and Settings.
    public static let presets: [Double] = [112, 148, 196]
    public static let `default`: Double = 148
    /// Slider step in Settings and the context menu.
    public static let step: Double = 4
    /// One range for every style, so changing the style never resizes an orb.
    public static let range: ClosedRange<Double> = 64...240
    public static let opacityRange: ClosedRange<Double> = 0.4...1

    public static func clamp(_ size: Double) -> Double {
        (size.isFinite ? size : self.default).clampedToRange(range)
    }
}
