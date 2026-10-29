import AgentSpeedCore
import SwiftUI

/// The card's header: a 7 pt source dot, the project (13 pt semibold, tail truncation) and, trailing, the provisional
/// chip while the score is provisional; line 2 is model · source (a group row adds its sub-agents) in 11 pt secondary.
struct PerfCardHeader: View {
    let header: PerfCardModel.Header
    let provisional: Bool
    let l10n: Localizer

    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let source = l10n(header.source.titleKey)
        VStack(alignment: .leading, spacing: 3) {
            HStack(alignment: .center, spacing: 7) {
                Circle()
                    .fill(header.source.tint(dark: colorScheme == .dark))
                    .frame(width: PerfCardLayout.dotSize, height: PerfCardLayout.dotSize)
                Text(header.project.isEmpty ? source : header.project)
                    .font(PerfCardFont.project)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .frame(maxWidth: .infinity, alignment: .leading)
                if provisional {
                    PerfChip(title: l10n("perf.provisional"), kind: .dotted, ring: true)
                        .padding(.leading, 4)
                        .transition(.opacity)
                }
            }
            .frame(minHeight: PerfChip.height)
            // A long model id gives way (middle truncation, as model ids are cut everywhere); the source and the
            // sub-agent count stay whole.
            HStack(spacing: 0) {
                if let model = header.model, !model.isEmpty {
                    Text(model)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Text(verbatim: " · ")
                        .fixedSize()
                }
                Text(trailingMeta(source))
                    .lineLimit(1)
                    .fixedSize()
            }
            .font(PerfCardFont.meta)
            .foregroundStyle(.secondary)
            .padding(.leading, PerfCardLayout.headerIndent)
        }
    }

    private func trailingMeta(_ source: String) -> String {
        header.subAgents > 0 ? source + " · " + l10n("boards.subCount", header.subAgents) : source
    }
}
