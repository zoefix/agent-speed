import AgentSpeedCore
import SwiftUI

/// `settings.bar.quota` and `settings.bar.quota.percent` (DESIGN 10): the quota block's source, right after the
/// intelligence switch, and whether its percents are used or remaining. A source whose quota is not read is listed but
/// disabled; a stored choice of it stays selected and the block reads `額度 –`.
struct BarQuotaControls: View {
    let model: AppModel

    var body: some View {
        @Bindable var settings = model.settings
        let l10n = model.l10n
        PopupRow(
            title: l10n("settings.bar.quota"),
            caption: settings.barQuota == nil ? nil : l10n("settings.bar.quota.hint"),
            selection: $settings.barQuota
        ) {
            Text(l10n("settings.bar.quota.off")).tag(SourceKind?.none)
            ForEach(QuotaController.sources, id: \.self) { source in
                Text(l10n(source.titleKey))
                    .tag(SourceKind?.some(source))
                    .disabled(!settings.showsQuota(source))
            }
        }
        .animation(Motion.fade, value: settings.barQuota == nil)

        SegmentedRow(title: l10n("settings.bar.quota.percent"), selection: $settings.barQuotaPercent, segmentWidth: 96,
                     count: BarQuotaPercent.allCases.count) {
            ForEach(BarQuotaPercent.allCases) { option in
                Text(l10n(option.titleKey)).tag(option)
            }
        }
        .disabled(settings.barQuota == nil)
    }
}
