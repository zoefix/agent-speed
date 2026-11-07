import SwiftUI

/// Settings → API (DESIGN 14): a two-segment control above live reporting (section 11) and the local network feed.
struct ApiPane: View {
    let model: AppModel

    static let segmentWidth: CGFloat = 300

    var body: some View {
        let l10n = model.l10n
        let segment = model.lan.segment
        VStack(spacing: 0) {
            Picker(l10n("settings.report"), selection: Binding(get: { model.lan.segment },
                                                               set: { model.lan.segment = $0 })) {
                Text(l10n("lan.segment.report")).tag(ApiSegment.report)
                Text(l10n("lan.segment.lan")).tag(ApiSegment.lan)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(width: Self.segmentWidth)
            .padding(.top, Spacing.l)
            .accessibilityLabel(l10n("settings.report"))
            ZStack(alignment: .top) {
                switch segment {
                case .report:
                    ReportPane(model: model)
                        .transition(.opacity)
                case .lan:
                    LanPane(model: model)
                        .transition(.opacity)
                }
            }
        }
        .animation(model.accessibility.reduceMotion ? nil : .easeOut(duration: 0.2), value: segment)
        // The grouped form's own background behind the segmented control too, as About does.
        .background {
            Form {}
                .settingsForm()
                .accessibilityHidden(true)
        }
    }
}
