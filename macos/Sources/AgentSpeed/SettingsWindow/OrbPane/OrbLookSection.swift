import AgentSpeedOrbModel
import SwiftUI

/// Size and opacity of the selected orb (DESIGN 5, editor): S / M / L presets left of a slider (64–240 pt, step 4)
/// with the value, opacity 40–100 %, and whether its name plate shows (automatic, on or off).
struct OrbLookSection: View {
    let model: AppModel
    let orb: OrbConfig

    private static let sliderWidth: CGFloat = 200
    private static let valueWidth: CGFloat = 52

    var body: some View {
        let l10n = model.l10n
        let orbs = model.orbs
        let id = orb.id
        let range = orb.style.sizeRange
        Section {
            LabeledContent {
                HStack(spacing: Spacing.m) {
                    Picker(l10n("mac.settings.orb.presets"), selection: Binding<Double?>(
                        get: { OrbSizes.presets.first { abs($0 - orb.size) < 0.5 } },
                        set: { if let size = $0 { orbs.setSize(size, of: id) } }
                    )) {
                        ForEach(Array(zip(OrbSizes.presets, ["menu.size.small", "menu.size.medium", "menu.size.large"])), id: \.0) { size, key in
                            Text(l10n(key)).tag(Optional(size))
                        }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .controlSize(.small)
                    .fixedSize()
                    .help(l10n("mac.settings.orb.presets"))

                    Slider(value: Binding(
                        get: { orb.size },
                        set: { orbs.setSize(($0 / OrbSizes.step).rounded() * OrbSizes.step, of: id) }
                    ), in: range) {
                        Text(l10n("settings.orb.size"))
                    }
                    .labelsHidden()
                    .frame(width: Self.sliderWidth)
                    .accessibilityValue(Text(l10n("mac.settings.orb.sizeValue", Int(orb.size.rounded()))))
                    .accessibilityAdjustableAction { direction in
                        orbs.setSize(SliderStep.adjust(orb.size, direction, step: OrbSizes.step, in: range), of: id)
                    }

                    ValueText(l10n("mac.settings.orb.sizeValue", Int(orb.size.rounded())))
                        .frame(minWidth: Self.valueWidth, alignment: .trailing)
                        .accessibilityHidden(true)
                }
            } label: {
                RowTitle(l10n("settings.orb.size"))
            }

            LabeledContent {
                HStack(spacing: Spacing.m) {
                    Slider(value: Binding(
                        get: { orb.opacity },
                        set: { orbs.setOpacity(($0 * 100).rounded() / 100, of: id) }
                    ), in: OrbSizes.opacityRange) {
                        Text(l10n("settings.orb.opacity"))
                    }
                    .labelsHidden()
                    .frame(width: Self.sliderWidth)
                    .accessibilityValue(Text(l10n("mac.settings.percentValue", Int((orb.opacity * 100).rounded()))))
                    .accessibilityAdjustableAction { direction in
                        orbs.setOpacity(SliderStep.adjust(orb.opacity, direction, step: 0.05, in: OrbSizes.opacityRange), of: id)
                    }

                    ValueText(l10n("mac.settings.percentValue", Int((orb.opacity * 100).rounded())))
                        .frame(minWidth: Self.valueWidth, alignment: .trailing)
                        .accessibilityHidden(true)
                }
            } label: {
                RowTitle(l10n("settings.orb.opacity"))
            }

            let automatic = NameplateVisibility.automatic(
                mode: model.settings.orbNames,
                shownOrbs: orbs.shownOrbs.count,
                tracksAll: NameplateVisibility.tracksAll(orb)
            )
            PopupRow(
                title: l10n("orbs.showName"),
                selection: Binding(get: { orb.showName }, set: { orbs.setShowName($0, of: id) })
            ) {
                Text(l10n("orbs.showName.auto", l10n(automatic ? "orbs.showName.on" : "orbs.showName.off"))).tag(Bool?.none)
                Divider()
                Text(l10n("orbs.showName.on")).tag(Bool?.some(true))
                Text(l10n("orbs.showName.off")).tag(Bool?.some(false))
            }
        }
    }
}

/// The window behaviour of the selected orb, and Reset Position (DESIGN 5, editor).
struct OrbSwitchesSection: View {
    let model: AppModel
    let orb: OrbConfig

    var body: some View {
        let l10n = model.l10n
        let orbs = model.orbs
        let id = orb.id
        Section {
            SettingToggle(title: l10n("menu.alwaysOnTop"), isOn: Binding(
                get: { orb.alwaysOnTop }, set: { orbs.setAlwaysOnTop($0, of: id) }
            ))
            SettingToggle(title: l10n("menu.clickThrough"), subtitle: l10n("mac.settings.orb.clickThroughHint"), isOn: Binding(
                get: { orb.clickThrough }, set: { orbs.setClickThrough($0, of: id) }
            ))
            SettingToggle(title: l10n("settings.orb.fadeIdle"), isOn: Binding(
                get: { orb.fadeWhenIdle }, set: { orbs.setFadeWhenIdle($0, of: id) }
            ))
            SettingToggle(title: l10n("settings.orb.allSpaces"), isOn: Binding(
                get: { orb.allSpaces }, set: { orbs.setAllSpaces($0, of: id) }
            ))
        } footer: {
            HStack {
                Spacer()
                Button(l10n("mac.settings.orb.resetPosition")) {
                    model.commands?.resetOrbPosition(id)
                }
            }
        }
    }
}
