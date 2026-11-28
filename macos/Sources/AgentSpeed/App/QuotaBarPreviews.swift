import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import SwiftUI

/// `--render-previews` group `quotabar`: the menu bar item with the quota block (DESIGN 10, Menu bar and taskbar,
/// *Quota*) in every form — two windows used and remaining, more windows, one window, an extrapolated percent, red from
/// 95 %, an old snapshot, a sign-in that no longer works, no data, the switch off, long labels — in Chinese, English and
/// Japanese on dark and light bars; with Increase Contrast; after the money, network and intelligence blocks and in
/// value-only mode; the block's own bitmap at 2× and 1× magnified to the pixel; 選單列 with a source chosen; and the
/// tooltip and spoken words of every form in `quotabar-words.txt`. A red hairline marks the item's end.
@MainActor
enum QuotaBarPreviews {
    struct Scenario {
        var name: String
        var source: SourceKind
        var percent: BarQuotaPercent = .used
        var setup: @MainActor (AppModel, Int64) -> Void
    }

    static let minute: Int64 = 60_000
    static let hour: Int64 = 3_600_000

    static func render(model: AppModel, into directory: URL) {
        let settings = model.settings
        let saved = (settings.language, settings.barTraffic, settings.barMode, settings.barSecondary,
                     settings.barIntelligence, settings.barQuota, settings.barQuotaPercent, settings.quotaCodex)
        defer {
            settings.language = saved.0
            model.l10n.apply(saved.0)
            settings.barTraffic = saved.1
            settings.barMode = saved.2
            settings.barSecondary = saved.3
            settings.barIntelligence = saved.4
            settings.barQuota = saved.5
            settings.barQuotaPercent = saved.6
            settings.quotaCodex = saved.7
            model.quota.estimateLines = [:]
        }
        settings.trafficEnabled = true
        TrafficPreviewData.publish(into: model.traffic)
        settings.barTraffic = .off
        settings.barIntelligence = false
        settings.barMode = .glyphValue
        settings.barSecondary = false
        var words: [String] = []
        for language in [LanguagePreference.zhHant, .en, .ja] {
            settings.language = language
            model.l10n.apply(language)
            for dark in [false, true] {
                let rows = scenarios.map { item(model, $0, dark: dark) }
                SurfacePreviews.writeColumn(rows, to: directory.appendingPathComponent("quotabar-\(language.rawValue)-\(dark ? "dark" : "light").png"))
            }
            for scenario in scenarios {
                let state = state(model, scenario)
                words.append("[\(language.rawValue)] \(scenario.name)")
                words += (state.quota?.tooltip ?? []).map { "  tooltip: \($0)" }
                words.append("  spoken:  \(state.quota?.spoken ?? "")")
            }
        }
        try? words.joined(separator: "\n").write(to: directory.appendingPathComponent("quotabar-words.txt"), atomically: true,
                                                 encoding: .utf8)
        settings.language = .zhHant
        model.l10n.apply(.zhHant)
        for dark in [false, true] {
            let scheme = dark ? "dark" : "light"
            let contrast = scenarios.prefix(6).map { item(model, $0, dark: dark, increasedContrast: true) }
            SurfacePreviews.writeColumn(contrast, to: directory.appendingPathComponent("quotabar-contrast-\(scheme).png"))
            SurfacePreviews.writeColumn(blocks(model, dark: dark), to: directory.appendingPathComponent("quotabar-blocks-\(scheme).png"))
            SurfacePreviews.writeColumn(pixels(model, dark: dark), to: directory.appendingPathComponent("quotabar-pixels-\(scheme).png"))
        }
        prepare(model, scenarios[0])
        for (language, dark) in [(LanguagePreference.zhHant, false), (.zhHant, true), (.en, false)] {
            settings.language = language
            model.l10n.apply(language)
            PreviewSnapshot.writeHosted(
                SettingsPaneView(pane: .menuBar, model: model).appEnvironment(model).environment(\.orbStillTime, Clock.now),
                dark: dark,
                to: directory.appendingPathComponent("quotabar-settings-menuBar-\(language.rawValue)-\(dark ? "dark" : "light").png")
            )
        }
    }

    // MARK: Scenarios

