import SwiftUI

/// Colors of the menu bar item, resolved from the menu bar's own appearance (not the app's Appearance setting).
struct StatusItemPalette: Equatable {
    let isDark: Bool
    let increasedContrast: Bool

    /// Approximate light menu bar the heat colors are checked against (every stop already clears 5:1 on dark bars).
    private static let lightBar = RGBColor(hex: 0xECECEC)

    /// Value text and the monochrome glyph. Native extras render their content at full strength.
    var primary: RGBColor { isDark ? .white : .black.opacity(0.85) }

    /// Unit and idle value; at least 4.5:1 against a plain bar.
    var secondary: RGBColor {
        isDark ? .white.opacity(increasedContrast ? 0.75 : 0.55) : .black.opacity(increasedContrast ? 0.72 : 0.56)
    }

    var track: RGBColor { primary.opacity(increasedContrast ? 0.4 : 0.25) }

    /// Strength of the soft glow around the fill from overdrive upward.
    var glowOpacity: Double { isDark ? 0.85 : 0.5 }

    /// Heat color for the fill, darkened on light bars until it keeps 3:1 (4.5:1 with Increase Contrast).
    func heat(_ h: Double) -> RGBColor {
        Self.legible(HeatPalette.color(h, dark: isDark), dark: isDark, target: increasedContrast ? 4.5 : 3)
    }

    /// An exhausted quota window's number (DESIGN 10): the system red, darkened on light bars until the 9 pt text keeps
    /// 4.5:1.
    var critical: RGBColor { Self.legible(QuotaLevel.red(dark: isDark), dark: isDark, target: 4.5) }

    private static func legible(_ color: RGBColor, dark: Bool, target: Double) -> RGBColor {
        guard !dark else { return color }
        var color = color
        var steps = 0
        while color.contrast(with: lightBar) < target, steps < 16 {
            color = color.darkened(0.05)
            steps += 1
        }
        return color
    }
}

private extension RGBColor {
    var relativeLuminance: Double {
        func linear(_ c: Double) -> Double { c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4) }
        return 0.2126 * linear(red) + 0.7152 * linear(green) + 0.0722 * linear(blue)
    }

    func contrast(with other: RGBColor) -> Double {
        let a = relativeLuminance, b = other.relativeLuminance
        return (max(a, b) + 0.05) / (min(a, b) + 0.05)
    }
}
