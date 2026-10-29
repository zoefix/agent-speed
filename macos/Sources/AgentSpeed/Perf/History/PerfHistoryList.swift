import AgentSpeedCore
import SwiftUI

/// Narrow: the table as a list of selectable rows (DESIGN 12, *Widths*). Line 1 the dot, the model id and, trailing, the
/// score with its meter; line 2 the source, the timed requests and the usual value; line 3 the present components.
struct PerfHistoryList: View {
    let rows: [PerfHistoryRow]
    let selected: PerfHistoryKey?
    let l10n: Localizer
    var reduceMotion = false
    let onSelect: (PerfHistoryKey) -> Void

    @State private var hovered: PerfHistoryKey?
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(rows) { row in
                item(row)
                    .onTapGesture { onSelect(row.id) }
                    .onHover { inside in
                        if inside { hovered = row.id } else if hovered == row.id { hovered = nil }
                    }
            }
        }
    }

    private func item(_ row: PerfHistoryRow) -> some View {
        let isSelected = row.id == selected
        let low = row.shown == nil
        let components = PerfHistoryText.listComponents(row, l10n)
        return VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 7) {
                PerfSourceDot(source: row.source)
                Text(PerfHistoryText.model(row, l10n))
                    .font(low ? PerfHistoryFont.listModelID.weight(.medium) : PerfHistoryFont.listModelID)
                    .foregroundStyle(low ? AnyShapeStyle(.historySecondary) : AnyShapeStyle(.primary))
                    .lineLimit(1)
                    .truncationMode(.middle)
                if low { PerfLowSampleChip(row: row, l10n: l10n) }
                Spacer(minLength: 8)
                PerfHistoryScoreCell(row: row, l10n: l10n, reduceMotion: reduceMotion)
            }
            Text(PerfHistoryText.listDetail(row, l10n))
                .font(PerfHistoryFont.listLine)
                .foregroundStyle(.historySecondary)
                .lineLimit(1)
                .truncationMode(.tail)
            if !components.isEmpty {
                Text(components)
                    .font(PerfHistoryFont.listLine)
                    .foregroundStyle(.historySecondary)
                    .lineSpacing(1.5)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.horizontal, PerfHistoryLayout.rowInset)
        .padding(.vertical, 9)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background {
            RoundedRectangle(cornerRadius: 6, style: .continuous)
                .fill(isSelected ? PerfHistoryPalette.selection
                    : hovered == row.id ? HistoryPalette.hover(dark: colorScheme == .dark) : .clear)
        }
        .overlay(alignment: .bottom) {
            if !isSelected { Hairline().padding(.horizontal, 2) }
        }
        .contentShape(Rectangle())
        .contextMenu {
            Button(l10n("mac.history.copy")) { PerfHistoryCopy.copy([row], l10n) }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(PerfHistoryText.accessibleLabel(row, l10n))
        .accessibilityValue(PerfHistoryText.accessibleValue(row, columns: PerfHistoryColumn.visible(.wide), l10n))
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
    }
}
