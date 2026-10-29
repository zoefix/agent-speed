import AgentSpeedCore
import AppKit
import SwiftUI

/// Type of 性能表現 (DESIGN 12, *Usage History: performance*).
enum PerfHistoryFont {
    static let title = Font.system(size: 15, weight: .semibold)
    static let trendTitle = Font.system(size: 12)
    static let legend = Font.system(size: 11).monospacedDigit()
    static let axis = Font.system(size: 9.5).monospacedDigit()
    static let axisReference = Font.system(size: 9.5, weight: .semibold).monospacedDigit()
    static let emptyChart = Font.system(size: 12)
    static let header = Font.system(size: 11, weight: .semibold)
    static let cell = Font.system(size: 12).monospacedDigit()
    static let score = Font.system(size: 13, weight: .semibold).monospacedDigit()
    static let modelID = Font.system(size: 12.5, weight: .medium)
    static let listModelID = Font.system(size: 12.5, weight: .semibold)
    static let sourceName = Font.system(size: 11)
    static let listLine = Font.system(size: 11).monospacedDigit()
    static let footnote = Font.system(size: 11)
    static let tooltipTitle = Font.system(size: 12, weight: .semibold)
    static let tooltipScore = Font.system(size: 13, weight: .semibold).monospacedDigit()
    static let tooltip = Font.system(size: 11.5).monospacedDigit()
}

enum PerfHistoryLayout {
    /// The card's padding: 16 × 18, 14 at the bottom.
    static let horizontalPadding: CGFloat = 18
    static let topPadding: CGFloat = 16
    static let bottomPadding: CGFloat = 14
    static let rowHeight: CGFloat = 30
    static let rowInset: CGFloat = 8
    static let meterWidth: CGFloat = 36
    static let sourceDot: CGFloat = 7
    static let infoWidth: CGFloat = 340
}

/// Colors of the section beyond the history window's own.
enum PerfHistoryPalette {
    /// Axis values, the legend and the empty chart: the label color at 36 % (dark) or 42 % (light).
    static func tertiary(dark: Bool, increasedContrast: Bool) -> Color {
        if increasedContrast { return Color(nsColor: .secondaryLabelColor) }
        return dark ? Color(.sRGB, red: 235 / 255, green: 235 / 255, blue: 245 / 255, opacity: 0.36)
            : Color(.sRGB, red: 60 / 255, green: 60 / 255, blue: 67 / 255, opacity: 0.42)
    }

    /// The reference 1000 line: the label color at 35 %.
    static func reference(increasedContrast: Bool) -> Color {
        Color(nsColor: .labelColor).opacity(increasedContrast ? 0.8 : 0.35)
    }

    /// The single day's slot: the label color at 5 %.
    static func daySlot(increasedContrast: Bool) -> Color {
        Color(nsColor: .labelColor).opacity(increasedContrast ? 0.1 : 0.05)
    }

    static func crosshair(increasedContrast: Bool) -> Color {
        Color(nsColor: .labelColor).opacity(increasedContrast ? 0.7 : 0.3)
    }

    /// The selected row: the system's unemphasized selection fill.
    static var selection: Color { Color(nsColor: .unemphasizedSelectedContentBackgroundColor) }
}

enum PerfHistoryMotion {
    /// Values, the series and the states crossfade.
    static let fade = Animation.easeOut(duration: 0.2)
    /// Meters spring to their new lengths, settled within 0.35 s (DESIGN 6).
    static let meters = Animation.spring(response: 0.3, dampingFraction: 0.88)

    static func meters(reduceMotion: Bool) -> Animation { reduceMotion ? fade : meters }
}

/// The 7 pt source dot.
struct PerfSourceDot: View {
    let source: SourceKind

    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        Circle()
            .fill(source.tint(dark: colorScheme == .dark))
            .frame(width: PerfHistoryLayout.sourceDot, height: PerfHistoryLayout.sourceDot)
            .accessibilityHidden(true)
    }
}

/// The score with its 36 × 4 meter, or `–` with the sample hint.
struct PerfHistoryScoreCell: View {
    let row: PerfHistoryRow
    let l10n: Localizer
    var reduceMotion = false

    var body: some View {
        if let shown = row.shown {
            HStack(spacing: 8) {
                Text(PerfFormat.full(shown))
                    .font(PerfHistoryFont.score)
                    .foregroundStyle(.primary)
                    .lineLimit(1)
                    .fixedSize()
                PerfMiniMeter(fraction: PerfFormat.scale(Double(shown)), width: PerfHistoryLayout.meterWidth)
                    .animation(PerfHistoryMotion.meters(reduceMotion: reduceMotion), value: shown)
            }
        } else {
            Text(PerfHistoryText.absent)
                .font(PerfHistoryFont.cell)
                .foregroundStyle(.tertiary)
                .help(PerfHistoryText.sampleHint(row.n, l10n))
        }
    }
}

/// `樣本少`: a small chip in a dotted outline.
struct PerfLowSampleChip: View {
    let row: PerfHistoryRow
    let l10n: Localizer

    var body: some View {
        PerfChip(title: l10n("history.perf.sample.low"), kind: .dotted, size: .small)
            .help(PerfHistoryText.sampleHint(row.n, l10n))
    }
}
