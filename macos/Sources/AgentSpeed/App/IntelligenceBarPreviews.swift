import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import SwiftUI

/// `--render-previews` group `intelbar`: the menu bar item with the intelligence block (DESIGN 10, Menu bar and
/// taskbar; DESIGN 12): every form of 性能 and the provisional mark with 思考 shown, within its hold and hidden after it, and the
/// widest numbers, in Chinese, English and Japanese on dark and light bars; with the money block and in value-only
/// mode; with Increase Contrast; the item's own bitmap at 2× and 1× magnified to the pixel, so the provisional mark's dots
/// can be checked; the intelligence block beside every network choice (`settings.bar.traffic` with the switch on, both
/// blocks together); and 選單列 and 網路流量 with the switch on, measuring on and off. A red hairline marks the item's end, so a width change between rows shows at once.
@MainActor
enum IntelligenceBarPreviews {
    typealias Scenario = IntelligenceBarPreviewData.Scenario

    private static var scenarios: [Scenario] { IntelligenceBarPreviewData.scenarios }

    /// The forms side by side: none, live, unverified, resting pale and pending pale on one line.
    private static var forms: [Scenario] { [0, 2, 3, 9, 10, 11].map { scenarios[$0] } }

    static func render(model: AppModel, into directory: URL) {
        let settings = model.settings
        let saved = (settings.language, settings.barTraffic, settings.barMode, settings.barSecondary, settings.trafficEnabled,
                     settings.barIntelligence)
        defer {
            settings.language = saved.0
            model.l10n.apply(saved.0)
            settings.barTraffic = saved.1
            settings.barMode = saved.2
            settings.barSecondary = saved.3
            settings.trafficEnabled = saved.4
            settings.barIntelligence = saved.5
        }
        settings.trafficEnabled = true
        TrafficPreviewData.publish(into: model.traffic)
        settings.barTraffic = .off
        settings.barIntelligence = true
        settings.barMode = .glyphValue
        settings.barSecondary = false
        for language in [LanguagePreference.zhHant, .en, .ja] {
            settings.language = language
            model.l10n.apply(language)
            for dark in [false, true] {
                let scheme = dark ? "dark" : "light"
                let name = "intelbar-\(language.rawValue)-\(scheme).png"
                SurfacePreviews.writeColumn(scenarios.map { item(model, $0, dark: dark) },
                                            to: directory.appendingPathComponent(name))
                guard language != .ja else { continue }
                var modes: [NSImage] = []
                for (secondary, mode) in [(true, BarDisplayMode.glyphValue), (false, .value)] {
                    settings.barSecondary = secondary
                    settings.barMode = mode
                    modes += forms.map { item(model, $0, dark: dark) }
                }
                settings.barSecondary = false
                settings.barMode = .glyphValue
                SurfacePreviews.writeColumn(modes, to: directory.appendingPathComponent("intelbar-modes-\(language.rawValue)-\(scheme).png"))
                if language == .zhHant {
                    let contrast = forms.map { item(model, $0, dark: dark, increasedContrast: true) }
                    SurfacePreviews.writeColumn(contrast, to: directory.appendingPathComponent("intelbar-contrast-\(scheme).png"))
                    SurfacePreviews.writeColumn(pixels(model, dark: dark), to: directory.appendingPathComponent("intelbar-pixels-\(scheme).png"))
                    // The intelligence block after every network choice, then the network block alone.
                    var choices = BarTraffic.allCases.map { choice -> NSImage in
                        settings.barTraffic = choice
                        return item(model, scenarios[2], dark: dark)
                    }
                    settings.barIntelligence = false
                    settings.barTraffic = .machine
                    choices.append(item(model, scenarios[2], dark: dark))
                    settings.barSecondary = true
                    settings.barIntelligence = true
                    choices.append(item(model, scenarios[2], dark: dark))
                    settings.barSecondary = false
                    settings.barTraffic = .off
                    SurfacePreviews.writeColumn(choices, to: directory.appendingPathComponent("intelbar-choices-\(scheme).png"))
                }
            }
        }
        settings.language = .zhHant
        model.l10n.apply(.zhHant)
        settings.barTraffic = .machine
        settings.barIntelligence = true
        for enabled in [true, false] {
            settings.trafficEnabled = enabled
            let state = enabled ? "on" : "off"
            for dark in [false, true] {
                for pane in [SettingsPane.menuBar, .traffic] {
                    PreviewSnapshot.writeHosted(
                        SettingsPaneView(pane: pane, model: model).appEnvironment(model).environment(\.orbStillTime, Clock.now),
                        dark: dark,
                        to: directory.appendingPathComponent("intelbar-settings-\(pane.rawValue)-measuring-\(state)-\(dark ? "dark" : "light").png")
                    )
                }
            }
        }
        settings.trafficEnabled = true
        settings.language = .en
        model.l10n.apply(.en)
        PreviewSnapshot.writeHosted(
            SettingsPaneView(pane: .menuBar, model: model).appEnvironment(model).environment(\.orbStillTime, Clock.now),
            dark: false, to: directory.appendingPathComponent("intelbar-settings-menuBar-en-light.png")
        )
    }

