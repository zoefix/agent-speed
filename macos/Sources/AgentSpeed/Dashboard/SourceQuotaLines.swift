import AgentSpeedCore
import SwiftUI

/// A Sources row's quota lines (DESIGN 4, *Quota lines*): one per window the account has, from the name's leading
/// edge — the label, the bar with its pace mark, the percent and the long reset, in columns shared by the section.
struct SourceQuotaLines: View {
    let source: SourceKind
    let lines: [SourceQuotaLine]
    let columns: SourceQuotaColumns
    let separator: String
    let reduceMotion: Bool
    var topPadding: CGFloat = 6

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(lines) { line in
                SourceQuotaLineView(source: source, line: line, columns: columns, separator: separator,
                                    reduceMotion: reduceMotion)
                    .transition(.opacity)
            }
        }
        .padding(.top, topPadding)
        .animation(reduceMotion ? nil : .easeInOut(duration: 0.25), value: lines.map(\.id))
    }
}

private struct SourceQuotaLineView: View {
    let source: SourceKind
    let line: SourceQuotaLine
    let columns: SourceQuotaColumns
    let separator: String
    let reduceMotion: Bool

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        let dark = colorScheme == .dark
        let strong = contrast == .increased
        HStack(spacing: SourceQuotaColumns.gap) {
            Text(verbatim: line.label)
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(.dashboardSecondary)
                .lineLimit(1)
                .frame(width: columns.label, alignment: .leading)
            QuotaBar(fraction: line.fraction, pace: line.pace, fill: fill(dark: dark, strong: strong),
                     track: strong ? Color(nsColor: .disabledControlTextColor) : Color.primary.opacity(dark ? 0.12 : 0.08),
                     tick: strong ? Color.primary : Color.primary.opacity(0.35),
                     animation: barAnimation)
                .frame(minWidth: SourceQuotaColumns.compactBar, maxWidth: .infinity)
            Text(verbatim: line.percentText)
                .font(.system(size: 11.5, weight: .semibold).monospacedDigit())
                .foregroundStyle(line.level == .critical ? Color(nsColor: .systemRed) : Color.primary)
                .lineLimit(1)
                .contentTransition(.opacity)
                .frame(width: columns.percent, alignment: .trailing)
            if columns.reset > 0 {
                tail
                    .font(.system(size: 11))
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .contentTransition(.opacity)
                    .frame(maxWidth: columns.reset, alignment: .leading)
                    .layoutPriority(1)
            }
        }
        .animation(.easeOut(duration: 0.2), value: line.percentText)
        .animation(.easeOut(duration: 0.2), value: line.reset)
        .contentShape(Rectangle())
        .help(line.tooltip)
    }

    /// The long reset in secondary, then ` · ` and the age in tertiary while the snapshot is old.
    private var tail: Text {
        let reset = Text(verbatim: line.reset ?? "").foregroundStyle(.dashboardSecondary)
        guard let age = line.age else { return reset }
        let lead = line.reset == nil ? "" : separator
        return reset + Text(verbatim: lead + age).foregroundStyle(.tertiary)
    }

    private func fill(dark: Bool, strong: Bool) -> Color {
        if strong { return Color.primary }
        return line.level.fill(dark: dark)?.color ?? source.tint(dark: dark)
    }

    /// 0.4 s to a new length, 0.6 s when the window started over; at once with Reduce Motion.
    private var barAnimation: Animation? {
        guard !reduceMotion else { return nil }
        return .easeOut(duration: line.passed ? 0.6 : 0.4)
    }
}

/// A 4 pt capsule bar with its 1 × 8 pt pace mark centered on its height.
struct QuotaBar: View {
    static let height: CGFloat = 4
    static let tickHeight: CGFloat = 8

    let fraction: Double
    let pace: Double?
    let fill: Color
    let track: Color
    let tick: Color
    let animation: Animation?

    var body: some View {
        GeometryReader { proxy in
            let width = proxy.size.width
            let filled = width * min(1, max(0, fraction))
            ZStack(alignment: .leading) {
                Capsule(style: .continuous)
                    .fill(track)
                    .frame(height: Self.height)
                Capsule(style: .continuous)
                    .fill(fill)
                    .frame(width: filled > 0 ? max(filled, 2) : 0, height: Self.height)
                    .animation(animation, value: fraction)
                if let pace {
                    RoundedRectangle(cornerRadius: 0.5)
                        .fill(tick)
                        .frame(width: 1, height: Self.tickHeight)
                        .offset(x: min(max(0, width * pace - 0.5), width - 1))
                        .animation(animation, value: pace)
                        .transition(.opacity)
                }
            }
            .frame(width: width, height: Self.tickHeight, alignment: .leading)
        }
        .frame(height: Self.tickHeight)
        .accessibilityHidden(true)
    }
}

/// The Claude Code row's invitation (DESIGN 4): the link that asks to connect the status line, and a × that hides it
/// for good.
struct QuotaInvitationLine: View {
    let model: AppModel

    @State private var isHovered = false

    var body: some View {
        let l10n = model.l10n
        HStack(spacing: Spacing.xs) {
            Button {
                model.quota.requestCliConfirmation()
                model.commands?.showSettings(.sources)
            } label: {
                Text(l10n("dashboard.quota.enableCli"))
                    .font(.system(size: 11))
                    .foregroundStyle(Color.accentColor)
                    .lineLimit(1)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .focusEffectDisabled()
            .onHover { inside in
                if inside { NSCursor.pointingHand.push() } else { NSCursor.pop() }
            }
            Spacer(minLength: Spacing.xs)
            Button {
                model.quota.dismissInvite()
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(isHovered ? AnyShapeStyle(.dashboardSecondary) : AnyShapeStyle(.tertiary))
                    // The glyph's trailing edge on the numbers' column edge.
                    .frame(width: 18, height: 16, alignment: .trailing)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .focusEffectDisabled()
            .onHover { isHovered = $0 }
            .animation(Motion.fade, value: isHovered)
            .help(l10n("dashboard.quota.dismiss"))
            .accessibilityLabel(l10n("dashboard.quota.dismiss"))
        }
        .padding(.top, 5)
    }
}
