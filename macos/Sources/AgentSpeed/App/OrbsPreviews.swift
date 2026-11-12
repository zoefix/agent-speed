import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import SwiftUI

/// `--render-previews` group `orbs`: several floating orbs composed like the desktop (bezels, source chips, a split in
/// progress with its undo chip, an unavailable orb), a size sheet per style, and every style's state sheet with
/// Increase Contrast and with Reduce Motion. Light and dark.
@MainActor
enum OrbsPreviews {
    static func render(model: AppModel, into directory: URL) {
        for dark in [false, true] {
            let scheme = dark ? "dark" : "light"
            arrangement(model: model, dark: dark)?.write(to: directory.appendingPathComponent("orbs-arrangement-\(scheme).png"))
            split(model: model, dark: dark)?.write(to: directory.appendingPathComponent("orbs-split-undo-\(scheme).png"))
            for style in OrbStyleKind.allCases {
                sizes(model: model, style: style, dark: dark)?
                    .write(to: directory.appendingPathComponent("orb-sizes-\(style.rawValue)-\(scheme).png"))
                stateSheet(model: model, style: style, dark: dark, variant: .increaseContrast, into: directory)
                stateSheet(model: model, style: style, dark: dark, variant: .reduceMotion, into: directory)
            }
        }
    }

