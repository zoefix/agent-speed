import AgentSpeedCore
import AgentSpeedOrbModel
import SwiftUI

/// Settings → 懸浮窗 (DESIGN 5, Floating orb pane): the master switch and name labels, the orb list with live
/// thumbnails, and the editor of the selected orb — sources, style cards, unit and traffic, size, opacity and name,
/// window behaviour and Advanced; then the leaderboard boards (DESIGN 2c). Every change applies live and persists
/// immediately. Thumbnails and style cards draw only while the pane is visible.
struct OrbPane: View {
    let model: AppModel

    @State private var activity = OrbPaneActivity()
    @State private var hovered: String?
    @State private var advancedExpanded: Bool
    @State private var cardFeeds = OrbStyleCardFeeds()

    @Environment(\.orbStillTime) private var stillTime

    init(model: AppModel, advancedExpanded: Bool = false) {
        self.model = model
        _advancedExpanded = State(initialValue: advancedExpanded)
    }

    private var orbs: OrbsController { model.orbs }

    /// The orb the editor shows: the Settings selection when it still exists, else the first.
    private var selected: OrbConfig? {
        orbs.settingsSelection.flatMap { orbs.orb($0) } ?? orbs.orbs.first
    }

    var body: some View {
        let l10n = model.l10n
        let live = activity.visible && stillTime == nil
        Form {
            Section {
                SettingToggle(
                    title: l10n("settings.orb.show"),
                    subtitle: l10n("orbs.allNote", l10n("settings.bar")),
                    isOn: Binding(get: { orbs.masterVisible }, set: { orbs.masterVisible = $0 })
                )
                PopupRow(
                    title: l10n("orbs.names"),
                    caption: l10n("orbs.names.hint"),
                    selection: Binding(get: { model.settings.orbNames }, set: { model.settings.orbNames = $0 })
                ) {
                    ForEach(OrbNamesMode.allCases) { mode in
                        Text(l10n(mode.titleKey)).tag(mode)
                    }
                }
            }

            if let orb = selected {
                OrbListSection(model: model, selection: orb.id, active: live, hovered: $hovered)

                OrbSourcesSection(model: model, orb: orb) {
                    OrbEditorHeading(model: model, orb: orb)
                }
                HostPickerSection(model: model, selection: orb.hosts,
                                  set: { orbs.setHosts($0, of: orb.id) },
                                  toggle: { orbs.toggleHost($0, of: orb.id) })
                OrbStyleCards(model: model, orb: orb, feeds: cardFeeds, active: live)
                if orb.style == .character {
                    OrbCharacterSection(model: model, orb: orb)
                        .id(OrbCharacterSection.anchor)
                    OrbCharacterReadoutsSection(model: model, orb: orb)
                } else {
                    OrbUnitSection(model: model, orb: orb)
                }
                OrbLookSection(model: model, orb: orb)
                OrbSwitchesSection(model: model, orb: orb)
                OrbAdvancedSection(model: model, orb: orb, expanded: $advancedExpanded)
            }

            BoardsSettings(model: model)
        }
        .settingsForm()
        .background(OrbPaneActivityReader(activity: $activity))
        .onAppear {
            if stillTime != nil { cardFeeds.settle(at: 2.6) }
            updateFeeds()
            updateHighlight()
        }
        .onDisappear {
            cardFeeds.stop()
            if orbs.highlightedOrb != nil { orbs.highlightedOrb = nil }
        }
        .onChange(of: activity) {
            updateFeeds()
            updateHighlight()
        }
        .onChange(of: model.accessibility.reduceMotion) { updateFeeds() }
        .onChange(of: hovered) { updateHighlight() }
        .onChange(of: orbs.settingsSelection) { updateHighlight() }
    }

    private func updateFeeds() {
        guard stillTime == nil else { return }
        cardFeeds.setActive(activity.visible, reduceMotion: model.accessibility.reduceMotion)
    }

    /// Hovering a row rings its orb on the desktop; the selected orb is ringed while this pane is in front (DESIGN 2b).
    private func updateHighlight() {
        guard stillTime == nil else { return }
        let target = hovered ?? (activity.visible && activity.key ? selected?.id : nil)
        if orbs.highlightedOrb != target { orbs.highlightedOrb = target }
    }
}

/// The editor's heading: the selected orb's colour dots and name (17 pt semibold).
struct OrbEditorHeading: View {
    let model: AppModel
    let orb: OrbConfig

    var body: some View {
        let orbs = model.orbs
        HStack(spacing: Spacing.xs) {
            let dots = orbs.dots(of: orb.id)
            if !dots.isEmpty { OrbSourceDots(dots: dots, size: 8) }
            Text(orbs.displayName(of: orb.id))
                .font(.system(size: 17, weight: .semibold))
                .lineLimit(1)
            Spacer()
        }
        .foregroundStyle(.primary)
        .padding(.top, Spacing.xxs)
        .padding(.bottom, Spacing.xs)
        .accessibilityAddTraits(.isHeader)
    }
}
