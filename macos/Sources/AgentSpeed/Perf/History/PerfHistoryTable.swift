import AgentSpeedCore
import AppKit
import SwiftUI

/// Wide and medium: the table in the daily table's style — 30 pt rows, alternating tint, hover highlight, sortable
/// headers, the selected row in the unemphasized selection fill, copy row as text (DESIGN 12, *Table*).
struct PerfHistoryTable: View {
    let rows: [PerfHistoryRow]
    let width: PerfHistoryWidth
    let sort: PerfHistorySort
    let selected: PerfHistoryKey?
    let l10n: Localizer
    var reduceMotion = false
    let onSort: (PerfHistoryColumn) -> Void
    let onSelect: (PerfHistoryKey) -> Void

    @State private var hovered: PerfHistoryKey?

    var body: some View {
        let columns = PerfHistoryColumn.visible(width)
        VStack(alignment: .leading, spacing: 0) {
            PerfHistoryTableHeader(columns: columns, sort: sort, l10n: l10n, onSort: onSort)
            ForEach(Array(rows.enumerated()), id: \.element.id) { index, row in
                PerfHistoryTableRow(row: row, index: index, columns: columns, selected: row.id == selected,
                                    hovered: hovered == row.id, l10n: l10n, reduceMotion: reduceMotion)
                    .onTapGesture { onSelect(row.id) }
                    .onHover { inside in
                        if inside { hovered = row.id } else if hovered == row.id { hovered = nil }
                    }
            }
        }
    }
}

private struct PerfHistoryTableHeader: View {
    let columns: [PerfHistoryColumn]
    let sort: PerfHistorySort
    let l10n: Localizer
    let onSort: (PerfHistoryColumn) -> Void

    var body: some View {
        PerfHistoryColumnsLayout(columns: columns, alignment: .bottom) {
            ForEach(columns, id: \.self) { column in
                header(column)
            }
        }
        .padding(.horizontal, PerfHistoryLayout.rowInset)
        .padding(.top, 2)
        .padding(.bottom, 6)
        .frame(maxWidth: .infinity, alignment: .leading)
        .overlay(alignment: .bottom) { Hairline() }
    }

    @ViewBuilder
    private func header(_ column: PerfHistoryColumn) -> some View {
        let title = l10n(column.headerKey)
        let full = l10n(column.fullKey)
        let tooltip = column.hintKey.map { full + " — " + l10n($0) } ?? full
        let active = sort.column == column
        // As in the daily table: only the sorted column carries its chevron, away from the alignment edge.
        let indicator = Image(systemName: sort.descending ? "chevron.down" : "chevron.up")
            .font(.system(size: 8, weight: .bold))
        let label = HStack(alignment: .lastTextBaseline, spacing: 3) {
            if column.isNumeric {
                Spacer(minLength: 0)
                if active { indicator }
            }
            Text(title)
                .lineLimit(2)
                .multilineTextAlignment(column.isNumeric ? .trailing : .leading)
                .fixedSize(horizontal: false, vertical: true)
            if !column.isNumeric {
                if active { indicator }
                Spacer(minLength: 0)
            }
        }
        .font(PerfHistoryFont.header)
        .foregroundStyle(active ? AnyShapeStyle(.primary) : AnyShapeStyle(.historySecondary))
        .frame(maxWidth: .infinity)
        .contentShape(Rectangle())
        if column.isSortable {
            Button { onSort(column) } label: { label }
                .buttonStyle(.plain)
                .help(tooltip)
                .accessibilityLabel(full)
                .accessibilityValue(active ? l10n(sort.descending ? "mac.history.sorted.desc" : "mac.history.sorted.asc") : "")
                .accessibilityAddTraits(active ? [.isHeader, .isSelected] : .isHeader)
        } else {
            label
                .help(tooltip)
                .accessibilityLabel(full)
                .accessibilityAddTraits(.isHeader)
        }
    }
}

