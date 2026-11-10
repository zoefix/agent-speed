import AgentSpeedCore
import AppKit
import SwiftUI

/// `AGENT_SPEED_PREVIEWS=quota2`: the phase 2 quotas (QUOTAS 9; DESIGN 4, 5 *額度, phase 2*, 13 *Host quotas*): the
/// dashboard's Sources rows with named windows, months and credits, Dashboard → Hosts with each host's source
/// sub-lines, every new Settings group in its states, every status line, the browser consent dialog and the host
/// editor's bridge switch, light and dark. No keychain, browser, process or network is touched.
@MainActor
enum QuotaVendorPreviews {
    static let devbox = "8C0D6B0A-1111-4222-8333-000000000001"
    static let gpu = "8C0D6B0A-1111-4222-8333-000000000002"
    static let hour: Int64 = 3_600_000
    static let minute: Int64 = 60_000

    static func render(model: AppModel, into directory: URL) {
        let now = QuotaController.nowMs()
        let settings = model.settings
        settings.quotaQoder = true
        settings.quotaWorkBuddy = false
        settings.quotaZcode = true
        settings.quotaGrokBuildLive = true
        publish(model, now: now)
        model.quota.preview(desktop: nil, cli: nil, apps: QuotaApps(agents: [.antigravity, .kimi, .qoderCli]),
                            vendor: readings(now), nowMs: now)
        for dark in [false, true] {
            write(sources(model), dark: dark, to: directory, "quota2-dashboard-sources-\(dark ? "dark" : "light")")
        }

        // Dashboard → Hosts with each host's quota sub-lines.
        let originalHosts = settings.remoteHosts
        var box = RemoteHost(id: devbox, destination: "devbox")
        box.quotaClaudeCli = true
        settings.remoteHosts = RemoteHostsDocument(hosts: [box, RemoteHost(id: gpu, destination: "zoe@gpu-01.lab")])
        publishHosts(model, now: now)
        model.quota.preview(desktop: nil, cli: nil, vendor: readings(now), hosts: hostQuotas(now), nowMs: now)
        for dark in [false, true] {
            write(hosts(model), dark: dark, to: directory, "quota2-dashboard-hosts-\(dark ? "dark" : "light")")
        }
        let sheet = RemoteHostSheet(model: model, draft: RemoteHostDraft(editing: box), advancedExpanded: false) {}
        for dark in [false, true] {
            write(sheet.appEnvironment(model), dark: dark, to: directory, "quota2-host-editor-\(dark ? "dark" : "light")")
        }
        var replaced = hostQuotas(now)
        replaced[devbox]?.bridge?.state = .replaced
        model.quota.preview(desktop: nil, cli: nil, vendor: readings(now), hosts: replaced, nowMs: now)
        write(sheet.appEnvironment(model), dark: false, to: directory, "quota2-host-editor-replaced-light")
        settings.remoteHosts = originalHosts

        // Settings → Data Sources, each new group.
        for (name, saved) in [("saved", true), ("empty", false)] {
            model.quota.preview(desktop: nil, cli: nil, vendor: readings(now), zcodeKeySaved: saved, nowMs: now)
            for dark in name == "saved" ? [false, true] : [false] {
                write(settingsGroups(model), dark: dark, to: directory, "quota2-settings-\(name)-\(dark ? "dark" : "light")")
            }
        }
        settings.quotaQoder = false
        settings.quotaWorkBuddy = true
        model.quota.preview(desktop: nil, cli: nil, vendor: readings(now), zcodeKeySaved: true, nowMs: now)
        write(browserGroups(model), dark: false, to: directory, "quota2-settings-browser-light")
        write(browserGroups(model), dark: true, to: directory, "quota2-settings-browser-dark")
        settings.networkEnabled = false
        write(settingsGroups(model), dark: false, to: directory, "quota2-settings-networkoff-light")
        settings.networkEnabled = true
        // A source that is off reads and shows no quota: its quota rows dim (QUOTAS 9.1).
        for source in [SourceKind.kimi, .workbuddy] { settings.setEnabled(source, false) }
        write(browserGroups(model), dark: false, to: directory, "quota2-settings-source-off-light")
        write(settingsGroups(model), dark: true, to: directory, "quota2-settings-source-off-dark")
        for source in [SourceKind.kimi, .workbuddy] { settings.setEnabled(source, true) }
        for dark in [false, true] {
            write(statusGallery(model, now: now), dark: dark, to: directory, "quota2-status-lines-\(dark ? "dark" : "light")")
        }
        for route in [QuotaRoute.qoder, .workbuddy] {
            for dark in [false, true] {
                writeAlert(QuotaCookieConsent.alert(route, l10n: model.l10n), dark: dark,
                           to: directory.appendingPathComponent("quota2-consent-\(route.rawValue)-\(dark ? "dark" : "light").png"))
            }
        }
        // English: the longest labels (`credits`, `mo`) and sentences.
        let language = settings.language
        settings.language = .en
        model.l10n.apply(.en)
        settings.quotaQoder = true
        publish(model, now: now)
        model.quota.preview(desktop: nil, cli: nil, apps: QuotaApps(agents: [.antigravity, .kimi, .qoderCli]),
                            vendor: readings(now), zcodeKeySaved: true, nowMs: now)
        write(sources(model), dark: false, to: directory, "quota2-dashboard-sources-en-light")
        write(statusGallery(model, now: now), dark: false, to: directory, "quota2-status-lines-en-light")
        write(browserGroups(model), dark: false, to: directory, "quota2-settings-browser-en-light")
        settings.language = language
        model.l10n.apply(language)
        settings.quotaZcode = false
        settings.quotaGrokBuildLive = false
    }

