import AgentSpeedCore
import SwiftUI

/// Borderless icon button with a soft hover plate (footer).
struct DashboardIconButtonStyle: ButtonStyle {
    var isOn = false

    func makeBody(configuration: Configuration) -> some View {
        HoverPlate(isPressed: configuration.isPressed, isOn: isOn) {
            configuration.label
                .font(.system(size: 13.5, weight: .regular))
                .frame(width: 28, height: 28)
        }
    }
}

/// Borderless icon-and-title button; tinted when it represents an active state.
struct DashboardPillButtonStyle: ButtonStyle {
    var isOn = false

    func makeBody(configuration: Configuration) -> some View {
        HoverPlate(isPressed: configuration.isPressed, isOn: isOn) {
            configuration.label
                .font(.system(size: 12, weight: .medium))
                .padding(.horizontal, 9)
                .frame(height: 28)
        }
    }
}

/// A small glyph-and-title button that sits on a section title line without making it taller (Today → 歷史).
struct DashboardInlineButtonStyle: ButtonStyle {
    static let height: CGFloat = 22
    /// How far the plate reaches above and below the title line.
    static let overhang: CGFloat = 4

    func makeBody(configuration: Configuration) -> some View {
        HoverPlate(isPressed: configuration.isPressed, isOn: false) {
            configuration.label
                .font(.system(size: 11.5, weight: .medium))
                .padding(.horizontal, 7)
                .frame(height: Self.height)
        }
        .padding(.vertical, -Self.overhang)
    }
}

/// Full-width row button whose hover plate bleeds slightly past the column, so its content stays aligned with the
/// rows above while the layout keeps the plain row height (agents disclosure).
struct DashboardRowButtonStyle: ButtonStyle {
    private static let bleed: CGFloat = 6
    private static let lift: CGFloat = 2

    func makeBody(configuration: Configuration) -> some View {
        HoverPlate(isPressed: configuration.isPressed, isOn: false) {
            configuration.label
                .padding(.horizontal, Self.bleed)
                .frame(maxWidth: .infinity, minHeight: DashboardLayout.rowHeight + Self.lift * 2, alignment: .leading)
        }
        .padding(.horizontal, -Self.bleed)
        .padding(.vertical, -Self.lift)
    }
}

/// A source row that opens its usage history: the hover plate bleeds past the column like the agents disclosure, a
/// trailing chevron fades in on hover or keyboard focus, and the row keeps the plain row height.
struct DashboardSourceRowButtonStyle: ButtonStyle {
    static let leadingBleed: CGFloat = 8
    /// Wider on the trailing side so the chevron sits clear of the tok/s column.
    static let trailingBleed: CGFloat = 14
    private static let lift: CGFloat = 2

    /// Draws the hover state without a pointer (previews).
    var forcedHover = false
    /// A row with lines under its first one (quota lines): the plate keeps its lift around them and the chevron stays
    /// beside the first line.
    var stacked = false

    func makeBody(configuration: Configuration) -> some View {
        SourceRowPlate(isPressed: configuration.isPressed, forcedHover: forcedHover, stacked: stacked) {
            configuration.label
        }
    }

    private struct SourceRowPlate<Label: View>: View {
        let isPressed: Bool
        let forcedHover: Bool
        let stacked: Bool
        @ViewBuilder var label: Label

        @State private var isHovered = false
        @Environment(\.isFocused) private var isFocused
        @Environment(\.colorScheme) private var colorScheme
        @Environment(\.colorSchemeContrast) private var contrast
        @Environment(\.accessibilityReduceMotion) private var reduceMotion

        var body: some View {
            let dark = colorScheme == .dark
            let active = isHovered || isFocused || forcedHover
            let shape = RoundedRectangle(cornerRadius: 7, style: .continuous)
            label
                .padding(.leading, DashboardSourceRowButtonStyle.leadingBleed)
                .padding(.trailing, DashboardSourceRowButtonStyle.trailingBleed)
                .padding(.vertical, stacked ? DashboardSourceRowButtonStyle.lift : 0)
                .frame(maxWidth: .infinity, minHeight: DashboardLayout.rowHeight + DashboardSourceRowButtonStyle.lift * 2, alignment: .leading)
                .overlay(alignment: stacked ? .topTrailing : .trailing) {
                    Image(systemName: "chevron.right")
                        .font(.system(size: 8.5, weight: .bold))
                        .foregroundStyle(.dashboardSecondary)
                        .frame(width: DashboardSourceRowButtonStyle.trailingBleed,
                               height: stacked ? DashboardLayout.rowHeight + DashboardSourceRowButtonStyle.lift * 2 : nil)
                        .opacity(active ? 1 : 0)
                        .offset(x: active || reduceMotion ? 0 : -3)
                        .accessibilityHidden(true)
                }
                .background(shape.fill(plate(dark: dark)))
                .overlay {
                    if isFocused {
                        shape.strokeBorder(Color.accentColor.opacity(0.75), lineWidth: 2)
                    }
                }
                .contentShape(shape)
                .onHover { isHovered = $0 }
                .animation(Motion.fade, value: active)
                .animation(Motion.fade, value: isPressed)
                .padding(.leading, -DashboardSourceRowButtonStyle.leadingBleed)
                .padding(.trailing, -DashboardSourceRowButtonStyle.trailingBleed)
                .padding(.vertical, -DashboardSourceRowButtonStyle.lift)
        }

        private func plate(dark: Bool) -> Color {
            var level: Double = 0
            if isHovered || isFocused || forcedHover { level = dark ? 0.08 : 0.055 }
            if isPressed { level += 0.06 }
            if contrast == .increased, level > 0 { level += 0.06 }
            return (dark ? Color.white : Color.black).opacity(level)
        }
    }
}

private struct HoverPlate<Label: View>: View {
    let isPressed: Bool
    let isOn: Bool
    @ViewBuilder var label: Label

    @State private var isHovered = false
    @Environment(\.isEnabled) private var isEnabled
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        let dark = colorScheme == .dark
        label
            .foregroundStyle(foreground(dark: dark))
            .background {
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(plate(dark: dark))
            }
            .contentShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
            .opacity(isEnabled ? 1 : 0.4)
            .onHover { isHovered = $0 && isEnabled }
            .animation(Motion.fade, value: isHovered)
            .animation(Motion.fade, value: isOn)
    }

    private func foreground(dark: Bool) -> AnyShapeStyle {
        if isOn || isHovered || isPressed { return AnyShapeStyle(.primary) }
        return AnyShapeStyle(.dashboardSecondary)
    }

    private func plate(dark: Bool) -> Color {
        let base = isOn ? Color.accentColor : (dark ? Color.white : Color.black)
        var level: Double = 0
        if isOn { level = 0.14 }
        if isHovered { level += isOn ? 0.06 : (dark ? 0.1 : 0.07) }
        if isPressed { level += 0.06 }
        if contrast == .increased, level > 0 { level += 0.06 }
        return base.opacity(level)
    }
}

extension EnvironmentValues {
    /// The source row drawn as hovered in previews.
    @Entry var previewHoveredSource: SourceKind?
}
