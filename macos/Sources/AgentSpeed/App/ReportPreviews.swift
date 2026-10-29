import AgentSpeedCore
import AppKit
import SwiftUI

/// Live reporting for `--render-previews` (group `report`, DESIGN 11): Settings → API, the consent sheet and the
/// dashboard invitation in all four languages, light and dark, plus the off, paused, attention, set-up-failed and
/// review states, the invitation's done line, the preview window's content (also before the document is known, and
/// that body in full down to its `perfScore` objects and statistics rows), the dashboard footer's indicator and the quick menu item, and
/// the WebSocket transport (streaming with its stats in every language, throttled, unsupported, reconnecting,
/// replaced, and its preview). Nothing is created, stored or sent.
@MainActor
enum ReportPreviews {
    static let languages: [(String, LanguagePreference, String)] = [
        ("zhhant", .zhHant, "小明的 MacBook Pro"),
        ("zhhans", .zhHans, "小明的 MacBook Pro"),
        ("en", .en, "Zoes MacBook Pro"),
        ("ja", .ja, "ゾーイの MacBook Pro"),
    ]

    static func render(model: AppModel, into directory: URL) {
        let saved = model.settings.language
        defer {
            apply(model, saved)
            model.report.preview(.off, username: "")
        }
        PreviewRenderer.publish(model, speed: model.settings.redline * 0.2)
        for (name, language, username) in languages {
            apply(model, language)
            for dark in [false, true] {
                let scheme = dark ? "dark" : "light"
                model.report.preview(.reporting, username: username)
                write(pane: model, dark: dark, to: directory.appendingPathComponent("report-settings-\(name)-\(scheme).png"))
                model.report.preview(.consent, username: username)
                write(consent: model, dark: dark, to: directory.appendingPathComponent("report-consent-\(name)-\(scheme).png"))
                model.report.preview(.invite, username: username)
                write(dashboard: model, dark: dark, to: directory.appendingPathComponent("report-invite-\(name)-\(scheme).png"))
                model.report.preview(.off, username: username)
                write(pane: model, dark: dark, to: directory.appendingPathComponent("report-settings-off-\(name)-\(scheme).png"))
                model.report.preview(.streaming, username: username)
                write(pane: model, dark: dark, to: directory.appendingPathComponent("report-settings-ws-\(name)-\(scheme).png"))
            }
        }
        for (name, language, username) in [languages[0], languages[2]] {
            apply(model, language)
            for (state, label) in [(ReportController.PreviewState.streamThrottled, "throttled"),
                                   (.streamUnsupported, "unsupported"), (.streamReconnecting, "reconnecting"),
                                   (.streamReplaced, "replaced")] {
                model.report.preview(state, username: username)
                for dark in [false, true] {
                    write(pane: model, dark: dark, to: directory.appendingPathComponent(
                        "report-settings-ws-\(label)-\(name)-\(dark ? "dark" : "light").png"))
                }
            }
            model.report.preview(.streaming, username: username)
            for dark in [false, true] {
                PreviewSnapshot.writeHosted(
                    ReportPreviewPanel(model: model).frame(width: 560, height: 640).appEnvironment(model), dark: dark,
                    to: directory.appendingPathComponent("report-preview-window-ws-\(name)-\(dark ? "dark" : "light").png"))
            }
        }
        apply(model, .zhHant)
        for dark in [false, true] {
            model.report.preview(.inviteDone, username: "小明的 MacBook Pro")
            write(dashboard: model, dark: dark, to: directory.appendingPathComponent("report-invite-done-\(dark ? "dark" : "light").png"))
        }
        for (state, label) in [(ReportController.PreviewState.setupFailed, "setupfailed"), (.review, "review"),
                               (.paused, "paused"), (.attention, "attention")] {
            model.report.preview(state, username: "小明的 MacBook Pro")
            for dark in [false, true] {
                write(pane: model, dark: dark,
                      to: directory.appendingPathComponent("report-settings-\(label)-\(dark ? "dark" : "light").png"))
            }
        }
        model.report.preview(.reporting, username: "小明的 MacBook Pro")
        for dark in [false, true] {
            let scheme = dark ? "dark" : "light"
            PreviewSnapshot.writeHosted(
                ReportPreviewBox(model: model, preview: model.report.preview(), showsRequest: true, maxHeight: 900)
                    .frame(width: 528).padding(Spacing.m).appEnvironment(model),
                dark: dark, to: directory.appendingPathComponent("report-preview-window-\(scheme).png"))
            model.report.preview(.invite, username: "小明的 MacBook Pro")
            PreviewSnapshot.writeHosted(
                ReportPreviewBox(model: model, preview: model.report.preview(), showsRequest: true, maxHeight: 900)
                    .frame(width: 528).padding(Spacing.m).appEnvironment(model),
                dark: dark, to: directory.appendingPathComponent("report-preview-window-assumed-\(scheme).png"))
            PreviewSnapshot.writeHosted(
                ReportPreviewBox(model: model, preview: model.report.preview(), showsRequest: true, maxHeight: 6000)
                    .frame(width: 528).padding(Spacing.m).appEnvironment(model),
                dark: dark, to: directory.appendingPathComponent("report-preview-body-assumed-\(scheme).png"))
            for (state, label) in [(ReportController.PreviewState.reporting, "on"), (.paused, "paused"),
                                   (.attention, "attention"), (.setupFailed, "setupfailed")] {
                model.report.preview(state, username: "小明的 MacBook Pro")
                PreviewSnapshot.writeHosted(
                    DashboardFooter(model: model).frame(width: 360).padding(Spacing.s).appEnvironment(model),
                    dark: dark, to: directory.appendingPathComponent("report-footer-\(label)-\(scheme).png"))
            }
        }
        model.report.preview(.reporting, username: "小明的 MacBook Pro")
        writeMenu(model, to: directory.appendingPathComponent("report-menu.txt"))
        writeToolbar(model, to: directory.appendingPathComponent("report-toolbar.txt"))
        writeSheetSize(model, to: directory.appendingPathComponent("report-sheet.txt"))
    }

