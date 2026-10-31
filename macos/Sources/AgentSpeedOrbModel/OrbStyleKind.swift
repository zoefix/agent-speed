import Foundation

/// The instrument a floating orb draws (DESIGN 2a). Unknown values from a newer version decode as `led`.
public enum OrbStyleKind: String, CaseIterable, Codable, Sendable, Identifiable {
    case led, needle, liquid, reactor, minimal

    public var id: String { rawValue }

    public init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = OrbStyleKind(rawValue: raw) ?? .led
    }

    /// Allowed diameters: every style shares one range (DESIGN 2), so a 64 pt orb keeps its size across styles.
    public var sizeRange: ClosedRange<Double> { OrbSizes.range }

    public func clampSize(_ size: Double) -> Double {
        guard size.isFinite else { return OrbSizes.default.clampedToRange(sizeRange) }
        return size.clampedToRange(sizeRange)
    }

    public var titleKey: String { "style.\(rawValue)" }
    public var descriptionKey: String { "style.\(rawValue).desc" }
}

/// Size and opacity limits shared by every orb (DESIGN 2, 2b, 5).
public enum OrbSizes {
    /// S / M / L presets of the context menu and Settings.
    public static let presets: [Double] = [112, 148, 196]
    public static let `default`: Double = 148
    /// Slider step in Settings.
    public static let step: Double = 4
    public static let range: ClosedRange<Double> = 64...240
    public static let opacityRange: ClosedRange<Double> = 0.4...1
}
