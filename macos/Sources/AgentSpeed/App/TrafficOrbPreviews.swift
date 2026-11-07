import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// `--render-previews` group `traffic` (orb part): the traffic readout of every style at 112, 148 and 196 pt in three
/// situations (a tool download, a request upload, a streamed response), and a state sheet per appearance — quiet,
/// fading out, yielding to the new-peak pill, bits, Increase Contrast, hover and a size below the style's minimum
/// (DESIGN 10, Floating orb readout).
@MainActor
enum TrafficOrbPreviews {
    struct Scenario {
        var name: String
        var rx: Double
        var tx: Double
    }

    static let scenarios = [
        Scenario(name: "download", rx: 12_400_000, tx: 186_000),
        Scenario(name: "upload", rx: 3_200, tx: 412_000),
        Scenario(name: "stream", rx: 8_600, tx: 1_100),
    ]

    /// Values per style that land on a mid tier at the default redline.
    static func value(_ style: OrbStyleKind, redline: Double) -> Double {
        switch style {
        case .led: 0.16 * redline
        case .needle: 0.42 * redline
        case .liquid: 0.30 * redline
        case .reactor: 0.52 * redline
        case .minimal: 0.22 * redline
        case .character: 0.30 * redline
        }
    }

    static func render(model: AppModel, into directory: URL) {
        let redline = model.settings.redline
        model.store.publishLive(PreviewData.snapshot(speed: redline * 0.3, redline: redline))
        for dark in [false, true] {
            let scheme = dark ? "dark" : "light"
            var rows: [[NSImage]] = []
            for style in OrbStyleKind.allCases {
                var row: [NSImage] = []
                for scenario in scenarios {
                    for size in SurfacePreviews.sizes {
                        let image = still(model: model, style: style, size: size, dark: dark, value: value(style, redline: redline)) { stage in
                            show(stage, rx: scenario.rx, tx: scenario.tx)
                        }
                        if let image { row.append(SurfacePreviews.pointImage(image)) }
                    }
                }
                rows.append(row)
            }
            SurfacePreviews.writeSheet(rows, dark: dark, to: directory.appendingPathComponent("orb-traffic-\(scheme).png"))
            writeStates(model: model, dark: dark, into: directory)
        }
    }

    /// Columns: quiet (needle keeps its unit on the dial), streaming, fading out, new-peak pill, bits, Increase
    /// Contrast, hovered (info line; the readout stays), 112 pt.
    private static func writeStates(model: AppModel, dark: Bool, into directory: URL) {
        let redline = model.settings.redline
        var rows: [[NSImage]] = []
        for style in OrbStyleKind.allCases {
            let speed = value(style, redline: redline)
            var row: [CGImage?] = []
            row.append(still(model: model, style: style, size: 148, dark: dark, value: 0) { stage in
                stage.feed.lines = OrbLines(format: .traffic(bits: false), reading: nil)
            })
            row.append(still(model: model, style: style, size: 148, dark: dark, value: speed) { show($0, rx: 8_600, tx: 1_100) })
            row.append(still(model: model, style: style, size: 148, dark: dark, value: speed) { stage in
                show(stage, rx: 640, tx: 120, quietIn: -0.3)
            })
            row.append(still(model: model, style: style, size: 148, dark: dark, value: speed, peak: true) { show($0, rx: 12_400_000, tx: 186_000) })
            row.append(still(model: model, style: style, size: 148, dark: dark, value: speed) { show($0, rx: 1_250_000, tx: 52_000, bits: true) })
            row.append(still(model: model, style: style, size: 148, dark: dark, value: speed) { stage in
                stage.increaseContrast = true
                show(stage, rx: 12_400_000, tx: 186_000)
            })
            row.append(still(model: model, style: style, size: 148, dark: dark, value: speed, hover: true) { show($0, rx: 12_400_000, tx: 186_000) })
            row.append(still(model: model, style: style, size: 112, dark: dark, value: speed) { show($0, rx: 12_400_000, tx: 186_000) })
            rows.append(row.compactMap { $0.map(SurfacePreviews.pointImage) })
        }
        SurfacePreviews.writeSheet(rows, dark: dark, to: directory.appendingPathComponent("orb-traffic-states-\(dark ? "dark" : "light").png"))
    }

    /// Shows the readout in steady state (faded in long ago, quiet `quietIn` seconds from the stage's time).
    static func show(_ stage: OrbPreviewStage, rx: Double, tx: Double, bits: Bool = false, quietIn: TimeInterval = 60) {
        show(stage, .traffic(rx: TrafficFormat.rateParts(rx, bits: bits), tx: TrafficFormat.rateParts(tx, bits: bits)),
             format: .traffic(bits: bits), quietIn: quietIn)
    }

    /// Shows `content` in steady state: faded in long ago, quiet `quietIn` seconds after the still is taken.
    static func show(_ stage: OrbPreviewStage, _ content: OrbLineContent, format: OrbLineFormat, quietIn: TimeInterval = 60,
                     thinking: OrbThinkingLine = OrbThinkingLine()) {
        stage.feed.lines = OrbLines(format: format,
                                    reading: OrbLineReading(content: content, shownAt: -.infinity, quietAt: stage.time + settle + quietIn,
                                                            thinking: thinking))
    }

    /// How long a still settles before it is taken.
    static let settle: TimeInterval = 2.4

    static func still(model: AppModel, style: OrbStyleKind, size: CGFloat, dark: Bool, value: Double, peak: Bool = false,
                      hover: Bool = false, configure: (OrbPreviewStage) -> Void) -> CGImage? {
        let stage = OrbPreviewStage(model: model, style: style, diameter: size, dark: dark)
        defer { stage.stop() }
        configure(stage)
        stage.reset(value: value)
        stage.hovered = hover
        stage.run(for: settle, at: value)
        if peak {
            stage.animator.celebrate(at: stage.time)
            stage.run(for: 0.62, at: value)
        }
        if hover { stage.run(for: 0.6, at: value) }
        return stage.image()
    }
}
