#if DEBUG
import AgentSpeedCore
import AppKit
import SwiftUI

/// `--screenshots DIR` (debug builds): shows the real surfaces with sample data on screen and captures each window
/// with `screencapture`, so system materials (Liquid Glass on macOS 26+) look exactly as in use. Light and dark;
/// `AGENT_SPEED_LANGUAGE` (`en`, `ja`, …) the language, `AGENT_SPEED_SCREENS` picks groups (dashboard, settings, history, update; `orbpane` measures the floating orb pane's
/// CPU and captures it, see `OrbPaneHarness`; `titles` the title editor's, see `TitlesPaneHarness`), `AGENT_SPEED_HISTORY_CASES` history
/// cases. Quits when done.
@MainActor
enum ScreenshotHarness {
    static func run(into directory: URL, onlyHistory: Bool) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let suite = "AgentSpeedScreenshots"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        let model = AppModel(
            settings: AppSettings(defaults: defaults),
            updates: UpdateController(configuration: .current(environment: [:]), store: UpdateStore(defaults: defaults))
        )
        if let language = ProcessInfo.processInfo.environment["AGENT_SPEED_LANGUAGE"].flatMap(LanguagePreference.init(rawValue:)) {
            model.settings.language = language
        }
        model.activate(live: false)
        let groups = ProcessInfo.processInfo.environment["AGENT_SPEED_SCREENS"]?.split(separator: ",").map(String.init)
            ?? (onlyHistory ? ["history"] : ["dashboard", "settings", "history", "update"])
        // A regular app is let to the front more readily, and screenshots need the active-window look.
        NSApp.setActivationPolicy(.regular)
        Task { @MainActor in
            if groups.contains("dashboard") { await captureDashboard(model: model, into: directory) }
            if groups.contains("settings") { await captureSettings(model: model, into: directory) }
            if groups.contains("orbpane") { await OrbPaneHarness.run(model: model, into: directory) }
            if groups.contains("titles") { await TitlesPaneHarness.run(model: model, into: directory) }
            if groups.contains("history") { await captureHistory(model: model, into: directory) }
            if groups.contains("perfcard") { await PerfCardHarness.run(model: model, into: directory) }
            if groups.contains("update") { await captureUpdate(model: model, into: directory) }
            NSApp.appearance = nil
            NSApp.terminate(nil)
        }
    }

    // MARK: Surfaces

    /// The popover's glass shows what is behind it, which a window-only capture leaves out. A backdrop window (a light
    /// or a dark wallpaper-like gradient) goes behind it and the screen region is captured, never anything else on
    /// screen.
    private static func captureDashboard(model: AppModel, into directory: URL) async {
        let statusItem = StatusItemController(model: model)
        let dashboard = DashboardController(model: model)
        for (name, fraction) in [("cruise", 0.08), ("boost", 0.6)] {
            for dark in [false, true] {
                for wallpaper in [false, true] {
                    NSApp.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
                    PreviewRenderer.publish(model, speed: model.settings.redline * fraction)
                    guard let anchor = statusItem.anchorView else { return }
                    await bringForward()
                    // A popover that is still closing would swallow the next show.
                    for _ in 0..<20 where dashboard.isShown { try? await Task.sleep(for: .seconds(0.1)) }
                    dashboard.show(relativeTo: anchor)
                    try? await Task.sleep(for: .seconds(0.4))
                    guard let popover = dashboard.debugPopover?.contentViewController?.view.window else { continue }
                    // Never the menu bar: only the popover and the backdrop around it.
                    var region = popover.frame.insetBy(dx: -24, dy: -24)
                    if let visible = popover.screen?.visibleFrame { region.size.height = min(region.maxY, visible.maxY) - region.minY }
                    let backdrop = Backdrop(frame: region, dark: wallpaper, below: popover)
                    try? await Task.sleep(for: .seconds(1.4))
                    let suffix = "\(dark ? "dark" : "light")-on-\(wallpaper ? "dark" : "light")"
                    captureRegion(region, "dashboard-\(name)-\(suffix)", into: directory)
                    dashboard.close()
                    backdrop.close()
                    for _ in 0..<20 where dashboard.isShown { try? await Task.sleep(for: .seconds(0.1)) }
                }
            }
        }
        _ = statusItem
    }

    /// A borderless window with a soft gradient, standing in for the desktop picture behind glass.
    @MainActor
    private final class Backdrop {
        private let window: NSWindow

        init(frame: CGRect, dark: Bool, below other: NSWindow) {
            window = NSWindow(contentRect: frame, styleMask: [.borderless], backing: .buffered, defer: false)
            window.isReleasedWhenClosed = false
            window.ignoresMouseEvents = true
            window.hasShadow = false
            window.level = NSWindow.Level(rawValue: other.level.rawValue - 1)
            let colors: [Color] = dark
                ? [Color(red: 0.07, green: 0.10, blue: 0.24), Color(red: 0.24, green: 0.12, blue: 0.36), Color(red: 0.05, green: 0.22, blue: 0.30)]
                : [Color(red: 0.80, green: 0.88, blue: 0.98), Color(red: 0.96, green: 0.86, blue: 0.90), Color(red: 0.84, green: 0.95, blue: 0.90)]
            window.contentView = NSHostingView(rootView: LinearGradient(colors: colors, startPoint: .topLeading, endPoint: .bottomTrailing))
            window.order(.below, relativeTo: other.windowNumber)
        }

        func close() { window.orderOut(nil) }
    }

    /// Captures a screen region given in AppKit screen coordinates.
    private static func captureRegion(_ rect: CGRect, _ name: String, into directory: URL) {
        guard let screen = NSScreen.screens.first else { return }
        let top = screen.frame.maxY - rect.maxY
        let region = "\(Int(rect.minX)),\(Int(top)),\(Int(rect.width)),\(Int(rect.height))"
        let url = directory.appendingPathComponent("screen-\(name).png")
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        process.arguments = ["-x", "-R", region, url.path]
        try? process.run()
        process.waitUntilExit()
    }

    private static func captureSettings(model: AppModel, into directory: URL) async {
        PreviewRenderer.publish(model, speed: model.settings.redline * 0.3)
        TrafficPreviewData.publish(into: model.traffic)
        let settings = SettingsWindowController(model: model)
        for dark in [false, true] {
            NSApp.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            for pane in SettingsPane.allCases {
                settings.show(pane: pane)
                await bringForward(NSApp.windows.first { $0 is SettingsWindow })
                try? await Task.sleep(for: .seconds(1.0))
                capture(NSApp.windows.first { $0 is SettingsWindow && $0.isVisible }, "settings-\(pane.rawValue)", dark: dark, into: directory)
            }
            NSApp.windows.first { $0 is SettingsWindow }?.orderOut(nil)
        }
    }

    private static func captureHistory(model: AppModel, into directory: URL) async {
        let only = ProcessInfo.processInfo.environment["AGENT_SPEED_HISTORY_CASES"]?.split(separator: ",").map(String.init)
            ?? ["all-wide", "source-min", "analyzing-empty"]
        let shots = HistoryPreviews.cases.filter { !$0.tall && only.contains($0.name) }
        for item in shots {
            for dark in [false, true] {
                NSApp.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
                let viewModel = HistoryPreviews.prepare(item, model: model)
                let host = NSHostingController(rootView: HistoryView(model: viewModel).appEnvironment(model))
                host.sizingOptions = []
                let window = HistoryWindowController.makeWindow(content: host)
                let toolbar = HistoryToolbar(model: viewModel) {}
                window.toolbar = toolbar.toolbar
                window.title = model.l10n("history.title")
                window.setContentSize(item.size)
                window.center()
                // Wherever the pointer happens to be, it must not leave a hover state in the picture.
                window.ignoresMouseEvents = true
                await bringForward(window)
                try? await Task.sleep(for: .seconds(1.0))
                let name = "history-\(item.name)"
                capture(window, name, dark: dark, into: directory)
                if item.name == only.first, let popover = toolbar.debugShowCustomRange() {
                    try? await Task.sleep(for: .seconds(0.8))
                    capture(popover.contentViewController?.view.window, "history-custom-popover", dark: dark, into: directory)
                    popover.close()
                }
                window.orderOut(nil)
                _ = toolbar
            }
        }
        model.history.publish(state: .notLoaded, days: [])
    }

    private static func captureUpdate(model: AppModel, into directory: URL) async {
        model.updates.preview(status: .available(UpdatePreviews.release), installation: .idle, lastChecked: Date().addingTimeInterval(-600))
        let updates = UpdateWindowController(model: model)
        for dark in [false, true] {
            NSApp.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            updates.show()
            let window = NSApp.windows.first { $0.title == model.l10n("mac.update.windowTitle") && $0.isVisible }
            await bringForward(window)
            try? await Task.sleep(for: .seconds(1.0))
            capture(window, "update-available", dark: dark, into: directory)
            updates.close()
        }
    }

    // MARK: Helpers

    /// Cooperative activation alone may be declined, so ask again until the window is key (or give up).
    static func bringForward(_ window: NSWindow? = nil) async {
        for _ in 0..<8 {
            if let window, window.isKeyWindow { return }
            if window == nil, NSApp.isActive { return }
            NSApp.activate()
            _ = NSApp.perform(NSSelectorFromString("activateIgnoringOtherApps:"), with: true)
            window?.makeKeyAndOrderFront(nil)
            try? await Task.sleep(for: .seconds(0.3))
        }
    }

    static func capture(_ window: NSWindow?, _ name: String, dark: Bool, into directory: URL) {
        guard let window else {
            FileHandle.standardError.write(Data("screenshot \(name): no window\n".utf8))
            return
        }
        let url = directory.appendingPathComponent("screen-\(name)-\(dark ? "dark" : "light").png")
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        process.arguments = ["-x", "-o", "-l", String(window.windowNumber), url.path]
        try? process.run()
        process.waitUntilExit()
    }
}
#endif
