import AgentSpeedCore
import AgentSpeedOrbModel
import SwiftUI

/// The six style cards (DESIGN 5, editor): each an animated preview by the real renderer at 64 pt over the style's
/// name (the `character` card shows the character's portrait), one row (3 + 3 when narrow); the selected card carries
/// an accent ring. ← / → move between cards. Below: the selected style's name and description.
///
/// The cards' dynamic layers share one Metal layer (one present per frame for all five), drawn between the card
/// backgrounds and their names.
struct OrbStyleCards: View {
    let model: AppModel
    let orb: OrbConfig
    let feeds: OrbStyleCardFeeds
    let active: Bool

    @FocusState private var focused: Bool
    @State private var orbFrames: [OrbStyleKind: CGRect] = [:]
    @State private var cardFrames: [OrbStyleKind: CGRect] = [:]
    @Environment(\.orbStillTime) private var stillTime

    /// The cards' frame rate (DESIGN 5: ≤ 24 fps).
    static let cardRate = 24.0
    static let orbDiameter: CGFloat = 64
    private static let spacing: CGFloat = 12
    private static let cardHeight: CGFloat = 112
    /// Six cards in one row from 560 pt of pane width, else 3 + 3 rows (DESIGN 5).
    private static let fitsOneRow = SettingsPaneView.width(for: .orb) >= 560
    nonisolated private static let space = "orbStyleCards"

    var body: some View {
        let l10n = model.l10n
        Section {
            VStack(alignment: .leading, spacing: Spacing.s) {
                ZStack(alignment: .topLeading) {
                    layout { background($0) }
                        .coordinateSpace(.named(Self.space))
                        .overlay {
                            if stillTime == nil {
                                OrbPreviewGroupView(model: model, specs: specs, cap: Self.cardRate, active: active)
                                    .allowsHitTesting(false)
                                    .accessibilityHidden(true)
                            }
                        }
                    layout { foreground($0) }
                }
                .focusable()
                .focused($focused)
                .focusEffectDisabled()
                .onKeyPress(keys: [.leftArrow, .rightArrow]) { press in
                    move(press.key == .rightArrow ? 1 : -1)
                    return .handled
                }
                (Text(l10n(orb.style.titleKey)).fontWeight(.semibold).foregroundStyle(.primary)
                    + Text("  ") + Text(l10n(orb.style.descriptionKey)).foregroundStyle(.secondary))
                    .font(.system(size: 12))
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, 2)
            }
            .padding(.vertical, Spacing.xxs)
        } header: {
            Text(l10n("orbs.style"))
        }
    }

    private var specs: [OrbPreviewSpec] {
        OrbStyleKind.allCases.compactMap { style in
            guard style != .character, let frame = orbFrames[style], let clip = cardFrames[style] else { return nil }
            return OrbPreviewSpec(id: style.rawValue, feed: feeds.feed(style), style: style, diameter: Self.orbDiameter,
                                  frame: frame, clip: clip, thumbnail: false, theme: model.titles.theme(forOrb: orb))
        }
    }

    /// The cards in one row, or 3 + 2 rows when narrow.
    @ViewBuilder
    private func layout<Card: View>(@ViewBuilder _ card: @escaping (OrbStyleKind) -> Card) -> some View {
        if Self.fitsOneRow {
            HStack(spacing: Self.spacing) {
                ForEach(OrbStyleKind.allCases) { card($0) }
            }
        } else {
            VStack(spacing: Self.spacing) {
                HStack(spacing: Self.spacing) {
                    ForEach(OrbStyleKind.allCases.prefix(3)) { card($0) }
                }
                HStack(spacing: Self.spacing) {
                    ForEach(OrbStyleKind.allCases.suffix(3)) { card($0) }
                }
            }
        }
    }

    /// The orb's locked character, else the first installed one.
    private var cardCharacter: InstalledCharacter? {
        let library = model.characters
        return orb.character.flatMap(library.installed) ?? library.installed.first
    }

    private func move(_ delta: Int) {
        let styles = OrbStyleKind.allCases
        guard let index = styles.firstIndex(of: orb.style) else { return }
        let next = styles[(index + delta + styles.count) % styles.count]
        model.orbs.setStyle(next, of: orb.id)
    }

    /// The card's surface and the orb's static body; reports where the orb and the card sit.
    private func background(_ style: OrbStyleKind) -> some View {
        let selected = orb.style == style
        return ZStack(alignment: .top) {
            StyleCardBackground(selected: selected)
            if style == .character {
                CharacterPortrait(pack: cardCharacter, height: 80)
                    .padding(.top, 4)
            } else {
                OrbPreviewView(model: model, feed: feeds.feed(style), style: style, diameter: Self.orbDiameter, cap: Self.cardRate,
                               active: active, dynamic: false, theme: model.titles.theme(forOrb: orb))
                    .onGeometryChange(for: CGRect.self) { $0.frame(in: .named(Self.space)) } action: { orbFrames[style] = $0 }
                    .padding(.top, -2)
            }
        }
        .frame(maxWidth: .infinity)
        .frame(height: Self.cardHeight)
        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        .onGeometryChange(for: CGRect.self) { $0.frame(in: .named(Self.space)) } action: { cardFrames[style] = $0 }
    }

    /// The card's name, ring and button, above the shared orb layer.
    private func foreground(_ style: OrbStyleKind) -> some View {
        let l10n = model.l10n
        let selected = orb.style == style
        return Button {
            model.orbs.setStyle(style, of: orb.id)
        } label: {
            Text(l10n(style.titleKey))
                .font(.system(size: 12, weight: selected ? .semibold : .medium))
                .foregroundStyle(selected ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary))
                .lineLimit(1)
                .minimumScaleFactor(0.8)
                .padding(.horizontal, 6)
                .padding(.top, 88)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                .frame(height: Self.cardHeight)
                .overlay(StyleCardRing(selected: selected))
                .contentShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(l10n("orbs.preview.a11y", l10n(style.titleKey)))
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

struct StyleCardBackground: View {
    let selected: Bool
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let dark = colorScheme == .dark
        RoundedRectangle(cornerRadius: 10, style: .continuous)
            .fill(selected
                ? AnyShapeStyle(Color.accentColor.opacity(dark ? 0.16 : 0.09))
                : AnyShapeStyle(Color.primary.opacity(dark ? 0.045 : 0.03)))
    }
}

struct StyleCardRing: View {
    let selected: Bool

    var body: some View {
        if selected {
            RoundedRectangle(cornerRadius: 11, style: .continuous)
                .strokeBorder(Color.accentColor, lineWidth: 2)
                .padding(-1)
        } else {
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(Color.primary.opacity(0.08), lineWidth: 0.5)
        }
    }
}