    static let scenarios: [Scenario] = [
        Scenario(name: "Codex 5h · 7d, used", source: .codex) { model, now in
            codex(model, now, [(.fiveHour, 68.4, 192), (.sevenDay, 42, 3 * 24 * 60 + 300)])
        },
        Scenario(name: "Codex 5h · 7d, remaining", source: .codex, percent: .remaining) { model, now in
            codex(model, now, [(.fiveHour, 68.4, 192), (.sevenDay, 42, 3 * 24 * 60 + 300)])
        },
        Scenario(name: "Claude Code 5h · 7d (+ Fable 7d), ≈ extrapolated", source: .claudeCode) { model, now in
            let cli = QuotaSnapshot(source: .claudeCode, observedMs: now - 2 * minute, windows: [
                QuotaWindow(kind: .fiveHour, usedPercent: 23.4, resetsAtMs: now + 132 * minute),
                QuotaWindow(kind: .sevenDay, usedPercent: 61, resetsAtMs: now + 53 * hour),
                QuotaWindow(kind: .modelWeek, name: "Fable", usedPercent: 12, resetsAtMs: now + 53 * hour),
            ])
            model.quota.preview(desktop: nil, cli: cli, nowMs: now)
            let window = cli.windows[0]
            model.quota.estimateLines = [QuotaEstimateLineKey(source: .claudeCode, window: window):
                QuotaEstimateDisplay(percent: 31.2, estimated: true, resetsMs: window.resetsAtMs, usedMicroUsd: 4_100_000,
                                     confidence: .high, capMicroUsd: 13_000_000)]
        },
        Scenario(name: "Claude Desktop, weekly only", source: .claudeDesktop) { model, now in
            model.quota.preview(desktop: QuotaSnapshot(source: .claudeDesktop, observedMs: now - 7 * minute, windows: [
                QuotaWindow(kind: .sevenDay, usedPercent: 27, resetsAtMs: now + 100 * hour),
            ]), cli: nil, nowMs: now)
        },
        Scenario(name: "Codex 97 % · 88 %, red from 95 %", source: .codex) { model, now in
            codex(model, now, [(.fiveHour, 97, 41), (.sevenDay, 88, 2 * 24 * 60)])
        },
        Scenario(name: "Codex, read 3 h ago: pale", source: .codex) { model, now in
            codex(model, now, [(.fiveHour, 97, 41), (.sevenDay, 42, 2 * 24 * 60)], observed: now - 3 * hour)
        },
        Scenario(name: "Kimi, signed out: pale", source: .kimi) { model, now in
            let snapshot = QuotaSnapshot(source: .kimi, observedMs: now - 20 * minute, windows: [
                QuotaWindow(kind: .fiveHour, usedPercent: 25, resetsAtMs: now + 80 * minute),
                QuotaWindow(kind: .sevenDay, usedPercent: 50, resetsAtMs: now + 70 * hour),
            ])
            model.quota.preview(desktop: nil, cli: nil, vendor: QuotaVendorReadings(routes: [
                .kimi: QuotaRouteReading(snapshot: snapshot, status: .noCredential, signedIn: false),
            ]), nowMs: now)
        },
        Scenario(name: "Gemini CLI, no data: –", source: .gemini) { model, now in
            model.quota.preview(desktop: nil, cli: nil, vendor: QuotaVendorReadings(routes: [
                .gemini: QuotaRouteReading(snapshot: nil, status: .failed, signedIn: true),
            ]), nowMs: now)
        },
        Scenario(name: "Codex, quota switch off: –", source: .codex) { model, now in
            codex(model, now, [(.fiveHour, 68.4, 192), (.sevenDay, 42, 3 * 24 * 60 + 300)])
            model.settings.quotaCodex = false
        },
        Scenario(name: "Antigravity, long labels", source: .antigravity) { model, now in
            model.quota.preview(desktop: nil, cli: nil, vendor: QuotaVendorReadings(routes: [
                .antigravity: QuotaRouteReading(snapshot: QuotaSnapshot(source: .antigravity, observedMs: now - minute, windows: [
                    QuotaWindow(kind: .sevenDay, name: "Gemini", usedPercent: 4.5, resetsAtMs: now + 90 * hour),
                    QuotaWindow(kind: .sevenDay, name: "Claude/GPT", usedPercent: 0, resetsAtMs: now + 96 * hour),
                ]), status: .ok(now - minute), signedIn: true),
            ]), nowMs: now)
        },
    ]

    /// Codex's snapshot comes with the engine's (QUOTAS 3): `(kind, used, minutes to the reset)`.
    private static func codex(_ model: AppModel, _ now: Int64, _ windows: [(QuotaWindowKind, Double, Int64)],
                              observed: Int64? = nil) {
        var snapshot = model.snapshot
        snapshot.codexQuota = QuotaSnapshot(source: .codex, observedMs: observed ?? now - 2 * minute, windows: windows.map {
            QuotaWindow(kind: $0.0, usedPercent: $0.1, resetsAtMs: now + $0.2 * minute)
        })
        model.store.publishLive(snapshot)
        model.quota.preview(desktop: nil, cli: nil, nowMs: now)
    }

    private static func prepare(_ model: AppModel, _ scenario: Scenario) {
        let settings = model.settings
        let now = QuotaController.nowMs()
        settings.quotaCodex = true
        settings.quotaClaudeCli = true
        settings.barQuota = scenario.source
        settings.barQuotaPercent = scenario.percent
        model.quota.estimateLines = [:]
        var snapshot = PreviewData.snapshot(speed: 532, redline: settings.redline)
        snapshot.intelligence = IntelligenceBarPreviewData.scenarios[2].sums
        snapshot.agents = IntelligenceBarPreviewData.scenarios[2].agents
        model.store.publishLive(snapshot)
        PreviewRenderer.snapGauge(model, to: 532, now: Clock.now - 3)
        scenario.setup(model, now)
    }

    private static func state(_ model: AppModel, _ scenario: Scenario, increasedContrast: Bool = false) -> StatusItemState {
        prepare(model, scenario)
        var state = StatusItemState(model: model, time: Clock.now)
        state.increasedContrast = increasedContrast
        return state
    }

