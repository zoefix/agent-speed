import AppKit
import SwiftUI

/// The performance score's colors (DESIGN 12, *Score card*, *Colors*): the quiet slate-blue score ink, its track, the
/// pearl gradient of the scale, the caption color and the chips. Text uses the system label colors.
enum PerfPalette {
    /// Meters and the History chart's reference: a quiet indigo of the heat palette, distinct from every source color.
    static func ink(dark: Bool, increasedContrast: Bool) -> Color {
        if increasedContrast { return Color(nsColor: .labelColor) }
        return (dark ? RGBColor(hex: 0x93A4D6) : RGBColor(hex: 0x4B5FA6)).color
    }

    static func track(dark: Bool, increasedContrast: Bool) -> Color {
        if increasedContrast { return Color(nsColor: .labelColor).opacity(0.25) }
        return dark ? Color.white.opacity(0.09) : Color.black.opacity(0.07)
    }

    /// The dots of an absent component's track: the label color at 18 %, at 100 % with Increase Contrast.
    static func absentDots(increasedContrast: Bool) -> Color {
        Color(nsColor: .labelColor).opacity(increasedContrast ? 1 : 0.18)
    }

    /// Meanings, the footnote and hints: 4.5:1 at 10.5 pt; the system secondary color with Increase Contrast.
    static func caption(dark: Bool, increasedContrast: Bool) -> Color {
        if increasedContrast { return Color(nsColor: .secondaryLabelColor) }
        return dark ? Color(.sRGB, red: 235 / 255, green: 235 / 255, blue: 245 / 255, opacity: 0.56)
            : Color(.sRGB, red: 60 / 255, green: 60 / 255, blue: 67 / 255, opacity: 0.72)
    }

    /// The scale's fill: `pearl-c` → `pearl-a` → `pearl-b` laid across the whole track (the board glass arc's colors);
    /// the label color without the gradient under Increase Contrast.
    static func pearl(dark: Bool, increasedContrast: Bool) -> [Color] {
        if increasedContrast { return [Color(nsColor: .labelColor)] }
        let stops: [UInt32] = dark ? [0xA5F3FC, 0xC7D2FE, 0xF5D0FE] : [0x1AA3C4, 0x6D7CF0, 0xC05BD9]
        return stops.map { RGBColor(hex: $0).color }
    }

    /// A tick across a scale: 500 and 2000 at 18 %, the reference 1000 at 45 %; the label color with Increase Contrast.
    static func tick(reference: Bool, increasedContrast: Bool) -> Color {
        Color(nsColor: .labelColor).opacity(increasedContrast ? 1 : reference ? 0.45 : 0.18)
    }

    /// The card's fill under Reduce Transparency.
    static func solidCard(dark: Bool) -> Color {
        dark ? RGBColor(hex: 0x26272B).color : Color.white
    }

    /// A chip's text, fill and outline: mint when the score is above usual, neutral otherwise, a dotted outline without
    /// a baseline or while provisional.
    struct Chip: Equatable {
        enum Outline: Equatable {
            case none
            case dotted
            case solid
        }

        var text: Color
        var fill: Color?
        var outline: Outline
    }

    enum ChipKind: Equatable {
        case above
        case neutral
        case dotted
    }

    static func chip(_ kind: ChipKind, dark: Bool, increasedContrast: Bool) -> Chip {
        let secondary = Color(nsColor: .secondaryLabelColor)
        var chip: Chip = switch kind {
        case .above:
            dark ? Chip(text: RGBColor(hex: 0x6EE7B7).color,
                        fill: Color(.sRGB, red: 52 / 255, green: 211 / 255, blue: 153 / 255, opacity: 0.14), outline: .none)
                : Chip(text: RGBColor(hex: 0x047857).color,
                       fill: Color(.sRGB, red: 16 / 255, green: 185 / 255, blue: 129 / 255, opacity: 0.12), outline: .none)
        case .neutral:
            Chip(text: secondary, fill: Color(nsColor: .labelColor).opacity(0.06), outline: .none)
        case .dotted:
            Chip(text: secondary, fill: nil, outline: .dotted)
        }
        if increasedContrast, chip.outline != .dotted { chip.outline = .solid }
        return chip
    }
}
