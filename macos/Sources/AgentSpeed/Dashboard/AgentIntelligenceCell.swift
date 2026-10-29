import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import SwiftUI

/// The agents' 性能 column (DESIGN 4, 性能 column): a fixed cell between the effort capsule column and the live value,
/// as wide as the wider of `8,888` (the widest compact score) and the header caption, so the digits of every row share
/// one right edge whatever the indent. One point under the live value, so tok/s stays the row's main number.
enum AgentIntelligenceColumn {
    /// Between the cell and the live value.
    static let gap: CGFloat = 6
    static let numberSize: CGFloat = 12
    static let captionSize: CGFloat = 10.5
    static let font = Font.system(size: numberSize, weight: .medium).monospacedDigit()

    @MainActor private static var widths: [String: CGFloat] = [:]

    /// Measured once per caption, so per language and never per value.
    @MainActor
    static func width(caption: String) -> CGFloat {
        if let width = widths[caption] { return width }
        func advance(_ text: String, _ font: NSFont) -> CGFloat {
            (text as NSString).size(withAttributes: [.font: font]).width
        }
        let width = ceil(max(
            advance(PerfFormat.measureText, .monospacedDigitSystemFont(ofSize: numberSize, weight: .medium)),
            advance(caption, .systemFont(ofSize: captionSize, weight: .medium))
        ))
        widths[caption] = width
        return width
    }
}

/// One row's performance score (DESIGN 4, 性能 column; DESIGN 12) in compact form: live in the live value's color,
/// resting and `–` in the tertiary color (Increase Contrast: secondary), the provisional mark under a provisional score.
/// A change of at least 1 % and 3 counts through the integers, up or down; smaller changes replace the digits; a change
/// of form crossfades the color in 0.2 s, kept with Reduce Motion. The row says the value in words, so the cell is
/// hidden from accessibility.
struct AgentIntelligenceCell: View {
    static let fade = Animation.easeOut(duration: PerfMotion.form)

    let reading: PerfSetReading
    let width: CGFloat

    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        let live = reading.form == .live && reading.shown != nil
        let quiet = contrast == .increased ? AnyShapeStyle(.dashboardSecondary) : AnyShapeStyle(.tertiary)
        PerfScoreNumber(value: reading.shown, form: .compact, font: AgentIntelligenceColumn.font)
            .lineLimit(1)
            .provisionalMark(reading.isProvisional, textSize: AgentIntelligenceColumn.numberSize)
            .foregroundStyle(live ? AnyShapeStyle(.primary) : quiet)
            .frame(width: width, alignment: .trailing)
            .animation(Self.fade, value: live)
            .accessibilityHidden(true)
    }
}
