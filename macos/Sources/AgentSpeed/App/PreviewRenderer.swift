import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import SwiftUI

/// `--render-previews DIR`: renders every surface to PNG for visual review, then exits.
@MainActor
enum PreviewRenderer {
    static func run(into directory: URL) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let only = ProcessInfo.processInfo.environment["AGENT_SPEED_PREVIEWS"]?.split(separator: ",").map(String.init)
        func wants(_ group: String) -> Bool { only?.contains(group) ?? true }

        let suite = "AgentSpeedPreview"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        let model = AppModel(
            settings: AppSettings(defaults: defaults),
            updates: UpdateController(configuration: .current(environment: [:]), store: UpdateStore(defaults: defaults))
        )
        model.activate(live: false)
        let redline = model.settings.redline
        GaugeChecks.run(into: directory)
        if wants("nameplates") { NameplatePreviews.render(model: model, into: directory) }

        if wants("orb") {
            SurfacePreviews.renderOrbs(model: model, into: directory)
        }
        if wants("orbs") {
            OrbsPreviews.render(model: model, into: directory)
        }
        if wants("boards") {
            BoardSettingsPreviews.render(model: model, into: directory)
        }
        if wants("themes") {
            TitleThemePreviews.render(model: model, into: directory)
        }
        if wants("traffic") {
            TrafficOrbPreviews.render(model: model, into: directory)
            TrafficSurfacePreviews.render(model: model, into: directory)
        }
        if wants("intel") {
            IntelligencePreviews.render(model: model, into: directory)
        }
        if wants("agentintel") {
            AgentIntelligencePreviews.render(model: model, into: directory)
        }
        if wants("intelbar") {
            IntelligenceBarPreviews.render(model: model, into: directory)
        }
        if wants("cards") {
            TitleCardPreviews.render(model: model, into: directory)
        }
        if only?.contains("upgrade") == true {
            OrbsPreviews.renderUpgrade(model: model, into: directory)
        }
        if wants("menubar") {
            SurfacePreviews.renderMenuBar(model: model, into: directory)
        }
        if wants("titles") {
            TitlePreviews.render(model: model, into: directory)
        }
        if wants("hero") {
            HeroPreviews.render(model: model, into: directory)
        }
        if wants("badge") {
            HeroBadgePreviews.render(model: model, into: directory)
        }
        if only?.contains("herobench") == true {
            HeroBenchmark.run(model: model, into: directory)
        }
        if wants("dashboard") {
            let cases: [(String, Double, PreviewData.AgentSet, Bool)] = [
                ("cruise", redline * 0.08, .standard, false),
                ("redline", redline * 0.92, .standard, false),
                ("groups", redline * 0.6, .grouped, false),
                ("agents-collapsed", redline * 0.7, .crowd(16), false),
                ("agents-expanded", redline * 0.7, .crowd(10), true),
                ("agents-scrolling", redline * 0.7, .crowd(16), true),
            ]
            for (name, speed, agents, expanded) in cases {
                model.settings.agentsExpanded = expanded
                publish(model, speed: speed, agents: agents)
                for dark in [false, true] {
                    PreviewSnapshot.writeHosted(
                        DashboardView(model: model).appEnvironment(model).environment(\.orbStillTime, Clock.now),
                        dark: dark,
                        to: directory.appendingPathComponent("dashboard-\(name)-\(dark ? "dark" : "light").png")
                    )
                }
            }
            model.settings.agentsExpanded = false
            publish(model, speed: redline * 0.3)
            for dark in [false, true] {
                PreviewSnapshot.writeHosted(
                    DashboardView(model: model).appEnvironment(model)
                        .environment(\.orbStillTime, Clock.now)
                        .environment(\.previewHoveredSource, .claudeCode),
                    dark: dark,
                    to: directory.appendingPathComponent("dashboard-source-hover-\(dark ? "dark" : "light").png")
                )
            }
            publish(model, snapshot: PreviewData.withPartialSources(
                PreviewData.snapshot(speed: redline * 0.3, redline: model.settings.redline)
            ))
            for dark in [false, true] {
                PreviewSnapshot.writeHosted(
                    DashboardView(model: model).appEnvironment(model).environment(\.orbStillTime, Clock.now),
                    dark: dark,
                    to: directory.appendingPathComponent("dashboard-sources-\(dark ? "dark" : "light").png")
                )
            }
        }
        if wants("sources") {
            SourcePreviews.render(model: model, into: directory)
        }
        if wants("cost") {
            CostPreviews.render(model: model, into: directory)
        }
        if wants("modes") { ExecutionModePreviews.render(model: model, into: directory) }
        if wants("configured") { ConfiguredCapsulePreviews.render(model: model, into: directory) }
        #if DEBUG
        if wants("profilecard") { ProfileCardPreviews.render(model: model, into: directory) }
        #endif
        if wants("report") {
            ReportPreviews.render(model: model, into: directory)
        }
        if wants("history") {
            HistoryLogicChecks.run(l10n: model.l10n, into: directory)
            HistoryPreviews.render(model: model, into: directory)
        }
        if wants("update") {
            UpdatePreviews.render(model: model, into: directory)
        }
        if wants("perfcard") { PerfCardPreviews.render(model: model, into: directory) }
        if wants("perfhistory") { PerfHistoryPreviews.render(model: model, into: directory) }
        if wants("settings") {
            publish(model, speed: redline * 0.4)
            settleOrbs(model)
            for pane in SettingsPane.allCases {
                for dark in [false, true] {
                    PreviewSnapshot.writeHosted(
                        SettingsPaneView(pane: pane, model: model).appEnvironment(model).environment(\.orbStillTime, Clock.now),
                        dark: dark,
                        to: directory.appendingPathComponent("settings-\(pane.rawValue)-\(dark ? "dark" : "light").png")
                    )
                }
            }
            // Three orbs, the second selected with its own redline, and Advanced open.
            let orbs = model.orbs
            if let first = orbs.orbs.first?.id, let pair = orbs.add(copying: first), let single = orbs.add(copying: pair) {
                orbs.setSources(.explicit([.claudeCode, .claudeDesktop]), of: pair)
                orbs.setStyle(.needle, of: pair)
                orbs.setSources(.explicit([.codex]), of: single)
                orbs.setStyle(.liquid, of: single)
                orbs.setSize(128, of: single)
                orbs.setStyle(.reactor, of: first)
                orbs.setSize(112, of: first)
                orbs.setRedline(800, metric: model.settings.metric, of: pair)
                orbs.settingsSelection = pair
                orbs.dismissUndo()
                settleOrbs(model)
                for dark in [false, true] {
                    PreviewSnapshot.writeHosted(
                        OrbPane(model: model, advancedExpanded: true)
                            .frame(width: SettingsPaneView.width(for: .orb))
                            .fixedSize(horizontal: false, vertical: true)
                            .appEnvironment(model)
                            .environment(\.orbStillTime, Clock.now),
                        dark: dark,
                        to: directory.appendingPathComponent("settings-orb-advanced-\(dark ? "dark" : "light").png")
                    )
                }
            }
            renderTitleThemes(model, into: directory)
        }
    }

    /// Settings → General with two custom title sets, the first selected with its editor open (light, dark, English).
    static func renderTitleThemes(_ model: AppModel, into directory: URL) {
        let titles = model.titles
        guard let vehicles = titles.copy(TitlePreset.vehicles.rawValue),
              let custom = titles.copy(TitlePreset.cultivation.rawValue) else { return }
        titles.rename(custom, to: "我的修仙路")
        titles.commitField(level: 4, of: custom, text: "結丹")
        titles.commitField(level: 12, of: custom, text: "飛升")
        titles.commitField(level: 6, of: vehicles, text: "保時捷")
        titles.select(custom)
        func write(_ name: String, dark: Bool) {
            PreviewSnapshot.writeHosted(
                SettingsPaneView(pane: .general, model: model).appEnvironment(model).environment(\.orbStillTime, Clock.now),
                dark: dark,
                to: directory.appendingPathComponent("settings-general-titles-\(name).png")
            )
        }
        write("light", dark: false)
        write("dark", dark: true)
        let language = model.settings.language
        model.settings.language = .en
        model.l10n.apply(.en)
        write("en-light", dark: false)
        model.settings.language = language
        model.l10n.apply(language)
    }

    /// Lets the floating orbs read the published snapshot, then settles their gauges (Settings thumbnails).
    static func settleOrbs(_ model: AppModel) {
        RunLoop.main.run(until: Date().addingTimeInterval(0.05))
        for orb in model.orbs.orbs {
            guard let gauge = model.orbs.runtime(orb.id)?.feed.gauge else { continue }
            gauge.snap(to: gauge.rawValue, now: Clock.now - 3)
        }
    }

    static func publish(_ model: AppModel, speed: Double, agents: PreviewData.AgentSet = .standard) {
        publish(model, snapshot: PreviewData.snapshot(speed: speed, redline: model.settings.redline, agents: agents))
    }

    static func publish(_ model: AppModel, snapshot: SpeedSnapshot) {
        model.store.publishLive(snapshot)
        snapGauge(model, to: model.value(of: snapshot.live), now: Clock.now)
    }

    /// Snaps the gauge, title and tier to `value` at the current settings. The app syncs the gauge's redline from the
    /// settings asynchronously, so a preview that just changed the redline (the redline ladder renders) would
    /// otherwise snap against a stale one and show the wrong title and heat.
    static func snapGauge(_ model: AppModel, to value: Double, tier: SpeedTier? = nil, now: TimeInterval) {
        model.gauge.redline = model.settings.redline
        model.gauge.snap(to: value, tier: tier, now: now)
    }
}