    // MARK: Data

    static func readings(_ now: Int64) -> QuotaVendorReadings {
        func reading(_ source: SourceKind, _ windows: [QuotaWindow], ago: Int64 = 4 * minute,
                     status: QuotaRouteStatus? = nil) -> QuotaRouteReading {
            QuotaRouteReading(snapshot: QuotaSnapshot(source: source, observedMs: now - ago, windows: windows),
                              status: status ?? .ok(now - ago), signedIn: true)
        }
        return QuotaVendorReadings(routes: [
            .antigravity: reading(.antigravity, [
                QuotaWindow(kind: .fiveHour, name: "Gemini", usedPercent: 25, resetsAtMs: now + 2 * hour + 12 * minute),
                QuotaWindow(kind: .sevenDay, name: "Gemini", usedPercent: 60, resetsAtMs: now + 53 * hour),
                QuotaWindow(kind: .sevenDay, name: "Claude/GPT", usedPercent: 83, resetsAtMs: now + 98 * hour),
            ]),
            .kimi: reading(.kimi, [
                QuotaWindow(kind: .fiveHour, usedPercent: 23, resetsAtMs: now + 3 * hour + 40 * minute),
                QuotaWindow(kind: .sevenDay, usedPercent: 41, resetsAtMs: now + 77 * hour),
                QuotaWindow(kind: .month, usedPercent: 12.5, resetsAtMs: now + 20 * 24 * hour),
            ]),
            .qoder: reading(.qoder, [QuotaWindow(kind: .credits, usedPercent: 37, resetsAtMs: now + 20 * 24 * hour)]),
            .grokBuild: reading(.grokBuild, [QuotaWindow(kind: .month, minutes: 43200, usedPercent: 64,
                                                         resetsAtMs: now + 12 * 24 * hour)], ago: 3 * hour),
            .grokBuildLive: QuotaRouteReading(status: .failed),
            .devin: reading(.devin, [QuotaWindow(kind: .other, minutes: 1440, usedPercent: 25, resetsAtMs: now + 9 * hour),
                                     QuotaWindow(kind: .sevenDay, usedPercent: 60, resetsAtMs: now + 100 * hour)], ago: 2 * hour),
            .gemini: QuotaRouteReading(status: .denied(host: QuotaHosts.gemini)),
            .droid: QuotaRouteReading(status: .noCredential),
            .zcode: reading(.zcode, [QuotaWindow(kind: .fiveHour, usedPercent: 30, resetsAtMs: now + 2 * hour),
                                     QuotaWindow(kind: .month, name: "MCP", usedPercent: 40, resetsAtMs: now + 20 * 24 * hour)]),
            .workbuddy: QuotaRouteReading(status: .noBrowser),
        ], antigravityRuns: true)
    }