    // MARK: Images

    /// The item on a plain bar, three times its size, with the scenario's name beside it.
    private static func item(_ model: AppModel, _ scenario: Scenario, dark: Bool, increasedContrast: Bool = false,
                             caption: String? = nil) -> NSImage {
        image(state(model, scenario, increasedContrast: increasedContrast), dark: dark, caption: caption ?? scenario.name)
    }

    private static func image(_ state: StatusItemState, dark: Bool, caption: String) -> NSImage {
        let size = CGSize(width: 560, height: state.layout.height)
        let image = NSImage(size: size, flipped: true) { rect in
            (dark ? NSColor(white: 0.16, alpha: 1) : NSColor(white: 0.93, alpha: 1)).setFill()
            rect.fill()
            NSColor.systemRed.withAlphaComponent(0.35).setFill()
            CGRect(x: 10 + state.layout.length, y: 0, width: 1, height: rect.height).fill()
            NSAttributedString(string: caption, attributes: [
                .font: NSFont.systemFont(ofSize: 7), .foregroundColor: NSColor(white: dark ? 0.6 : 0.4, alpha: 1),
            ]).draw(at: CGPoint(x: 10 + state.layout.length + 20, y: rect.height / 2 - 5))
            NSGraphicsContext.current?.cgContext.translateBy(x: 10, y: 0)
            StatusItemArtist.draw(state, dark: dark, in: CGRect(origin: .zero, size: CGSize(width: state.layout.length,
                                                                                          height: state.layout.height)))
            return true
        }
        let scale: CGFloat = 3
        return NSImage(size: CGSize(width: size.width * scale, height: size.height * scale), flipped: false) { rect in
            image.draw(in: rect)
            return true
        }
    }

    /// The block after the others: alone, after the network block, after the intelligence block, after both, after the
    /// money block and all three, and in value-only mode.
    private static func blocks(_ model: AppModel, dark: Bool) -> [NSImage] {
        let settings = model.settings
        defer {
            settings.barTraffic = .off
            settings.barIntelligence = false
            settings.barSecondary = false
            settings.barMode = .glyphValue
        }
        let combinations: [(String, BarTraffic, Bool, Bool, BarDisplayMode)] = [
            ("quota alone", .off, false, false, .glyphValue),
            ("network + quota", .machine, false, false, .glyphValue),
            ("性能 + quota", .off, true, false, .glyphValue),
            ("network + 性能 + quota", .machine, true, false, .glyphValue),
            ("money + network + 性能 + quota", .machine, true, true, .glyphValue),
            ("value only: network + 性能 + quota", .machine, true, false, .value),
        ]
        var rows: [NSImage] = []
        for scenario in [scenarios[0], scenarios[3]] {
            for (name, traffic, intelligence, money, mode) in combinations {
                settings.barTraffic = traffic
                settings.barIntelligence = intelligence
                settings.barSecondary = money
                settings.barMode = mode
                rows.append(item(model, scenario, dark: dark, caption: "\(name) · \(scenario.name)"))
            }
        }
        return rows
    }

    /// The block exactly as the menu bar gets it: the item's 2× bitmap, and the same item at 1×, cropped to the block
    /// and magnified without smoothing (8× and 16×).
    private static func pixels(_ model: AppModel, dark: Bool) -> [NSImage] {
        var rows: [NSImage] = []
        for scenario in [scenarios[0], scenarios[2], scenarios[3], scenarios[4], scenarios[7]] {
            let state = state(model, scenario)
            let size = NSSize(width: state.layout.length, height: state.layout.height)
            let block = CGRect(x: state.layout.quotaX, y: 0, width: state.layout.quotaWidth, height: size.height)
            let factors: [(scale: CGFloat, zoom: CGFloat)] = [(2, 8), (1, 16)]
            let crops: [(image: CGImage, zoom: CGFloat)] = factors.compactMap { factor in
                guard let bitmap = StatusItemArtist.render(state, dark: dark, size: size, scale: factor.scale),
                      let crop = bitmap.cropping(to: CGRect(x: block.minX * factor.scale, y: 0, width: block.width * factor.scale,
                                                            height: size.height * factor.scale)) else { return nil }
                return (crop, factor.zoom)
            }
            var width: CGFloat = 0
            for crop in crops { width += CGFloat(crop.image.width) * crop.zoom + 16 }
            let height = size.height * 16
            rows.append(NSImage(size: CGSize(width: width, height: height), flipped: false) { rect in
                (dark ? NSColor(white: 0.16, alpha: 1) : NSColor(white: 0.93, alpha: 1)).setFill()
                rect.fill()
                guard let context = NSGraphicsContext.current?.cgContext else { return false }
                context.interpolationQuality = .none
                var x: CGFloat = 0
                for (crop, zoom) in crops {
                    let target = CGRect(x: x, y: 0, width: CGFloat(crop.width) * zoom, height: CGFloat(crop.height) * zoom)
                    context.draw(crop, in: target)
                    x = target.maxX + 16
                }
                return true
            })
        }
        return rows
    }
}
