#if DEBUG
import AgentSpeedCore
import AppKit
import SwiftUI

/// `--screenshots DIR` group `perfcard` (debug builds): the real card window on its material (Liquid Glass on macOS
/// 26) over a wallpaper-like backdrop, light and dark, plus a rise captured mid count and a fall. Only the backdrop's
/// screen region is captured.
@MainActor
enum PerfCardHarness {
    static func run(model: AppModel, into directory: URL) async {
        let l10n = model.l10n
        let cases = Dictionary(uniqueKeysWithValues: PerfPreviewData.cards(l10n).map { ($0.name, $0.model) })
        let panel = PerfCardPanel(l10n: l10n)
        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let visible = screen.visibleFrame
        let region = CGRect(x: visible.midX - 300, y: visible.maxY - 860, width: 600, height: 840)
        // Stands in for the dashboard popover the card sits beside.
        let container = CGRect(x: region.midX + 20, y: region.maxY - 700, width: 260, height: 660)
        let anchor = PerfCardAnchor(row: CGRect(x: container.minX, y: container.maxY - 120, width: container.width, height: 36),
                                    container: container, visibleFrame: visible, side: .towardScreenCenter)
        for dark in [false, true] {
            NSApp.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            let backdrop = Backdrop(frame: region, container: container, dark: dark)
            await ScreenshotHarness.bringForward()
            for name in ["live", "thinking", "provisional", "resting", "none"] {
                guard let card = cases[name] else { continue }
                panel.show(card, key: name, anchor: anchor, above: backdrop.window, reduceMotion: false, reduceTransparency: false)
                try? await Task.sleep(for: .seconds(0.9))
                capture(region, "perfcard-\(name)-\(dark ? "dark" : "light")", into: directory)
                cache(panel, "perfcard-\(name)-\(dark ? "dark" : "light")", anchor: anchor, into: directory)
                panel.hide(animated: false)
            }
            if !dark, let live = cases["live"], let rise = rise(cases["live"]) {
                // A rise of 30 %: the number counts up over about 0.4 s; frames early, mid-way and landed.
                panel.show(live, key: "rise", anchor: anchor, above: backdrop.window, reduceMotion: false, reduceTransparency: false)
                try? await Task.sleep(for: .seconds(0.8))
                panel.update(rise)
                for (index, wait) in [0.08, 0.14, 0.6].enumerated() {
                    try? await Task.sleep(for: .seconds(wait))
                    capture(region, "perfcard-rise-\(index)", into: directory)
                }
                panel.hide(animated: false)
            }
            backdrop.close()
        }
        NSApp.appearance = nil
    }

    /// The live card with every component 30 % faster.
    private static func rise(_ card: PerfCardModel?) -> PerfCardModel? {
        guard var card, let shown = card.score.shown else { return nil }
        let evaluation = PerfPreviewData.evaluation(score: Double(shown) * 1.3, source: card.score.source,
                                                    model: card.header.model ?? "", n: card.score.n, tools: card.score.tools,
                                                    baseline: card.score.baseline)
        card.score.shown = evaluation.shown
        card.score.components = evaluation.components
        card.score.vsUsualPercent = evaluation.vsUsualPercent
        return card
    }

    /// Without Screen Recording permission `screencapture` fails; the window's own drawing (no material) and its frame
    /// against the expected placement still show that it fits its content and sits where DESIGN puts it.
    private static func cache(_ panel: PerfCardPanel, _ name: String, anchor: PerfCardAnchor, into directory: URL) {
        let window = panel.debugWindow
        guard let view = window.contentView, let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        if let data = rep.representation(using: .png, properties: [:]) {
            try? data.write(to: directory.appendingPathComponent("window-\(name).png"))
        }
        let expected = PerfCardPlacement.place(size: window.frame.size, anchor: anchor).frame
        let line = "\(name) frame \(window.frame) expected \(expected) level \(window.level.rawValue) visible \(window.isVisible)\n"
        let url = directory.appendingPathComponent("perfcard-frames.txt")
        if let handle = try? FileHandle(forWritingTo: url) {
            handle.seekToEndOfFile()
            handle.write(Data(line.utf8))
            try? handle.close()
        } else {
            try? Data(line.utf8).write(to: url)
        }
    }

    private static func capture(_ rect: CGRect, _ name: String, into directory: URL) {
        guard let screen = NSScreen.screens.first else { return }
        let top = screen.frame.maxY - rect.maxY
        let region = "\(Int(rect.minX)),\(Int(top)),\(Int(rect.width)),\(Int(rect.height))"
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        process.arguments = ["-x", "-R", region, directory.appendingPathComponent("screen-\(name).png").path]
        try? process.run()
        process.waitUntilExit()
    }

    /// A soft gradient standing in for the desktop picture, with a quiet panel where the dashboard would be.
    @MainActor
    private final class Backdrop {
        let window: NSWindow

        init(frame: CGRect, container: CGRect, dark: Bool) {
            window = NSWindow(contentRect: frame, styleMask: [.borderless], backing: .buffered, defer: false)
            window.isReleasedWhenClosed = false
            window.ignoresMouseEvents = true
            window.hasShadow = false
            window.level = .floating
            let colors: [Color] = dark
                ? [Color(red: 0.07, green: 0.10, blue: 0.24), Color(red: 0.24, green: 0.12, blue: 0.36), Color(red: 0.05, green: 0.22, blue: 0.30)]
                : [Color(red: 0.80, green: 0.88, blue: 0.98), Color(red: 0.96, green: 0.86, blue: 0.90), Color(red: 0.84, green: 0.95, blue: 0.90)]
            let local = container.offsetBy(dx: -frame.minX, dy: -frame.minY)
            window.contentView = NSHostingView(rootView: ZStack(alignment: .topLeading) {
                LinearGradient(colors: colors, startPoint: .topLeading, endPoint: .bottomTrailing)
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(dark ? Color.black.opacity(0.35) : Color.white.opacity(0.45))
                    .frame(width: local.width, height: local.height)
                    .offset(x: local.minX, y: frame.height - local.maxY)
            })
            window.orderFrontRegardless()
        }

        func close() { window.orderOut(nil) }
    }
}
#endif