    /// The consent sheet as SwiftUI presents it on Settings (an invisible window): its height against the screen.
    private static func writeSheetSize(_ model: AppModel, to url: URL) {
        var lines: [String] = []
        for (name, language, username) in languages {
            apply(model, language)
            model.report.preview(.off, username: username)
            let host = NSHostingController(rootView: SettingsPaneView(pane: .report, model: model).appEnvironment(model))
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: SettingsPaneView.width, height: 700),
                                  styleMask: [.titled, .closable], backing: .buffered, defer: false)
            window.contentViewController = host
            window.alphaValue = 0
            window.orderFront(nil)
            RunLoop.main.run(until: Date().addingTimeInterval(0.3))
            model.report.preview(.consent, username: username)
            RunLoop.main.run(until: Date().addingTimeInterval(1.5))
            let sheet = window.attachedSheet?.frame.size ?? .zero
            let screen = NSScreen.main?.visibleFrame.height ?? 0
            lines.append("\(name): sheet \(Int(sheet.width)) × \(Int(sheet.height)) pt, screen \(Int(screen)) pt")
            model.report.cancelConsent()
            RunLoop.main.run(until: Date().addingTimeInterval(0.8))
            window.orderOut(nil)
            window.contentViewController = nil
        }
        try? lines.joined(separator: "\n").write(to: url, atomically: true, encoding: .utf8)
    }

    /// Whether every Settings tab fits the toolbar at both pane widths in each language (an invisible window).
    private static func writeToolbar(_ model: AppModel, to url: URL) {
        var lines: [String] = []
        for (name, language, _) in languages {
            apply(model, language)
            for width in [SettingsPaneView.width, SettingsPaneView.wideWidth] {
                let tabs = SettingsTabViewController(model: model)
                let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: width, height: 400),
                                      styleMask: [.titled, .closable], backing: .buffered, defer: false)
                window.contentViewController = tabs
                window.toolbarStyle = .preference
                window.alphaValue = 0
                tabs.select(.cost)
                tabs.applyTitles()
                window.setContentSize(NSSize(width: width, height: 400))
                window.orderFront(nil)
                RunLoop.main.run(until: Date().addingTimeInterval(0.3))
                let visible = window.toolbar?.visibleItems?.count ?? -1
                let total = window.toolbar?.items.count ?? -1
                lines.append("\(name) @\(Int(width)): \(visible) of \(total) toolbar items visible")
                window.orderOut(nil)
            }
        }
        try? lines.joined(separator: "\n").write(to: url, atomically: true, encoding: .utf8)
    }

    private static func apply(_ model: AppModel, _ language: LanguagePreference) {
        model.settings.language = language
        model.l10n.apply(language)
    }

    /// The whole dashboard, so the invitation is seen where it lives (above the hero).
    private static func write(dashboard model: AppModel, dark: Bool, to url: URL) {
        PreviewSnapshot.writeHosted(
            DashboardView(model: model).environment(\.orbStillTime, Clock.now).appEnvironment(model), dark: dark, to: url)
    }

    private static func write(pane model: AppModel, dark: Bool, to url: URL) {
        PreviewSnapshot.writeHosted(
            SettingsPaneView(pane: .report, model: model).appEnvironment(model), dark: dark, to: url)
    }

    /// The sheet sizes itself from measured heights, so it is laid out twice before it is captured.
    private static func write(consent model: AppModel, dark: Bool, to url: URL) {
        let appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        let host = NSHostingView(rootView: ReportConsentSheet(model: model, maxHeight: 4000).appEnvironment(model)
            .environment(\.colorScheme, dark ? .dark : .light)
            .background(Color(nsColor: .windowBackgroundColor)))
        host.appearance = appearance
        let window = NSWindow(contentRect: CGRect(x: 0, y: 0, width: ReportConsentSheet.width, height: 600),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.appearance = appearance
        window.contentView = host
        for _ in 0..<3 {
            host.frame = CGRect(origin: .zero, size: host.fittingSize)
            window.setContentSize(host.fittingSize)
            host.layoutSubtreeIfNeeded()
            RunLoop.main.run(until: Date().addingTimeInterval(0.15))
        }
        host.displayIfNeeded()
        defer { PreviewSnapshot.release(window) }
        guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { return }
        host.cacheDisplay(in: host.bounds, to: rep)
        if let image = rep.cgImage { PreviewSnapshot.write(image, to: url) }
    }

    /// The quick menu's item and submenu as text (menus cannot be captured offscreen).
    private static func writeMenu(_ model: AppModel, to url: URL) {
        var lines: [String] = []
        for (_, language, _) in languages {
            apply(model, language)
            guard let item = ReportMenu.item(model: model) else { continue }
            lines.append(item.title)
            for entry in item.submenu?.items ?? [] { lines.append(entry.isSeparatorItem ? "  ———" : "  " + entry.title) }
            lines.append("")
        }
        try? lines.joined(separator: "\n").write(to: url, atomically: true, encoding: .utf8)
    }
}
