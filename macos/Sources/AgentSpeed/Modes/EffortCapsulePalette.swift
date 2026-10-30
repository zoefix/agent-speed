import AgentSpeedCore
import SwiftUI

/// Colors of the effort capsule, its meter and bolts (DESIGN 4, Agents), in both appearances and with Increase Contrast.
struct EffortCapsulePalette {
    let dark: Bool
    let increasedContrast: Bool
    let tint: EffortCapsuleTint?

    init(dark: Bool, increasedContrast: Bool, tint: EffortCapsuleTint? = nil) {
        self.dark = dark
        self.increasedContrast = increasedContrast
        self.tint = tint
    }

    init(_ environment: EnvironmentValues) {
        self.init(dark: environment.colorScheme == .dark, increasedContrast: environment.colorSchemeContrast == .increased,
                  tint: environment.effortCapsuleTint)
    }

    var fill: Color { increasedContrast ? .clear : Color.primary.opacity(dark ? 0.07 : 0.055) }
    var stroke: Color { increasedContrast ? .primary : Color.primary.opacity(0.08) }
    var strokeWidth: CGFloat { increasedContrast ? 1 : 0.5 }

    var unlit: Color {
        increasedContrast ? Color(nsColor: .disabledControlTextColor) : Color.primary.opacity(dark ? 0.18 : 0.14)
    }

    var secondary: Color { DashboardPalette.secondary(dark: dark, increasedContrast: increasedContrast) }

    var bolt: Color {
        increasedContrast ? .accentColor : DashboardPalette.amber(dark: dark).color
    }

    // MARK: Configured values

    /// Bars, word, mark and bolts of a configured value: tertiary, the text color with Increase Contrast.
    var configuredInk: Color {
        if increasedContrast { return .primary }
        return tint?.ink ?? Color(nsColor: .tertiaryLabelColor)
    }

    var configuredUnlit: Color {
        increasedContrast ? Color(nsColor: .disabledControlTextColor) : Color.primary.opacity(dark ? 0.12 : 0.1)
    }

    var configuredStroke: Color {
        if increasedContrast { return .primary }
        return tint?.line ?? Color.primary.opacity(dark ? 0.26 : 0.22)
    }

    var configuredStrokeWidth: CGFloat { increasedContrast ? 1 : 0.75 }

    /// Fill of a lit bar whose top sits at `top` (0–1) of the meter height; the top rung's gradient spans the meter.
    func litBar(_ tone: EffortTone, top: Double) -> LinearGradient {
        let colors: [Color]
        switch tone {
        case _ where increasedContrast: colors = [.primary, .primary]
        case .neutral: colors = [secondary, secondary]
        case .configured: colors = [configuredInk, configuredInk]
        case .max: colors = Array(repeating: DashboardPalette.heatGraphic(0.82, dark: dark, increasedContrast: false).color, count: 2)
        case .topRung: colors = [heat(Self.topRungRange.lowerBound + topRungSpan * top), heat(Self.topRungRange.lowerBound)]
        }
        return LinearGradient(colors: colors, startPoint: .top, endPoint: .bottom)
    }

    /// Color of the word: secondary, heat 0.82 for `max`, the top rung's gradient running along the word.
    func word(_ tone: EffortTone) -> AnyShapeStyle {
        if increasedContrast { return AnyShapeStyle(Color.primary) }
        switch tone {
        case .neutral: return AnyShapeStyle(secondary)
        case .configured: return AnyShapeStyle(configuredInk)
        case .max: return AnyShapeStyle(DashboardPalette.heatText(0.82, dark: dark, increasedContrast: false))
        case .topRung:
            let range = Self.topRungRange
            return AnyShapeStyle(LinearGradient(
                colors: [
                    DashboardPalette.heatText(range.lowerBound, dark: dark, increasedContrast: false),
                    DashboardPalette.heatText(range.upperBound, dark: dark, increasedContrast: false),
                ],
                startPoint: .leading,
                endPoint: .trailing
            ))
        }
    }

    /// Share bar of a history row: neutral up to `xhigh`, heat 0.82 for `max`, the top rung's gradient.
    func shareBar(_ tone: EffortTone) -> AnyShapeStyle {
        if increasedContrast { return AnyShapeStyle(Color.primary) }
        switch tone {
        case .neutral, .configured: return AnyShapeStyle(Color.primary.opacity(dark ? 0.42 : 0.36))
        case .max: return AnyShapeStyle(heat(0.82))
        case .topRung:
            let range = Self.topRungRange
            return AnyShapeStyle(LinearGradient(colors: [heat(range.lowerBound), heat(range.upperBound)], startPoint: .leading, endPoint: .trailing))
        }
    }

    var unknownBar: Color { Color.primary.opacity(0.12) }

    static let topRungRange = 0.72...1.0
    private var topRungSpan: Double { Self.topRungRange.upperBound - Self.topRungRange.lowerBound }

    private func heat(_ h: Double) -> Color {
        DashboardPalette.heatGraphic(h, dark: dark, increasedContrast: false).color
    }
}

/// The colors a surface gives a configured value instead of the dashboard's: a board's `ink-3` and `line` (DESIGN 2c).
struct EffortCapsuleTint: Equatable, Hashable, Sendable {
    var ink: Color
    var line: Color
}

extension EnvironmentValues {
    @Entry var effortCapsuleTint: EffortCapsuleTint?
}

/// How an effort token is colored: neutral up to `xhigh`, `max`, the top rung (`ultra`, `ultracode`), and a configured
/// value of any level, which nothing confirms.
enum EffortTone: Equatable, Sendable {
    case neutral, max, topRung, configured

    init(_ effort: String?) {
        switch effort {
        case "max": self = .max
        case let value? where ModeDisplay.isTopRung(value): self = .topRung
        default: self = .neutral
        }
    }
}