@MainActor
enum PreviewSnapshot {
    static func writeImageRenderer<V: View>(_ view: V, dark: Bool, scale: CGFloat = 2, to url: URL) {
        let renderer = ImageRenderer(content: view.environment(\.colorScheme, dark ? .dark : .light))
        renderer.scale = scale
        renderer.isOpaque = false
        guard let image = renderer.cgImage else { return }
        write(image, to: url)
    }

    /// Renders through a real offscreen window so AppKit-backed controls draw too.
    static func writeHosted<V: View>(_ view: V, dark: Bool, background: NSColor = .windowBackgroundColor, to url: URL) {
        let appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        let host = NSHostingView(rootView: view
            .environment(\.colorScheme, dark ? .dark : .light)
            .background(Color(nsColor: background)))
        host.appearance = appearance
        let size = host.fittingSize
        host.frame = CGRect(origin: .zero, size: size)
        let window = NSWindow(contentRect: host.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.appearance = appearance
        window.backgroundColor = background
        window.contentView = host
        host.layoutSubtreeIfNeeded()
        host.displayIfNeeded()
        RunLoop.main.run(until: Date().addingTimeInterval(0.15))
        defer { release(window) }
        guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { return }
        host.cacheDisplay(in: host.bounds, to: rep)
        guard let image = rep.cgImage else { return }
        write(image, to: url)
    }

    /// AppKit keeps every window it created until it is closed: an unclosed snapshot window would keep its views alive
    /// and updating for the rest of the run, which slows (and with live sheets can stall) later renders.
    static func release(_ window: NSWindow) {
        window.contentView = nil
        window.close()
    }

    static func write(_ image: CGImage, to url: URL) {
        let rep = NSBitmapImageRep(cgImage: image)
        guard let data = rep.representation(using: .png, properties: [:]) else { return }
        try? data.write(to: url)
    }
}
