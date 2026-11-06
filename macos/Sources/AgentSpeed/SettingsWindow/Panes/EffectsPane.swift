import SwiftUI

struct EffectsPane: View {
    let model: AppModel

    var body: some View {
        @Bindable var settings = model.settings
        let l10n = model.l10n
        let effectsOff = settings.effects == .off
        Form {
            Section {
                SegmentedRow(
                    title: l10n("settings.effects.intensity"),
                    selection: $settings.effects,
                    count: EffectsLevel.allCases.count
                ) {
                    ForEach(EffectsLevel.allCases) { level in
                        Text(l10n(level.titleKey)).tag(level)
                    }
                }
            } footer: {
                if model.accessibility.reduceMotion {
                    Notice(text: l10n("settings.effects.reduceMotion"), symbol: "figure.walk.motion")
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }

            Section {
                SettingToggle(title: l10n("settings.effects.peaks"), isOn: $settings.celebratePeaks)
                    .disabled(effectsOff)
                SettingToggle(title: l10n("settings.effects.pulses"), isOn: $settings.responsePulses)
                    .disabled(effectsOff)
            }

            Section {
                LabeledContent {
                    PreviewEffectsControl(model: model)
                } label: {
                    Text(l10n("mac.settings.effects.previewHint"))
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            Section {
                SettingToggle(title: l10n("mac.settings.effects.displaySync"), isOn: $settings.displaySync)
            } footer: {
                SectionFootnote(l10n("mac.settings.effects.displaySync.hint"))
            }
        }
        .settingsForm()
    }
}

/// Starts the 20 s sweep; while it runs, shows progress and offers to stop it.
private struct PreviewEffectsControl: View {
    let model: AppModel

    var body: some View {
        let l10n = model.l10n
        HStack(spacing: Spacing.xs) {
            if model.isPreviewing {
                ProgressView()
                    .controlSize(.small)
                    .accessibilityHidden(true)
                Text(l10n("settings.effects.previewing"))
                    .foregroundStyle(.secondary)
                Button(l10n("mac.dashboard.stopPreview")) {
                    model.stopPreview()
                }
            } else {
                Button {
                    model.previewEffects()
                } label: {
                    HStack(spacing: 5) {
                        Image(systemName: "play.fill")
                            .font(.system(size: 9, weight: .bold))
                            .accessibilityHidden(true)
                        Text(l10n("settings.effects.preview"))
                    }
                }
            }
        }
        .animation(Motion.fade, value: model.isPreviewing)
    }
}