    static func hostQuotas(_ now: Int64) -> [String: HostQuotaState] {
        func report(_ host: String, _ source: SourceKind, _ windows: [QuotaWindow]) -> RemoteQuotaReport {
            RemoteQuotaReport(hostId: host, snapshot: QuotaSnapshot(source: source, observedMs: now - 3 * minute, windows: windows),
                              running: true, signedIn: true, status: .ok(now - 3 * minute))
        }
        return [
            devbox: HostQuotaState(reports: [
                .codex: report(devbox, .codex, [QuotaWindow(kind: .sevenDay, usedPercent: 40, resetsAtMs: now + 80 * hour)]),
                .claudeCode: report(devbox, .claudeCode, [
                    QuotaWindow(kind: .fiveHour, usedPercent: 6, resetsAtMs: now + 4 * hour + 10 * minute),
                    QuotaWindow(kind: .sevenDay, usedPercent: 86, resetsAtMs: now + 50 * hour),
                ]),
                .kimi: report(devbox, .kimi, [QuotaWindow(kind: .fiveHour, usedPercent: 52, resetsAtMs: now + 70 * minute)]),
            ], bridge: RemoteQuotaBridge(hostId: devbox, state: .ok, settingsPath: "~/.claude/settings.json")),
            gpu: HostQuotaState(reports: [
                .gemini: report(gpu, .gemini, [
                    QuotaWindow(kind: .other, name: "Pro", minutes: 1440, usedPercent: 40, resetsAtMs: now + 9 * hour),
                    QuotaWindow(kind: .other, name: "Flash", minutes: 1440, usedPercent: 3, resetsAtMs: now + 9 * hour),
                ]),
            ]),
        ]
    }

    /// The phase 2 sources producing today, with Codex beside them.
    static func publish(_ model: AppModel, now: Int64) {
        var snapshot = PreviewData.snapshot(speed: model.settings.redline * 0.3, redline: model.settings.redline)
        let kept: Set<SourceKind> = [.antigravity, .kimi, .qoder, .qoderCli]
        snapshot.sources = snapshot.sources.map { source in
            guard !kept.contains(source.kind) else { return source }
            var idle = source
            idle.today = TokenTotals()
            idle.todayCost = CostTally()
            idle.live = .zero
            return idle
        }
        snapshot.agents = snapshot.agents.filter { kept.contains($0.source) }
        snapshot.codexQuota = nil
        PreviewRenderer.publish(model, snapshot: snapshot)
    }

    static func publishHosts(_ model: AppModel, now: Int64) {
        var snapshot = PreviewData.snapshot(speed: model.settings.redline * 0.3, redline: model.settings.redline)
        let live = snapshot.live.output
        let names = model.settings.remoteHosts.displayNames()
        snapshot.hosts = [
            HostStatus(id: RemoteHost.localId, name: RemoteHost.localId, live: Rate(output: live * 0.6, total: live * 14),
                       today: TokenTotals(requests: 1_020, output: 948_000), todayCost: CostTally(costMicroUsd: 18_420_000),
                       sessions: 7, isConnected: true),
            HostStatus(id: devbox, name: names[devbox] ?? "devbox",
                       remote: RemoteHostStatus(status: .connected, clockOffsetMs: 310, relayVersion: "1.7.0", relayArch: "x86_64"),
                       live: Rate(output: live * 0.3, total: live * 8), today: TokenTotals(requests: 240, output: 212_000),
                       todayCost: CostTally(costMicroUsd: 6_140_000), sessions: 3, isConnected: true),
            HostStatus(id: gpu, name: names[gpu] ?? "gpu-01.lab",
                       remote: RemoteHostStatus(status: .connected, clockOffsetMs: -40, relayVersion: "1.7.0", relayArch: "aarch64"),
                       live: Rate(output: live * 0.1, total: live * 3), today: TokenTotals(requests: 60, output: 48_600),
                       todayCost: CostTally(costMicroUsd: 1_210_000), sessions: 1, isConnected: true),
        ]
        PreviewRenderer.publish(model, snapshot: snapshot)
    }

    // MARK: Surfaces

