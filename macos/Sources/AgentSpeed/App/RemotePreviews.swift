import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import SwiftUI

/// `--render-previews` group `remote` (DESIGN 13): Settings → 遠端主機 empty and with eight hosts in every status (one
/// with history too large to sync), About with the remote hosts' privacy paragraph, the
/// Add / Edit sheet (empty, a field error, a test result, a failed test, testing), the orb editor's Hosts section and the
/// dashboard's Hosts section with remote agents, in every language and appearance
/// (`remote-<surface>-<language>-<scheme>.png`); the orb and board Hosts submenus and the toolbar fit as text.
@MainActor
enum RemotePreviews {
    static let languages: [LanguagePreference] = [.zhHant, .zhHans, .en, .ja]
    static let ids = (1...8).map { String(format: "8C0D6B0A-1111-4222-8333-%012d", $0) }

    static var document: RemoteHostsDocument {
        RemoteHostsDocument(hosts: [
            RemoteHost(id: ids[0], destination: "devbox", relayFolder: nil),
            RemoteHost(id: ids[1], destination: "zoe@gpu-01.lab.example.com"),
            RemoteHost(id: ids[2], destination: "ssh://ci@build-farm-ap-northeast-1.internal.example.com:2222",
                       name: "Build Farm · ap-northeast-1 (CI)"),
            RemoteHost(id: ids[3], destination: "pi@raspberrypi.local", name: "Raspberry Pi"),
            RemoteHost(id: ids[4], destination: "root@203.0.113.7"),
            RemoteHost(id: ids[5], destination: "lab-ubuntu"),
            RemoteHost(id: ids[6], destination: "admin@nas.home.arpa", name: "NAS"),
            RemoteHost(id: ids[7], destination: "old-thinkpad", enabled: false),
        ])
    }

    static func render(model: AppModel, into directory: URL) {
        let originalLanguage = model.settings.language
        let originalHosts = model.settings.remoteHosts
        let orbId = model.orbs.orbs.first?.id
        let originalOrb = orbId.flatMap { model.orbs.orb($0) }

        for language in languages {
            model.settings.language = language
            model.l10n.apply(language)
            for dark in [false, true] {
                let suffix = "\(language.rawValue)-\(dark ? "dark" : "light").png"
                func write<V: View>(_ name: String, _ view: V) {
                    PreviewSnapshot.writeHosted(
                        view.appEnvironment(model).environment(\.orbStillTime, Clock.now),
                        dark: dark, to: directory.appendingPathComponent("remote-\(name)-\(suffix)"))
                }

                model.settings.remoteHosts = .empty
                publish(model)
                write("pane-empty", SettingsPaneView(pane: .remote, model: model))

                model.settings.remoteHosts = document
                publish(model)
                write("pane-hosts", SettingsPaneView(pane: .remote, model: model))
                write("about", SettingsPaneView(pane: .about, model: model))

                for (name, sheet) in sheets(model) { write("sheet-\(name)", sheet) }

                if let orbId {
                    model.orbs.setSources(.explicit([.codex]), of: orbId)
                    model.orbs.setHosts(.explicit([RemoteHost.localId, ids[0]]), of: orbId)
                    if let orb = model.orbs.orb(orbId) {
                        write("orb-hosts", Form {
                            OrbSourcesSection(model: model, orb: orb) { OrbEditorHeading(model: model, orb: orb) }
                            HostPickerSection(model: model, selection: orb.hosts, set: { _ in }, toggle: { _ in })
                        }
                        .settingsForm()
                        .frame(width: SettingsPaneView.width(for: .orb)))
                    }
                }

                write("dashboard", DashboardView(model: model))
                write("dashboard-hover", DashboardView(model: model).environment(\.previewHoveredHost, ids[0]))
            }
        }
        writeMenus(model, orbId: orbId, to: directory.appendingPathComponent("remote-menus.txt"))
        writeToolbar(model, to: directory.appendingPathComponent("remote-toolbar.txt"))

        if let originalOrb {
            model.orbs.setSources(originalOrb.sources, of: originalOrb.id)
            model.orbs.setHosts(originalOrb.hosts, of: originalOrb.id)
        }
        model.settings.remoteHosts = originalHosts
        model.history.publish(remoteTooLarge: [])
        model.settings.language = originalLanguage
        model.l10n.apply(originalLanguage)
        PreviewRenderer.publish(model, speed: model.settings.redline * 0.3)
    }