private struct PerfHistoryTableRow: View {
    let row: PerfHistoryRow
    let index: Int
    let columns: [PerfHistoryColumn]
    let selected: Bool
    let hovered: Bool
    let l10n: Localizer
    let reduceMotion: Bool

    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let dark = colorScheme == .dark
        PerfHistoryColumnsLayout(columns: columns, alignment: .center) {
            ForEach(columns, id: \.self) { column in
                cell(column)
            }
        }
        .padding(.horizontal, PerfHistoryLayout.rowInset)
        .padding(.vertical, 3)
        .frame(minHeight: PerfHistoryLayout.rowHeight)
        .fixedSize(horizontal: false, vertical: true)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background {
            RoundedRectangle(cornerRadius: 6, style: .continuous)
                .fill(selected ? PerfHistoryPalette.selection
                    : hovered ? HistoryPalette.hover(dark: dark)
                    : index.isMultiple(of: 2) ? .clear : HistoryPalette.stripe(dark: dark))
        }
        .contentShape(Rectangle())
        .contextMenu {
            Button(l10n("mac.history.copy")) { PerfHistoryCopy.copy([row], l10n) }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(PerfHistoryText.accessibleLabel(row, l10n))
        .accessibilityValue(PerfHistoryText.accessibleValue(row, columns: columns, l10n))
        .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
    }

    @ViewBuilder
    private func cell(_ column: PerfHistoryColumn) -> some View {
        let low = row.shown == nil
        switch column {
        case .model:
            PerfHistoryModelCell(row: row, l10n: l10n)
        case .score:
            PerfHistoryScoreCell(row: row, l10n: l10n, reduceMotion: reduceMotion)
        case .parts:
            PerfPartsBars(row: row, l10n: l10n, reduceMotion: reduceMotion)
        default:
            let text = PerfHistoryText.cell(row, column, l10n)
            let absent = text == PerfHistoryText.absent
            Text(text)
                .font(PerfHistoryFont.cell)
                .foregroundStyle(absent ? AnyShapeStyle(.tertiary)
                    : (low || column == .usual ? AnyShapeStyle(.historySecondary) : AnyShapeStyle(.primary)))
                .lineLimit(1)
                .minimumScaleFactor(0.8)
                .help(PerfHistoryText.tooltip(row, column, l10n) ?? "")
        }
    }
}

/// The model cell: the source dot, the model id (middle truncation; an unknown model `cost.noModelName`), the source
/// name, and `樣本少` while the row has no score.
struct PerfHistoryModelCell: View {
    let row: PerfHistoryRow
    let l10n: Localizer

    var body: some View {
        let low = row.shown == nil
        HStack(spacing: 7) {
            PerfSourceDot(source: row.source)
            Group {
                if row.model.isEmpty {
                    Text(l10n("cost.noModelName")).italic()
                } else {
                    Text(row.model)
                }
            }
            .font(low ? PerfHistoryFont.modelID.weight(.regular) : PerfHistoryFont.modelID)
            .foregroundStyle(low || row.model.isEmpty ? AnyShapeStyle(.historySecondary) : AnyShapeStyle(.primary))
            .lineLimit(1)
            .truncationMode(.middle)
            .layoutPriority(1)
            Text(PerfHistoryText.source(row, l10n))
                .font(PerfHistoryFont.sourceName)
                .foregroundStyle(.historySecondary)
                .lineLimit(1)
                .fixedSize()
            if low { PerfLowSampleChip(row: row, l10n: l10n) }
        }
        .help(PerfHistoryText.subject(row, l10n))
    }
}

@MainActor
enum PerfHistoryCopy {
    static func copy(_ rows: [PerfHistoryRow], _ l10n: Localizer) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(PerfHistoryText.copyText(rows, l10n), forType: .string)
    }
}

/// Lays a header's or a row's cells out in the table's columns over the width it is given: numbers in fixed columns
/// aligned trailing, the model in the rest.
struct PerfHistoryColumnsLayout: Layout {
    let columns: [PerfHistoryColumn]
    var alignment: VerticalAlignment = .center

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let available = proposal.width ?? 640
        let layout = PerfHistoryColumnLayout.layout(columns, available: available)
        let height = zip(subviews, layout.widths).map { subview, width in
            subview.sizeThatFits(ProposedViewSize(width: width, height: nil)).height
        }.max() ?? 0
        return CGSize(width: available, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let layout = PerfHistoryColumnLayout.layout(columns, available: bounds.width)
        var x = bounds.minX
        for (index, subview) in subviews.enumerated() {
            let width = index < layout.widths.count ? layout.widths[index] : 0
            let size = subview.sizeThatFits(ProposedViewSize(width: width, height: nil))
            let numeric = index < columns.count ? columns[index].isNumeric : true
            let fitted = min(size.width, width)
            let cellX = numeric ? x + width - fitted : x
            let y: CGFloat = switch alignment {
            case .bottom: bounds.maxY - size.height
            case .top: bounds.minY
            default: bounds.midY - size.height / 2
            }
            subview.place(at: CGPoint(x: cellX, y: y), proposal: ProposedViewSize(width: fitted, height: size.height))
            x += width + layout.gap
        }
    }
}
