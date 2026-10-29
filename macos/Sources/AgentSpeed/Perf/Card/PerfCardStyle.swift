import AppKit
import SwiftUI

/// The score card's metrics (DESIGN 12, *Score card*; the prototype's sizes where the section gives none).
enum PerfCardLayout {
    static let width: CGFloat = 320
    static let padding: CGFloat = 14
    static let radius: CGFloat = 12
    static let blockSpacing: CGFloat = 12
    static let contentWidth = width - 2 * padding
    static let dotSize: CGFloat = 7
    /// Line 2 of the header lines up with the project after the dot.
    static let headerIndent: CGFloat = dotSize + 7
    static let sectionTitleGap: CGFloat = 8
    static let groupSpacing: CGFloat = 14
    static let rowSpacing: CGFloat = 9
    static let scaleTrack: CGFloat = 6
    static let meterWidth: CGFloat = 44
    static let meterHeight: CGFloat = 4
    static let valueMinimum: CGFloat = 64
    static let subScoreMinimum: CGFloat = 34
}

enum PerfCardFont {
    static let project = Font.system(size: 13, weight: .semibold)
    static let meta = Font.system(size: 11)
    static let eyebrow = Font.system(size: 11)
    /// SF Pro Display semibold at 34 pt, tracking −0.02 em, tabular.
    static let number = Font.system(size: 34, weight: .semibold, design: .default)
    /// `–` while none: lighter, so the absent mark never reads as a value.
    static let numberAbsent = Font.system(size: 34, weight: .medium, design: .default)
    static let numberSize: CGFloat = 34
    static let numberTracking: CGFloat = -0.02 * 34
    static let scaleLabel = Font.system(size: 9.5).monospacedDigit()
    static let scaleReference = Font.system(size: 9.5, weight: .semibold).monospacedDigit()
    static let summary = Font.system(size: 12)
    static let hint = Font.system(size: 11)
    static let groupTitle = Font.system(size: 11, weight: .semibold)
    static let groupTitleLatin = Font.system(size: 10.5, weight: .semibold)
    static let componentLabel = Font.system(size: 12.5, weight: .semibold)
    static let componentValue = Font.system(size: 12, weight: .medium).monospacedDigit()
    static let componentReason = Font.system(size: 11.5)
    static let subScore = Font.system(size: 12, weight: .semibold).monospacedDigit()
    static let meaning = Font.system(size: 10.5)
    static let chip = Font.system(size: 11, weight: .semibold).monospacedDigit()
    static let signal = Font.system(size: 12)
    static let signalGlyph = Font.system(size: 13, weight: .medium)
    static let fact = Font.system(size: 11).monospacedDigit()
    static let footnote = Font.system(size: 10.5)
}

/// The card's reading text (meanings, hints, the footnote): the caption color of DESIGN 12 *Colors*, which keeps
/// 4.5:1 at 10.5 pt on the popover material; the secondary label color with Increase Contrast.
struct PerfCaptionStyle: ShapeStyle {
    func resolve(in environment: EnvironmentValues) -> Color {
        PerfPalette.caption(dark: environment.colorScheme == .dark,
                            increasedContrast: environment.colorSchemeContrast == .increased)
    }
}

extension ShapeStyle where Self == PerfCaptionStyle {
    static var perfCaption: PerfCaptionStyle { PerfCaptionStyle() }
}

/// A hairline in the separator color, one device pixel thick.
struct PerfCardHairline: View {
    @Environment(\.displayScale) private var scale

    var body: some View {
        Rectangle()
            .fill(Color(nsColor: .separatorColor))
            .frame(height: 1 / max(scale, 1))
            .accessibilityHidden(true)
    }
}

/// A group's or a section's small title (速度, 效率, 行為): 11 pt semibold, tertiary, tracking 0.06 em; Latin titles
/// upper-case at 10.5 pt.
struct PerfCardSectionTitle: View {
    let title: String

    var body: some View {
        let latin = !title.unicodeScalars.contains { $0.value >= 0x2E80 }
        Text(latin ? title.uppercased() : title)
            .font(latin ? PerfCardFont.groupTitleLatin : PerfCardFont.groupTitle)
            .tracking((latin ? 10.5 : 11) * 0.06)
            .foregroundStyle(.tertiary)
            .padding(.bottom, PerfCardLayout.sectionTitleGap)
    }
}