    private static func sheets(_ model: AppModel) -> [(String, RemoteHostSheet)] {
        let hosts = model.settings.remoteHosts.hosts
        var invalid = RemoteHostDraft()
        invalid.destination = "zoe@dev box"
        invalid.name = "My very long remote host name that does not fit"
        var edited = RemoteHostDraft(editing: hosts[0])
        edited.relayFolder = "/srv/agent-speed"
        return [
            ("add", RemoteHostSheet(model: model, draft: RemoteHostDraft(), onClose: {})),
            ("error", RemoteHostSheet(model: model, draft: invalid, advancedExpanded: true, onClose: {})),
            ("edit", RemoteHostSheet(model: model, draft: edited,
                                     test: .finished(.supported(system: "Linux x86_64", arch: .x86_64)), onClose: {})),
            ("testfail", RemoteHostSheet(model: model, draft: RemoteHostDraft(editing: hosts[4]),
                                         test: .finished(.failed(.hostKeyUnknown)), onClose: {})),
            ("testing", RemoteHostSheet(model: model, draft: RemoteHostDraft(editing: hosts[1]), test: .running,
                                        onClose: {})),
        ]
    }

    /// The standard snapshot with two agents on remote hosts and every host's status and totals.
    private static func publish(_ model: AppModel) {
        let now = Date()
        var snapshot = PreviewData.snapshot(speed: model.settings.redline * 0.45, redline: model.settings.redline, now: now)
        guard model.hasRemoteHosts else {
            model.history.publish(remoteTooLarge: [])
            PreviewRenderer.publish(model, snapshot: snapshot)
            return
        }
        // The lab machine's history is beyond the bounds (REMOTE 8.3).
        model.history.publish(remoteTooLarge: [ids[5]])
        for (index, agent) in snapshot.agents.enumerated() {
            if agent.project == "rocket" { snapshot.agents[index].host = ids[0] }
            if agent.project == "quasar" { snapshot.agents[index].host = ids[5] }
        }
        let names = model.settings.remoteHosts.displayNames()
        func remote(_ index: Int, _ status: RemoteHostStatus, live: Double = 0, output: Int = 0, cost: Int64 = 0,
                    sessions: Int = 0) -> HostStatus {
            HostStatus(id: ids[index], name: names[ids[index]] ?? "", remote: status, live: Rate(output: live, total: live * 24),
                       today: TokenTotals(requests: output / 900, output: output),
                       todayCost: CostTally(costMicroUsd: cost), sessions: sessions,
                       isConnected: status.status == .connected)
        }
        let live = snapshot.live.output
        snapshot.hosts = [
            HostStatus(id: RemoteHost.localId, name: RemoteHost.localId, live: Rate(output: live * 0.52, total: live * 13),
                       today: TokenTotals(requests: 1_020, output: 948_000), todayCost: CostTally(costMicroUsd: 18_420_000),
                       sessions: 7, isConnected: true),
            remote(0, RemoteHostStatus(status: .connected, clockOffsetMs: 310, relayVersion: "1.7.0", relayArch: "x86_64"),
                   live: live * 0.34, output: 212_000, cost: 6_140_000, sessions: 3),
            remote(1, RemoteHostStatus(status: .seeding(fraction: 0.42))),
            remote(2, RemoteHostStatus(status: .unreachable, retryAt: now.addingTimeInterval(9.6),
                                       lastErrorAt: now.addingTimeInterval(-24)), output: 31_500, cost: 820_000),
            remote(3, RemoteHostStatus(status: .authFailed, retryAt: now.addingTimeInterval(240),
                                       lastErrorAt: now.addingTimeInterval(-60))),
            remote(4, RemoteHostStatus(status: .hostKeyUnknown, lastErrorAt: now.addingTimeInterval(-300))),
            remote(5, RemoteHostStatus(status: .connected, clockOffsetMs: -12_400, relayVersion: "1.7.0", relayArch: "aarch64"),
                   live: live * 0.14, output: 48_600, cost: 1_210_000, sessions: 1),
            remote(6, RemoteHostStatus(status: .unsupportedSystem(system: "FreeBSD amd64"))),
            remote(7, RemoteHostStatus(status: .disabled)),
        ]
        snapshot.hostSources = snapshot.hosts.map { host in
            HostSourceStatus(cell: HostSource(host: host.id, source: .codex), available: host.isConnected,
                             live: host.live, today: host.today, todayCost: host.todayCost)
        }
        PreviewRenderer.publish(model, snapshot: snapshot)
    }

