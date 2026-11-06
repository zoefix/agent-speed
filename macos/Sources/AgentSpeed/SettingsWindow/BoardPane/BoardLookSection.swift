import AgentSpeedOrbModel
import SwiftUI

/// The selected board's size (S 280 / M 340 / L 420), opacity (40–100 %) and window toggles, in the orb editor's order
/// (DESIGN 2c).
struct BoardLookSection: View {
    let model: AppModel
    let board: BoardConfig

    private static let sliderWidth: CGFloat = 200
    private static let valueWidth: CGFloat = 52
    private static let opacityRange = 0.4...1.0

    var body: some View {
        let l10n = model.l10n
        let boards = model.boards
        let id = board.id
        Section {
            LabeledContent {
                HStack(spacing: Spacing.m) {
                    Picker(l10n("settings.orb.size"), selection: Binding(get: { board.size }, set: { boards.setSize($0, of: id) })) {
                        ForEach(BoardSize.allCases) { size in
                            Text(l10n(BoardMenus.sizeKey(size))).tag(size)
                        }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .fixedSize()
                    ValueText(l10n("mac.settings.orb.sizeValue", Int(board.size.side)))
                        .frame(minWidth: Self.valueWidth, alignment: .trailing)
                        .accessibilityHidden(true)
                }
            } label: {
                RowTitle(l10n("settings.orb.size"))
            }

            LabeledContent {
                HStack(spacing: Spacing.m) {
                    Slider(value: Binding(
                        get: { board.opacity },
                        set: { boards.setOpacity(($0 * 100).rounded() / 100, of: id) }
                    ), in: Self.opacityRange) {
                        Text(l10n("settings.orb.opacity"))
                    }
                    .labelsHidden()
                    .frame(width: Self.sliderWidth)
                    .accessibilityValue(Text(l10n("mac.settings.percentValue", Int((board.opacity * 100).rounded()))))
                    .accessibilityAdjustableAction { direction in
                        boards.setOpacity(SliderStep.adjust(board.opacity, direction, step: 0.05, in: Self.opacityRange), of: id)
                    }
                    ValueText(l10n("mac.settings.percentValue", Int((board.opacity * 100).rounded())))
                        .frame(minWidth: Self.valueWidth, alignment: .trailing)
                        .accessibilityHidden(true)
                }
            } label: {
                RowTitle(l10n("settings.orb.opacity"))
            }
        }

        Section {
            SettingToggle(title: l10n("menu.alwaysOnTop"), isOn: Binding(
                get: { board.alwaysOnTop }, set: { boards.setAlwaysOnTop($0, of: id) }
            ))
            SettingToggle(title: l10n("menu.clickThrough"), isOn: Binding(
                get: { board.clickThrough }, set: { boards.setClickThrough($0, of: id) }
            ))
            SettingToggle(title: l10n("settings.orb.fadeIdle"), isOn: Binding(
                get: { board.fadeWhenIdle }, set: { boards.setFadeWhenIdle($0, of: id) }
            ))
            SettingToggle(title: l10n("settings.orb.allSpaces"), isOn: Binding(
                get: { board.allSpaces }, set: { boards.setAllSpaces($0, of: id) }
            ))
        }
    }
}