    /// The item's state showing `scenario` for its `held` seconds, the value at 532 tok/s while an agent is live.
    private static func state(_ model: AppModel, _ scenario: Scenario, increasedContrast: Bool = false) -> StatusItemState {
        let value = scenario.output > 0 ? 532.0 : 0
        var snapshot = PreviewData.snapshot(speed: value, redline: model.settings.redline)
        let sums = scenario.sums
        snapshot.intelligence = sums
        snapshot.agents = scenario.agents
        model.store.publishLive(snapshot)
        let now = Clock.now
        model.barThinking.update(.zero, at: now - scenario.held - 1)
        model.barThinking.update(sums, at: now - scenario.held)
        model.barThinking.update(sums, at: now)
        PreviewRenderer.snapGauge(model, to: value, now: Clock.now - 3)
        var state = StatusItemState(model: model, time: Clock.now)
        state.increasedContrast = increasedContrast
        return state
    }

    /// The item on a plain bar, three times its size, with the scenario's name and its spoken words beside it.
    private static func item(_ model: AppModel, _ scenario: Scenario, dark: Bool, increasedContrast: Bool = false) -> NSImage {
        let state = state(model, scenario, increasedContrast: increasedContrast)
        let size = CGSize(width: 680, height: state.layout.height)
        let caption = [scenario.name, state.traffic.map { _ in "+ network" }, state.intelligence?.spoken].compactMap { $0 }
            .joined(separator: "   ")
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

    /// The block exactly as the menu bar gets it: the item's 2× bitmap, and the same item at 1×, cropped to the block
    /// and magnified without smoothing (8× and 16×), one row per provisional moment.
    private static func pixels(_ model: AppModel, dark: Bool) -> [NSImage] {
        let marked = [scenarios[3], scenarios[4], scenarios[5], scenarios[11]]
        var rows: [NSImage] = []
        for increasedContrast in [false, true] {
            for scenario in marked {
                let state = state(model, scenario, increasedContrast: increasedContrast)
                let size = NSSize(width: state.layout.length, height: state.layout.height)
                let block = CGRect(x: state.layout.intelligenceX, y: 0, width: state.layout.intelligenceWidth, height: size.height)
                let factors: [(scale: CGFloat, zoom: CGFloat)] = [(2, 8), (1, 16)]
                let crops: [(image: CGImage, zoom: CGFloat)] = factors.compactMap { factor in
                    guard let bitmap = StatusItemArtist.render(state, dark: dark, size: size, scale: factor.scale),
                          let crop = bitmap.cropping(to: CGRect(x: block.minX * factor.scale, y: 0,
                                                                width: block.width * factor.scale,
                                                                height: size.height * factor.scale)) else { return nil }
                    return (crop, factor.zoom)
                }
                var width: CGFloat = 0
                for crop in crops { width += CGFloat(crop.image.width) * crop.zoom + 16 }
                let height = size.height * 16
                let image = NSImage(size: CGSize(width: width, height: height), flipped: false) { rect in
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
                }
                rows.append(image)
            }
        }
        return rows
    }
}