    /// The Hosts submenus of an orb and a board, in every language.
    private static func writeMenus(_ model: AppModel, orbId: String?, to url: URL) {
        var lines: [String] = []
        model.settings.remoteHosts = document
        publish(model)
        let added = model.boards.boards.isEmpty ? model.boards.add() : nil
        let boardId = model.boards.boards.first?.id
        if let boardId { model.boards.setHosts(.explicit([ids[0], ids[5]]), of: boardId) }
        for language in languages {
            model.settings.language = language
            model.l10n.apply(language)
            var menus: [(String, NSMenu)] = []
            if let orbId { menus.append(("orb", OrbMenus.contextMenu(model: model, orbId: orbId))) }
            if let boardId { menus.append(("board", BoardMenus.contextMenu(model: model, boardId: boardId))) }
            for (kind, menu) in menus {
                lines.append("[\(language.rawValue) \(kind)] \(menu.items.first?.title ?? "")")
                let titles = menu.items.map(\.title)
                lines.append("  " + titles.filter { !$0.isEmpty }.joined(separator: " | "))
                guard let hosts = menu.items.first(where: { $0.title == model.l10n("remote.hosts") })?.submenu else {
                    lines.append("  (no Hosts submenu)")
                    continue
                }
                for entry in hosts.items {
                    let check = entry.state == .on ? "✓" : " "
                    let enabled = entry.isEnabled ? "" : "  (disabled: \(entry.toolTip ?? ""))"
                    lines.append(entry.isSeparatorItem ? "    ———" : "    \(check) \(entry.title)\(enabled)")
                }
            }
            lines.append("")
        }
        if let boardId { model.boards.setHosts(.all, of: boardId) }
        if let added { model.boards.remove(added) }
        try? lines.joined(separator: "\n").write(to: url, atomically: true, encoding: .utf8)
    }

    /// Whether all ten Settings tabs fit the toolbar at both pane widths in each language (an invisible window).
    private static func writeToolbar(_ model: AppModel, to url: URL) {
        var lines: [String] = []
        for language in languages {
            model.settings.language = language
            model.l10n.apply(language)
            for width in [SettingsPaneView.width, SettingsPaneView.wideWidth] {
                let tabs = SettingsTabViewController(model: model)
                let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: width, height: 400),
                                      styleMask: [.titled, .closable], backing: .buffered, defer: false)
                window.isReleasedWhenClosed = false
                window.contentViewController = tabs
                window.toolbarStyle = .preference
                window.alphaValue = 0
                tabs.select(.remote)
                tabs.applyTitles()
                window.setContentSize(NSSize(width: width, height: 400))
                window.orderFront(nil)
                RunLoop.main.run(until: Date().addingTimeInterval(0.3))
                let visible = window.toolbar?.visibleItems?.count ?? -1
                let total = window.toolbar?.items.count ?? -1
                let labels = tabs.tabViewItems.map(\.label).joined(separator: " · ")
                lines.append("\(language.rawValue) @\(Int(width)): \(visible) of \(total) visible — \(labels)")
                window.orderOut(nil)
                window.contentViewController = nil
            }
        }
        try? lines.joined(separator: "\n").write(to: url, atomically: true, encoding: .utf8)
    }
}
