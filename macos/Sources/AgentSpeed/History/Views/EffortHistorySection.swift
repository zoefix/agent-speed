import AgentSpeedCore
import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// Effort and speed over the selected range and source (DESIGN 4b, 3b): one row per value, summing models, providers
/// and evidence, in display order with the unknown bucket last.
struct EffortHistorySection: View {
    let model: HistoryViewModel

    @AppStorage("historyModeKind") private var kindValue = ExecutionModeKind.effort.rawValue
    @State private var hovered: String?
    @State private var exportFailed = false

    private var kind: ExecutionModeKind { kindValue == ExecutionModeKind.speed.rawValue ? .speed : .effort }

    private var days: [DailyUsage] {
        let host = model.content.host
        return model.store.days.filter { day in
            (model.source == nil || day.source == model.source) && (host == nil || day.host == host)
                && DayKey(day.day).map(model.content.range.contains) == true
        }
    }

    var body: some View {
        let l10n = model.l10n
        let rows = ModeHistoryBreakdown.rows(days, kind: kind)
        let total = rows.reduce(0.0) { $0 + Double($1.output) }
        let reduceMotion = model.accessibility.reduceMotion

        VStack(alignment: .leading, spacing: Spacing.s) {
            header(l10n)
            captions(l10n)
            VStack(spacing: 2) {
                ForEach(rows) { row in
                    EffortHistoryRow(
                        row: row,
                        kind: kind,
                        share: total > 0 ? Double(row.output) / total : 0,
                        hovered: hovered == row.id,
                        l10n: l10n,
                        reduceMotion: reduceMotion
                    )
                    .onHover { inside in
                        if inside { hovered = row.id } else if hovered == row.id { hovered = nil }
                    }
                    .zIndex(hovered == row.id ? 1 : 0)
                    .transition(.opacity)
                }
            }
            .animation(reduceMotion ? HistoryMotion.fade : .spring(response: 0.32, dampingFraction: 0.86),
                       value: rows.map { [$0.mode, String($0.output), String(total)] })
            if !rows.contains(where: { !$0.isUnknown }) {
                Text(l10n("modes.history.empty"))
                    .font(HistoryFont.cardNote)
                    .foregroundStyle(.historySecondary)
                    .transition(.opacity)
            }
        }
        .padding(.horizontal, Spacing.m)
        .padding(.top, 14)
        .padding(.bottom, Spacing.m)
        .historyCard()
        .zIndex(1)
        .alert(l10n("mac.history.export.failed"), isPresented: $exportFailed) {
            Button(l10n("mac.update.ok"), role: .cancel) {}
        }
    }

    private func header(_ l10n: Localizer) -> some View {
        HStack(alignment: .center, spacing: Spacing.s) {
            Text(l10n("modes.history.title"))
                .font(HistoryFont.sectionTitle)
                .accessibilityAddTraits(.isHeader)
            Picker(l10n("modes.history.title"), selection: $kindValue) {
                Text(l10n("effort.label")).tag(ExecutionModeKind.effort.rawValue)
                Text(l10n("speed.label")).tag(ExecutionModeKind.speed.rawValue)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .controlSize(.small)
            .fixedSize()
            Spacer(minLength: Spacing.s)
            Button(action: export) {
                Label(l10n("mode.exportCsv"), systemImage: "square.and.arrow.up")
                    .font(HistoryFont.legend)
            }
            .buttonStyle(.borderless)
            .help(l10n("mode.exportCsv"))
        }
    }

    private func captions(_ l10n: Localizer) -> some View {
        HStack(spacing: EffortHistoryRow.columnSpacing) {
            Spacer(minLength: 0)
            ForEach(EffortHistoryRow.numberColumns, id: \.key) { column in
                Text(l10n(column.key))
                    .frame(width: column.width, alignment: .trailing)
            }
        }
        .font(HistoryFont.header)
        .foregroundStyle(.historySecondary)
        .lineLimit(1)
        .accessibilityHidden(true)
    }

    /// The mode CSV of every kind, effort and speed included, over the selected range and source.
    private func export() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.commaSeparatedText]
        panel.canCreateDirectories = true
        panel.nameFieldStringValue = "agent-speed-modes-\(model.content.range.start)-\(model.content.range.end).csv"
        let data = ExecutionModeCSV.build(days)
        panel.begin { response in
            MainActor.assumeIsolated {
                guard response == .OK, let url = panel.url else { return }
                do { try data.write(to: url, options: .atomic) } catch { exportFailed = true }
            }
        }
    }
}
