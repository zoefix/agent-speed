import AgentSpeedCore
import AppKit
import SwiftUI

/// `--render-previews` group `lan` (DESIGN 14): Settings → API on both segments, the 區域網路 segment off, on with
/// devices and targets in every status, with the network switch off, the Add / Edit sheet (empty, a broadcast, an
/// `https` error, a passed and a failed test, testing) and About with the local network paragraph, in every language
/// and appearance (`lan-<surface>-<language>-<scheme>.png`).
@MainActor
enum LanPreviews {
    static let languages: [LanguagePreference] = [.zhHant, .zhHans, .en, .ja]
    static let ids = (1...6).map { String(format: "8c0d6b0a-5a8e-4b0e-9d4e-%012d", $0) }

    static var document: LanDocument {
        LanDocument(instance: "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
                    server: .init(enabled: true, port: 7247, key: "K7Q4M2X9PT3D"),
                    targets: [
                        .init(id: ids[0], name: "Desk display", kind: .udp, address: "192.168.1.50:7247"),
                        .init(id: ids[1], name: "Home Assistant", kind: .http,
                              address: "http://homeassistant.local:8123/api/webhook/agent-speed", key: "s3cret"),
                        .init(id: ids[2], kind: .udp, address: "192.168.1.255:7247"),
                        .init(id: ids[3], name: "Workshop panel", kind: .http, address: "http://10.0.0.42/speed"),
                        .init(id: ids[4], name: "Old tablet", kind: .udp, address: "tablet.local:7247"),
                        .init(id: ids[5], name: "Spare", kind: .udp, address: "192.168.1.77:9000", enabled: false),
                    ])
    }

    static func statuses(now: Date) -> [String: LanTargetStatus] {
        [
            ids[0]: LanTargetStatus(state: .ok, lastSuccess: now.addingTimeInterval(-1)),
            ids[1]: LanTargetStatus(state: .ok, lastSuccess: now.addingTimeInterval(-24)),
            ids[2]: LanTargetStatus(state: .waiting),
            ids[3]: LanTargetStatus(state: .unreachable, retryAt: now.addingTimeInterval(8)),
            ids[4]: LanTargetStatus(state: .notLocal),
        ]
    }

    static func render(model: AppModel, into directory: URL) {
        let originalLanguage = model.settings.language
        let originalDocument = model.settings.lan
        let originalNetwork = model.settings.networkEnabled
        let originalSegment = model.lan.segment
        let now = Date()

        for language in languages {
            model.settings.language = language
            model.l10n.apply(language)
            for dark in [false, true] {
                let suffix = "\(language.rawValue)-\(dark ? "dark" : "light").png"
                func write<V: View>(_ name: String, _ view: V) {
                    PreviewSnapshot.writeHosted(view.appEnvironment(model), dark: dark,
                                                to: directory.appendingPathComponent("lan-\(name)-\(suffix)"))
                }
                model.settings.networkEnabled = true
                model.lan.segment = .report
                write("api-report", SettingsPaneView(pane: .report, model: model))

                model.lan.segment = .lan
                model.settings.lan = .empty
                model.lan.preview(server: .stopped, targets: [:], addresses: ["192.168.1.20"])
                write("pane-off", SettingsPaneView(pane: .report, model: model))

                model.settings.lan = document
                model.lan.preview(server: .init(state: .listening, port: 7247, devices: 2), targets: statuses(now: now),
                                  addresses: ["192.168.1.20", "10.0.0.8"])
                write("pane-on", SettingsPaneView(pane: .report, model: model))
                write("about", SettingsPaneView(pane: .about, model: model))

                model.lan.preview(server: .init(state: .portInUse, port: 7247, devices: 0), targets: statuses(now: now),
                                  addresses: [])
                write("pane-port", SettingsPaneView(pane: .report, model: model))

                model.settings.networkEnabled = false
                write("pane-network-off", SettingsPaneView(pane: .report, model: model))
                model.settings.networkEnabled = true

                for (name, sheet) in sheets(model) { write("sheet-\(name)", sheet) }
            }
        }

        model.settings.language = originalLanguage
        model.l10n.apply(originalLanguage)
        model.settings.lan = originalDocument
        model.settings.networkEnabled = originalNetwork
        model.lan.segment = originalSegment
    }

    private static func sheets(_ model: AppModel) -> [(String, LanTargetSheet)] {
        let targets = document.targets
        var broadcast = LanTargetDraft()
        broadcast.address = "192.168.1.255:7247"
        broadcast.name = "Every display"
        var https = LanTargetDraft()
        https.kind = .http
        https.address = "https://homeassistant.local/api/webhook/x"
        return [
            ("add", LanTargetSheet(model: model, draft: LanTargetDraft(), onClose: {})),
            ("broadcast", LanTargetSheet(model: model, draft: broadcast, onClose: {})),
            ("https", LanTargetSheet(model: model, draft: https, onClose: {})),
            ("edit", LanTargetSheet(model: model, draft: LanTargetDraft(editing: targets[1]),
                                    test: .finished(LanTargetStatus(state: .ok, lastSuccess: Date())), onClose: {})),
            ("testfail", LanTargetSheet(model: model, draft: LanTargetDraft(editing: targets[4]),
                                        test: .finished(LanTargetStatus(state: .notLocal)), onClose: {})),
            ("testing", LanTargetSheet(model: model, draft: LanTargetDraft(editing: targets[0]), test: .running,
                                       onClose: {})),
        ]
    }
}