    /// `led` stills of every tier at 112 / 148 / 196 pt at R = 400 tok/s, the redline the app used before the
    /// default moved, for before/after comparisons with the single-orb app (`upgrade/orb-<tier>-<size>-<scheme>.png`).
    static func renderUpgrade(model: AppModel, into directory: URL) {
        let folder = directory.appendingPathComponent("upgrade", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let redline = model.settings.redlineOutput
        model.settings.redlineOutput = 400
        defer { model.settings.redlineOutput = redline }
        for dark in [false, true] {
            for size in SurfacePreviews.sizes {
                for sample in SurfacePreviews.tiers {
                    guard let image = SurfacePreviews.orbImage(model: model, size: size, dark: dark, speed: 400 * sample.fraction,
                                                               tier: sample.tier) else { continue }
                    PreviewSnapshot.write(image, to: folder.appendingPathComponent("orb-\(sample.name)-\(Int(size))-\(dark ? "dark" : "light").png"))
                }
            }
        }
    }

    /// Isolated idle settings, without reading or changing the user's application defaults.
    static func renderIdleBehavior(model: AppModel, into directory: URL) {
        model.settings.language = .zhHans
        model.l10n.apply(.zhHans)
        guard let id = model.orbs.orbs.first?.id else { return }
        for behavior in OrbIdleBehavior.allCases {
            model.orbs.setIdleBehavior(behavior, of: id)
            guard let orb = model.orbs.orb(id) else { continue }
            for dark in [false, true] {
                PreviewSnapshot.writeHosted(
                    Form { OrbSwitchesSection(model: model, orb: orb) }
                        .formStyle(.grouped).frame(width: 650, height: 380).appEnvironment(model),
                    dark: dark,
                    to: directory.appendingPathComponent("idle-\(behavior.rawValue)-\(dark ? "dark" : "light").png")
                )
            }
        }
    }

    // MARK: Desktop compositions

    private struct Orb {
        var style: OrbStyleKind
        var size: CGFloat
        var sources: [SourceKind]?
        var unavailable: [SourceKind] = []
        var unavailableTitle: String?
        var fraction: Double
        /// Center in the scene (points, y down).
        var center: CGPoint
        var scale: CGFloat = 1
        var alpha: CGFloat = 1
        var bezelDrawOn: Double = 1
        var chip: [OrbChipItem]?
    }

    /// Four orbs touching at 12 pt: all sources (with its chip, since there are several), a pair, a single source and
    /// a three-source orb with one source not found; plus an orb whose only source is not detected.
    private static func arrangement(model: AppModel, dark: Bool) -> PreviewCanvas? {
        let l10n = model.l10n
        var orbs: [Orb] = []
        let y: CGFloat = 150
        let a = CGPoint(x: 120, y: y)
        let b = CGPoint(x: a.x + 74 + 12 + 74, y: y)
        let c = CGPoint(x: b.x + 74 + 12 + 56, y: y + 18)
        let d = CGPoint(x: c.x + 56 + 12 + 74, y: y)
        let e = CGPoint(x: d.x + 74 + 12 + 48, y: y + 26)
        orbs.append(Orb(style: .led, size: 148, sources: nil, fraction: 0.62, center: a,
                        chip: [OrbChipItem(id: "all", color: nil, text: l10n("orbs.sources.all"), secondary: false)]))
        orbs.append(Orb(style: .needle, size: 148, sources: [.claudeCode, .claudeDesktop], fraction: 0.45, center: b,
                        chip: chip(model: model, [.claudeCode, .claudeDesktop], unavailable: [])))
        orbs.append(Orb(style: .liquid, size: 112, sources: [.codex], fraction: 0.3, center: c,
                        chip: chip(model: model, [.codex], unavailable: [])))
        orbs.append(Orb(style: .reactor, size: 148, sources: [.deepseek, .opencode, .gemini], unavailable: [.opencode],
                        fraction: 0.9, center: d, chip: chip(model: model, [.deepseek, .opencode, .gemini], unavailable: [.opencode])))
        orbs.append(Orb(style: .minimal, size: 96, sources: [.antigravity], unavailable: [.antigravity],
                        unavailableTitle: "source.notFound", fraction: 0, center: e, alpha: 0.55,
                        chip: chip(model: model, [.antigravity], unavailable: [.antigravity])))
        return compose(orbs, model: model, size: CGSize(width: e.x + 120, height: 330), dark: dark, undo: nil)
    }

    /// A split in flight: the original shrinks and fades while its children spring out with their bezels drawing on,
    /// and the undo chip waits under the first one.
    private static func split(model: AppModel, dark: Bool) -> PreviewCanvas? {
        let l10n = model.l10n
        let origin = CGPoint(x: 120, y: 150)
        let sources: [SourceKind] = [.codex, .claudeCode, .claudeDesktop]
        var orbs: [Orb] = []
        orbs.append(Orb(style: .needle, size: 148, sources: [.codex], fraction: 0.55, center: origin, bezelDrawOn: 0.8,
                        chip: chip(model: model, [.codex], unavailable: [])))
        let second = CGPoint(x: origin.x + 160 * 0.8, y: origin.y)
        let third = CGPoint(x: origin.x + 320 * 0.62, y: origin.y)
        orbs.append(Orb(style: .needle, size: 148, sources: [.claudeCode], fraction: 0.55, center: second, scale: 0.88, alpha: 0.9,
                        bezelDrawOn: 0.55))
        orbs.append(Orb(style: .needle, size: 148, sources: [.claudeDesktop], fraction: 0.55, center: third, scale: 0.74, alpha: 0.7,
                        bezelDrawOn: 0.3))
        let undo = l10n("orbs.didSplit", sources.count) + " · " + l10n("orbs.undo")
        return compose(orbs, model: model, size: CGSize(width: 560, height: 330), dark: dark, undo: (undo, origin))
    }

    private static func chip(model: AppModel, _ sources: [SourceKind], unavailable: [SourceKind]) -> [OrbChipItem] {
        let l10n = model.l10n
        let shown = sources.count <= 2 ? sources : [sources[0]]
        var items = shown.map { source -> OrbChipItem in
            let name = l10n(source.titleKey)
            guard unavailable.contains(source) else { return OrbChipItem(id: source.rawValue, color: source.tint, text: name, secondary: false) }
            return OrbChipItem(id: source.rawValue, color: source.tint, text: name + " · " + l10n("source.notFound"), secondary: true)
        }
        if sources.count > 2 { items.append(OrbChipItem(id: "more", color: nil, text: "+\(sources.count - 1)", secondary: false)) }
        return items
    }

    private static func compose(_ orbs: [Orb], model: AppModel, size: CGSize, dark: Bool, undo: (String, CGPoint)?) -> PreviewCanvas? {
        guard let canvas = PreviewCanvas(size: size, background: .clear) else { return nil }
        drawDesktop(canvas, size: size, dark: dark)
        let redline = model.settings.redline
        for orb in orbs {
            let stage = OrbPreviewStage(model: model, style: orb.style, diameter: orb.size, dark: dark)
            if let sources = orb.sources {
                stage.feed.bezel = OrbBezel(arcs: sources.map { OrbBezel.Arc(color: $0.tint, available: !orb.unavailable.contains($0)) },
                                            drawOn: orb.bezelDrawOn)
                stage.feed.set(generating: Array(sources.filter { !orb.unavailable.contains($0) }.prefix(2)), todayTokens: 380_000,
                               agentCount: 2)
            }
            stage.feed.unavailableTitleKey = orb.unavailableTitle
            stage.reset(value: redline * orb.fraction)
            stage.run(for: 2.4, at: redline * orb.fraction)
            if let image = stage.image() {
                let side = stage.canvasSide * orb.scale
                canvas.context.saveGState()
                canvas.context.setAlpha(orb.alpha)
                canvas.draw(image, at: CGPoint(x: orb.center.x - side / 2, y: orb.center.y - side / 2), size: CGSize(width: side, height: side))
                canvas.context.restoreGState()
            }
            stage.stop()
            if let items = orb.chip {
                drawChip(OrbChipView(items: items), canvas: canvas, below: orb, dark: dark)
            }
        }
        if let (text, anchor) = undo {
            let chip = OrbChipView(items: [OrbChipItem(id: "undo", color: nil, text: text, secondary: false)])
            drawChip(chip, canvas: canvas, below: Orb(style: .led, size: 148, sources: nil, fraction: 0, center: anchor), dark: dark,
                     extraOffset: 22)
        }
        return canvas
    }

    /// The chip centered 6 pt below the disc (its view carries 8 pt of shadow padding).
    private static func drawChip(_ chip: OrbChipView, canvas: PreviewCanvas, below orb: Orb, dark: Bool, extraOffset: CGFloat = 0) {
        let renderer = ImageRenderer(content: chip.environment(\.colorScheme, dark ? .dark : .light))
        renderer.scale = 2
        renderer.isOpaque = false
        guard let image = renderer.cgImage else { return }
        let size = CGSize(width: CGFloat(image.width) / 2, height: CGFloat(image.height) / 2)
        let top = orb.center.y + orb.size * orb.scale / 2 + 6 - 8 + extraOffset
        canvas.draw(image, at: CGPoint(x: orb.center.x - size.width / 2, y: top), size: size)
    }

    private static func drawDesktop(_ canvas: PreviewCanvas, size: CGSize, dark: Bool) {
        let colors = dark
            ? [NSColor(srgbRed: 0.10, green: 0.13, blue: 0.20, alpha: 1), NSColor(srgbRed: 0.20, green: 0.17, blue: 0.27, alpha: 1)]
            : [NSColor(srgbRed: 0.80, green: 0.86, blue: 0.93, alpha: 1), NSColor(srgbRed: 0.93, green: 0.86, blue: 0.84, alpha: 1)]
        guard let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB), colors: colors.map(\.cgColor) as CFArray,
                                        locations: [0, 1]) else { return }
        canvas.context.drawLinearGradient(gradient, start: .zero, end: CGPoint(x: size.width, y: size.height), options: [])
    }

    // MARK: Sheets

    /// One style at the redline at 64 (minimal only), 96, 148 and 240 pt, with a bezel at 148.
    private static func sizes(model: AppModel, style: OrbStyleKind, dark: Bool) -> PreviewCanvas? {
        let sizes: [CGFloat] = (style == .minimal ? [64] : []) + [96, 148, 240]
        let redline = model.settings.redline
        let images = sizes.compactMap { size in
            SurfacePreviews.stageImage(model: model, style: style, size: size, dark: dark, value: redline * 0.9) { stage in
                if size == 148 {
                    stage.feed.bezel = OrbBezel(arcs: [OrbBezel.Arc(color: SourceKind.claudeCode.tint, available: true),
                                                       OrbBezel.Arc(color: SourceKind.claudeDesktop.tint, available: true)])
                }
            }
        }
        let width = images.reduce(CGFloat(24)) { $0 + CGFloat($1.width) / 2 + 12 }
        let height = (images.map { CGFloat($0.height) / 2 }.max() ?? 0) + 24
        guard let canvas = PreviewCanvas(size: CGSize(width: width, height: height), background: .clear) else { return nil }
        drawDesktop(canvas, size: canvas.size, dark: dark)
        var x: CGFloat = 12
        for image in images {
            let size = CGSize(width: CGFloat(image.width) / 2, height: CGFloat(image.height) / 2)
            canvas.draw(image, at: CGPoint(x: x, y: (height - size.height) / 2), size: size)
            x += size.width + 12
        }
        return canvas
    }

    enum Variant: String {
        case increaseContrast = "contrast"
        case reduceMotion = "reduce-motion"
    }

    /// The style × state sheet (idle … beyond, new peak, hover, pulse; 148 and 240 pt) under an accessibility setting.
    private static func stateSheet(model: AppModel, style: OrbStyleKind, dark: Bool, variant: Variant, into directory: URL) {
        let redline = model.settings.redline
        let columns: [(Double, SurfacePreviews.OrbExtra)] = SurfacePreviews.tiers.map { ($0.fraction, .none) }
            + [(0.95, .peak), (0.6, .hover), (0.6, .pulse)]
        var rows: [[NSImage]] = []
        for size: CGFloat in [148, 240] {
            rows.append(columns.compactMap { fraction, extra in
                SurfacePreviews.stageImage(model: model, style: style, size: size, dark: dark, value: redline * fraction, extra: extra) { stage in
                    switch variant {
                    case .increaseContrast: stage.increaseContrast = true
                    case .reduceMotion: stage.animator.overrides.reduceMotion = true
                    }
                }.map { NSImage(cgImage: $0, size: CGSize(width: CGFloat($0.width) / 2, height: CGFloat($0.height) / 2)) }
            })
        }
        SurfacePreviews.writeSheet(rows, dark: dark, to: directory.appendingPathComponent(
            "orb-style-\(style.rawValue)-\(dark ? "dark" : "light")-\(variant.rawValue).png"
        ))
    }
}