    private static func sources(_ model: AppModel) -> some View {
        SourcesSection(model: model)
            .padding(DashboardLayout.padding)
            .frame(width: DashboardController.width)
            .appEnvironment(model)
    }

    private static func hosts(_ model: AppModel) -> some View {
        HostsSection(model: model)
            .padding(DashboardLayout.padding)
            .frame(width: DashboardController.width)
            .appEnvironment(model)
    }

    private static func settingsGroups(_ model: AppModel) -> some View {
        Form {
            ForEach([TranscriptFolder.grokBuild, .devin, .antigravity, .kimi, .gemini, .droid, .zcode], id: \.self) { folder in
                Section {
                    ForEach(folder.sources) { SourceToggleRow(kind: $0, model: model) }
                    QuotaSwitches(folder: folder, model: model)
                }
            }
        }
        .settingsForm()
        .frame(width: SettingsPaneView.width(for: .sources))
        .fixedSize(horizontal: false, vertical: true)
        .appEnvironment(model)
    }

    private static func browserGroups(_ model: AppModel) -> some View {
        Form {
            ForEach([TranscriptFolder.qoder, .workbuddy], id: \.self) { folder in
                Section {
                    ForEach(folder.sources) { SourceToggleRow(kind: $0, model: model) }
                    QuotaSwitches(folder: folder, model: model)
                }
            }
        }
        .settingsForm()
        .frame(width: SettingsPaneView.width(for: .sources))
        .fixedSize(horizontal: false, vertical: true)
        .appEnvironment(model)
    }

    /// Every status line of DESIGN 5 in one group.
    private static func statusGallery(_ model: AppModel, now: Int64) -> some View {
        let statuses: [(QuotaRouteStatus, QuotaRoute)] = [
            (.ok(now - 4 * minute), .kimi), (.notRunning, .kimi), (.networkOff, .kimi), (.noCredential, .droid),
            (.noBrowser, .qoder), (.expired, .kimi), (.denied(host: QuotaHosts.gemini), .gemini), (.failed, .grokBuildLive),
        ]
        return Form {
            Section {
                ForEach(Array(statuses.enumerated()), id: \.offset) { _, item in
                    HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
                        QuotaIndent()
                        QuotaStatusLabel(status: item.0, text: QuotaStatusText.text(item.0, route: item.1, model: model))
                        Spacer(minLength: 0)
                    }
                }
            }
        }
        .settingsForm()
        .frame(width: SettingsPaneView.width(for: .sources))
        .fixedSize(horizontal: false, vertical: true)
        .appEnvironment(model)
    }

    private static func write(_ view: some View, dark: Bool, to directory: URL, _ name: String) {
        PreviewSnapshot.writeHosted(view, dark: dark, to: directory.appendingPathComponent("\(name).png"))
    }

    /// An alert's panel as it would open, drawn offscreen over the window background its material shows (offscreen,
    /// the material itself draws nothing), with the app's icon an unbundled run lacks.
    private static func writeAlert(_ alert: NSAlert, dark: Bool, to url: URL) {
        let appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        let icon = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("assets/icon/AgentSpeed.icns")
        if let image = NSImage(contentsOf: icon) { alert.icon = image }
        alert.window.appearance = appearance
        alert.layout()
        guard let view = alert.window.contentView else { return }
        view.appearance = appearance
        view.layoutSubtreeIfNeeded()
        view.displayIfNeeded()
        RunLoop.main.run(until: Date().addingTimeInterval(0.1))
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        defer { PreviewSnapshot.release(alert.window) }
        guard let content = rep.cgImage,
              let context = CGContext(data: nil, width: content.width, height: content.height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
        var background = NSColor.windowBackgroundColor.cgColor
        appearance?.performAsCurrentDrawingAppearance { background = NSColor.windowBackgroundColor.cgColor }
        let bounds = CGRect(x: 0, y: 0, width: content.width, height: content.height)
        context.setFillColor(background)
        context.addPath(CGPath(roundedRect: bounds, cornerWidth: 28, cornerHeight: 28, transform: nil))
        context.fillPath()
        context.draw(content, in: bounds)
        if let image = context.makeImage() { PreviewSnapshot.write(image, to: url) }
    }
}
