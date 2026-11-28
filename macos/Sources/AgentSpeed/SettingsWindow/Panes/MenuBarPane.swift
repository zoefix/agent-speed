import SwiftUI

struct MenuBarPane: View {
    let model: AppModel

    var body: some View {
        @Bindable var settings = model.settings
        let l10n = model.l10n
        Form {
            Section {
                MenuBarPreview(model: model)
            }

            Section {
                Picker(l10n("settings.bar.mode"), selection: $settings.barMode) {
                    ForEach(BarDisplayMode.allCases) { mode in
                        Text(l10n(mode.titleKey)).tag(mode)
                    }
                }
                .pickerStyle(.radioGroup)

                SettingToggle(title: l10n("settings.bar.colored"), isOn: $settings.barColoredGlyph)
                    .disabled(!settings.barMode.showsGlyph)

                SettingToggle(title: l10n("settings.bar.unit"), isOn: $settings.barShowsUnit)
                    .disabled(!settings.barMode.showsValue)

                BarTrafficPicker(model: model)
                    .disabled(settings.barMode == .glyph)

                BarIntelligenceToggle(model: model)

                BarQuotaControls(model: model)
            }

            Section {
                BarValuePicker(model: model)
            } footer: {
                SectionFootnote(l10n("settings.liveUnit.hint"))
            }
        }
        .settingsForm()
    }
}

/// The menu bar's live value (DESIGN 3, 9): tok/s, money, or tok/s with the money rate after it.
private struct BarValuePicker: View {
    let model: AppModel

    private enum Choice: Hashable { case tokens, cost, both }

    var body: some View {
        let l10n = model.l10n
        let settings = model.settings
        let choice = Binding<Choice>(
            get: { settings.liveUnit == .cost ? .cost : settings.barSecondary ? .both : .tokens },
            set: { choice in
                settings.liveUnit = choice == .cost ? .cost : .tokens
                settings.barSecondary = choice == .both
            }
        )
        SegmentedRow(title: l10n("settings.liveUnit"), selection: choice, segmentWidth: 110, count: 3) {
            Text(l10n("settings.liveUnit.tokens")).tag(Choice.tokens)
            Text(l10n("settings.liveUnit.cost", model.cost.symbol)).tag(Choice.cost)
            Text(l10n("settings.liveUnit.both")).tag(Choice.both)
        }
    }
}
