import AgentSpeedCore
import SwiftUI

/// `性能表現` (15 pt semibold) with its ⓘ button.
struct PerfHistoryHeader: View {
    let l10n: Localizer

    var body: some View {
        HStack(alignment: .center, spacing: 6) {
            Text(l10n("history.perf.title"))
                .font(PerfHistoryFont.title)
                .lineLimit(1)
                .accessibilityAddTraits(.isHeader)
            PerfHistoryInfoButton(l10n: l10n)
            Spacer(minLength: 0)
        }
    }
}

/// ⓘ: a 340 pt popover with `history.perf.info` and every component's name with its meaning.
struct PerfHistoryInfoButton: View {
    let l10n: Localizer

    @State private var shown = false

    var body: some View {
        Button {
            shown.toggle()
        } label: {
            Image(systemName: "info.circle")
                .font(.system(size: 13))
                .foregroundStyle(.historySecondary)
                .contentShape(Rectangle())
        }
        .buttonStyle(.borderless)
        .help(l10n("history.perf.info.a11y"))
        .accessibilityLabel(l10n("history.perf.info.a11y"))
        .popover(isPresented: $shown, arrowEdge: .bottom) {
            PerfHistoryInfo(l10n: l10n)
        }
    }
}

struct PerfHistoryInfo: View {
    let l10n: Localizer

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            Text(l10n("history.perf.info"))
                .font(.system(size: 12))
                .foregroundStyle(.historySecondary)
                .lineSpacing(2.5)
                .fixedSize(horizontal: false, vertical: true)
            Divider()
            VStack(alignment: .leading, spacing: 9) {
                ForEach(PerfComponentId.cardOrder, id: \.self) { id in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(l10n(id.titleKey))
                            .font(.system(size: 12, weight: .semibold))
                        Text(l10n(id.meaningKey))
                            .font(.system(size: 11.5))
                            .foregroundStyle(.historySecondary)
                            .lineSpacing(1.5)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .accessibilityElement(children: .combine)
                }
            }
        }
        .padding(Spacing.m)
        .frame(width: PerfHistoryLayout.infoWidth, alignment: .leading)
        .environment(\.locale, l10n.locale)
    }
}
