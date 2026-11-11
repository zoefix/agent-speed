import AppKit
import SwiftUI

/// A quota line's tooltip with an estimate (DESIGN 4 *Tooltip*): the snapshot's time and pace, a hairline, the
/// estimate's lines in secondary text and its note in tertiary.
struct QuotaTooltip: Equatable {
    var head: [String]
    var details: [String]
    var note: String?

    static let maxWidth: CGFloat = 300
    static var font: NSFont { NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .regular) }
    static var noteFont: NSFont { NSFont.systemFont(ofSize: 10.5) }

    /// As wide as its widest line, at most `maxWidth`: the panel takes the view's fitting size, so the width is fixed
    /// and the height follows from it.
    var width: CGFloat {
        let lines = (head + details).map { SourceQuotaColumns.width($0, Self.font) }
            + [note.map { SourceQuotaColumns.width($0, Self.noteFont) } ?? 0]
        return min(Self.maxWidth, lines.max() ?? 0)
    }
}

struct QuotaTooltipView: View {
    let tooltip: QuotaTooltip

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            ForEach(Array(tooltip.head.enumerated()), id: \.offset) { _, line in
                Text(verbatim: line).foregroundStyle(.primary)
            }
            if !tooltip.details.isEmpty {
                Rectangle()
                    .fill(Color(nsColor: .separatorColor))
                    .frame(height: 0.5)
                    .padding(.vertical, 3)
                ForEach(Array(tooltip.details.enumerated()), id: \.offset) { _, line in
                    Text(verbatim: line).foregroundStyle(.secondary)
                }
            }
            if let note = tooltip.note {
                Text(verbatim: note)
                    .font(.system(size: 10.5))
                    .foregroundStyle(.tertiary)
                    .padding(.top, 2)
            }
        }
        .font(.system(size: 11))
        .monospacedDigit()
        .frame(width: tooltip.width, alignment: .leading)
        .fixedSize(horizontal: false, vertical: true)
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous)
            .strokeBorder(Color(nsColor: .separatorColor), lineWidth: 0.5))
    }
}

/// The one tooltip window: borderless, never key, ignoring the mouse, shown beside the pointer above the window it
/// belongs to.
@MainActor
final class QuotaTooltipPanel {
    static let shared = QuotaTooltipPanel()
    static let delay: Duration = .milliseconds(600)
    static let fade: TimeInterval = 0.15

    private let panel: NSPanel
    private let hosting: NSHostingView<AnyView>
    private(set) var isShown = false

    private init() {
        hosting = NSHostingView(rootView: AnyView(EmptyView()))
        hosting.sizingOptions = [.intrinsicContentSize]
        panel = TooltipWindow(contentRect: CGRect(x: 0, y: 0, width: 200, height: 60),
                              styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        panel.isReleasedWhenClosed = false
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.animationBehavior = .none
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient, .ignoresCycle]
        panel.setAccessibilityElement(false)
        panel.contentView = hosting
    }

    func show(_ tooltip: QuotaTooltip, at point: CGPoint, above window: NSWindow?, appearance: NSAppearance?) {
        hosting.rootView = AnyView(QuotaTooltipView(tooltip: tooltip))
        panel.appearance = appearance
        let size = hosting.fittingSize
        let screen = NSScreen.screens.first { $0.frame.contains(point) } ?? NSScreen.main
        let visible = screen?.visibleFrame ?? CGRect(origin: .zero, size: CGSize(width: 10_000, height: 10_000))
        var origin = CGPoint(x: point.x + 12, y: point.y - size.height - 18)
        if origin.y < visible.minY { origin.y = point.y + 18 }
        origin.x = min(max(visible.minX + 4, origin.x), visible.maxX - size.width - 4)
        let level = max(window?.level.rawValue ?? NSWindow.Level.floating.rawValue, NSWindow.Level.floating.rawValue) + 1
        panel.level = NSWindow.Level(rawValue: level)
        panel.setFrame(CGRect(origin: origin, size: size), display: true)
        if !isShown {
            isShown = true
            panel.alphaValue = 0
            panel.orderFrontRegardless()
            NSAnimationContext.runAnimationGroup { context in
                context.duration = Self.fade
                panel.animator().alphaValue = 1
            }
        }
        panel.invalidateShadow()
    }

    func hide() {
        guard isShown else { return }
        isShown = false
        panel.alphaValue = 0
        panel.orderOut(nil)
    }
}

private final class TooltipWindow: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

private struct QuotaTooltipModifier: ViewModifier {
    let tooltip: QuotaTooltip

    @State private var pending: Task<Void, Never>?
    @Environment(\.colorScheme) private var colorScheme

    func body(content: Content) -> some View {
        content
            .onHover { inside in
                pending?.cancel()
                guard inside else {
                    pending = nil
                    QuotaTooltipPanel.shared.hide()
                    return
                }
                let appearance = NSAppearance(named: colorScheme == .dark ? .darkAqua : .aqua)
                pending = Task { @MainActor in
                    try? await Task.sleep(for: QuotaTooltipPanel.delay)
                    guard !Task.isCancelled else { return }
                    QuotaTooltipPanel.shared.show(tooltip, at: NSEvent.mouseLocation, above: NSApp.keyWindow,
                                                  appearance: appearance)
                }
            }
            .onDisappear {
                pending?.cancel()
                QuotaTooltipPanel.shared.hide()
            }
    }
}

extension View {
    /// The estimate's styled tooltip while there is one, else the plain text tooltip.
    @ViewBuilder
    func quotaTooltip(_ tooltip: QuotaTooltip?, fallback: String) -> some View {
        if let tooltip, !tooltip.details.isEmpty {
            modifier(QuotaTooltipModifier(tooltip: tooltip))
        } else {
            help(fallback)
        }
    }
}
