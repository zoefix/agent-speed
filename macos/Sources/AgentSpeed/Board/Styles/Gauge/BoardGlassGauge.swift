import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// Glass's sub-gauge, 光環副錶 (DESIGN 2c Live metrics): a pearl arc on the log scale around 性能 over the 思考 / 費用
/// table, right-aligned at the side inset and centred in the hero band, with the reference 1000 ticked at the top. The
/// arc's end and its tip move by layer animation (a mask and a turn), so a new value redraws no arc. Resting draws the
/// value arc and tip at 35 % without glow; a provisional score draws the arc as round dots and the tip as a hollow ring
/// (DESIGN 12 *Provisional mark*).
@MainActor
final class BoardGlassGauge {
    let size: BoardSize
    let palette: BoardPalette
    let l10n: Localizer
    let metrics: BoardGaugeMetrics
    private(set) var table: BoardGaugeTable?
    private var tableKey: [String] = []
    private var numberCell = BoardCellWidth()
    private var score = BoardHeroScore()
    /// The dial, where the hero's score card opens (board points); nil without 性能.
    private(set) var scoreArea: CGRect?

    /// When a running count lands (the board lays itself out on every frame until then).
    func climbsUntil(after now: TimeInterval) -> TimeInterval? { score.climbsUntil(after: now) }

    /// The arc runs from 150° (lower left) clockwise over the top to 30° (lower right), y down.
    static let arcStart = 150 * CGFloat.pi / 180
    static let arcSweep = 240 * CGFloat.pi / 180

    init(size: BoardSize, palette: BoardPalette, l10n: Localizer) {
        self.size = size
        self.palette = palette
        self.l10n = l10n
        metrics = BoardGaugeMetrics(.glass, size)
    }

    private var nominal: CGFloat { CGFloat(BoardGeometry(.glass, size).gaugeWidth) }

    /// Measures the table for the tick's switches and currency (only when they change) and fits its number cell.
    private func prepare(_ tick: BoardMetricsTick, readings: BoardGaugeReadings, now: TimeInterval) -> CGFloat {
        let cells = BoardGaugeReadings.cells(tick, l10n: l10n)
        let key = cells.labels + ["|"] + cells.units
        if table == nil || key != tableKey {
            tableKey = key
            table = cells.labels.isEmpty ? nil
                : BoardGaugeTable(style: .glass, size: size, palette: palette, labels: cells.labels, units: cells.units)
            numberCell.reset(base: Double(table?.numberBase ?? 0))
        }
        guard let table else { return 0 }
        let need = [readings.thinking, readings.cost].compactMap { $0 }.map { Double(table.need($0)) }.max() ?? 0
        return CGFloat(numberCell.fit(need, at: now))
    }

    /// When the number cell narrows back, for a board that stops changing.
    var narrowsAt: TimeInterval? { numberCell.narrowsAt }

    /// The sub-gauge's width for this tick (its nominal width or its table's, whichever is more).
    func width(_ tick: BoardMetricsTick, now: TimeInterval) -> CGFloat {
        let readings = BoardGaugeReadings(tick, l10n: l10n)
        let cell = prepare(tick, readings: readings, now: now)
        return max(nominal, table.map { $0.width(numberCell: cell) } ?? 0).rounded(.up)
    }

    func items(_ tick: BoardMetricsTick, in frame: BoardGaugeFrame) -> [BoardContentItem] {
        let readings = BoardGaugeReadings(tick, l10n: l10n)
        let cell = prepare(tick, readings: readings, now: frame.now)
        let width = max(nominal, table.map { $0.width(numberCell: cell) } ?? 0).rounded(.up)
        let center = frame.right - width / 2
        let stack = metrics.stack(intelligence: readings.intelligenceOn, thinking: readings.thinking != nil,
                                  cost: readings.cost != nil)
        let top = frame.top + (frame.height - CGFloat(stack.height)) / 2
        let memory = frame.memory
        var items: [BoardContentItem] = []
        scoreArea = nil
        if let main = stack.main {
            items += dial(tick, center: center, top: top + CGFloat(main), frame: frame)
        }
        if let table {
            let left = center - table.width(numberCell: cell) / 2
            for line in stack.rows {
                let (reading, key) = line.key == .thinking ? (readings.thinking, "think") : (readings.cost, "cost")
                guard let reading else { continue }
                items += table.items(reading, key: "gauge." + key, left: left, numberCell: cell,
                                     baseline: top + CGFloat(line.baseline), previous: memory.previousMetric(key),
                                     fresh: memory.fresh)
            }
        }
        return items
    }

    // MARK: Dial

    private func dial(_ tick: BoardMetricsTick, center: CGFloat, top: CGFloat, frame gauge: BoardGaugeFrame) -> [BoardContentItem] {
        let palette = self.palette, metrics = self.metrics
        let reading = tick.intelligence
        let (shown, motion) = score.update(reading, previous: gauge.memory.previousHeroScore, fresh: gauge.memory.fresh,
                                           now: gauge.now, reduceMotion: gauge.reduceMotion)
        let resting = reading.isResting, provisional = reading.isProvisional
        let diameter = CGFloat(metrics.dial)
        let radius = CGFloat(metrics.arcRadius), stroke = CGFloat(metrics.arcStroke), tip = CGFloat(metrics.tipRadius)
        let margin: CGFloat = 6
        let box = CGRect(x: center - diameter / 2 - margin, y: top - margin, width: diameter + 2 * margin, height: diameter + 2 * margin)
        scoreArea = CGRect(x: center - diameter / 2, y: top, width: diameter, height: CGFloat(metrics.dialHeight))
        let local = CGPoint(x: diameter / 2 + margin, y: diameter / 2 + margin)
        let theme = AnyHashable([AnyHashable(palette.dark), AnyHashable(palette.highContrast), AnyHashable(size.rawValue)])
        let look = AnyHashable([theme, AnyHashable(resting), AnyHashable(provisional)])
        func arc() -> CGPath {
            let path = CGMutablePath()
            path.addArc(center: local, radius: radius, startAngle: Self.arcStart, endAngle: Self.arcStart + Self.arcSweep, clockwise: false)
            return path
        }
        // The reference tick: at the top of the sweep (f = 0.5), from 1 pt inside the track's inner edge 0.05 D toward the
        // centre. It belongs to the track: always drawn, never animated.
        let tickOuter = radius - stroke / 2 - 1
        let tickInner = tickOuter - 0.05 * diameter
        var track = BoardContentItem(key: "gauge.track", frame: box, motion: .glide(fade: 0.25), revision: theme) { context, _ in
            context.setStrokeColor(palette.dialTrack.cgColor)
            context.setLineWidth(stroke)
            context.setLineCap(.round)
            context.addPath(arc())
            context.strokePath()
            context.setStrokeColor((palette.highContrast ? palette.ink2 : palette.ink3).cgColor)
            context.setLineWidth(1)
            context.move(to: CGPoint(x: local.x, y: local.y - tickOuter))
            context.addLine(to: CGPoint(x: local.x, y: local.y - tickInner))
            context.strokePath()
        }
        track.group = .hero

        // The provisional mark's dots: dash 0, gap 2 × the stroke, from the start of the sweep.
        let pitch = 2 * stroke
        let ring = ProvisionalMark.tipRing(at: .zero, lineWidth: stroke)
        let dots = BoardContentItem.Sweep.Dots(pitch: pitch, radius: stroke / 2, clearance: ring.rect.width / 2 + 0.5 * stroke)
        let dialBox = CGRect(x: margin, y: margin, width: diameter, height: CGFloat(metrics.dialHeight))
        var value = BoardContentItem(key: "gauge.arc", frame: box, motion: .glide(fade: 0.25), revision: look) { context, _ in
            let outline: CGPath
            if provisional {
                let path = CGMutablePath()
                var length: CGFloat = 0
                while length <= Self.arcSweep * radius + 0.01 {
                    let angle = Self.arcStart + length / radius
                    let point = CGPoint(x: local.x + radius * cos(angle), y: local.y + radius * sin(angle))
                    path.addEllipse(in: CGRect(x: point.x - stroke / 2, y: point.y - stroke / 2, width: stroke, height: stroke))
                    length += pitch
                }
                outline = path
            } else {
                outline = arc().copy(strokingWithWidth: stroke, lineCap: .round, lineJoin: .round, miterLimit: 10)
            }
            // Increase Contrast: ink, a resting arc in the quiet ink rather than faded into the track.
            if palette.highContrast {
                context.setFillColor((resting ? palette.quiet : palette.ink).cgColor)
                context.addPath(outline)
                return context.fillPath()
            }
            context.saveGState()
            if resting {
                context.setAlpha(0.35)
                context.beginTransparencyLayer(auxiliaryInfo: nil)
            }
            if !resting {
                BoardGlow.fill(palette.pearl[2].opacity(0.6), blur: 3, in: context) { $0.addPath(outline); $0.fillPath() }
            }
            context.saveGState()
            context.addPath(outline)
            context.clip()
            if let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB),
                                         colors: palette.pearl.map(\.cgColor) as CFArray, locations: [0, 0.5, 1]) {
                context.drawLinearGradient(gradient, start: CGPoint(x: dialBox.minX, y: dialBox.maxY),
                                           end: CGPoint(x: dialBox.maxX, y: dialBox.minY),
                                           options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
            }
            context.restoreGState()
            if resting { context.endTransparencyLayer() }
            context.restoreGState()
        }
        value.group = .hero
        let fraction = BoardIntelligenceScale.arc(shown)
        value.sweep = BoardContentItem.Sweep(center: local, radius: radius, width: stroke + 10, start: Self.arcStart,
                                             sweep: Self.arcSweep, fraction: fraction, dots: provisional ? dots : nil)
        if let fade = motion.shapeFade { value.accents.append(.crossfade(fade)) }
        var items = [track, value]

        if shown != nil {
            let start = CGPoint(x: local.x + radius * cos(Self.arcStart), y: local.y + radius * sin(Self.arcStart))
            var dot = BoardContentItem(key: "gauge.tip", frame: box, motion: .glide(fade: 0.2), revision: look) { context, _ in
                let rect = CGRect(x: start.x - tip, y: start.y - tip, width: 2 * tip, height: 2 * tip)
                let glows = palette.dark && palette.glows && !resting
                let faded = resting && !palette.highContrast
                let contrastInk = resting ? palette.quiet : palette.ink
                context.saveGState()
                if faded {
                    context.setAlpha(0.35)
                    context.beginTransparencyLayer(auxiliaryInfo: nil)
                }
                if provisional {
                    // The hollow ring of a provisional score, in the gradient's last color (ink with Increase Contrast).
                    let color = palette.highContrast ? contrastInk : palette.pearl[2]
                    if glows {
                        let ring = ProvisionalMark.tipRing(at: start, lineWidth: stroke)
                        BoardGlow.fill(palette.pearl[2].opacity(0.6), blur: 3, in: context) {
                            $0.setLineWidth(ring.stroke)
                            $0.setStrokeColor(CGColor(gray: 1, alpha: 1))
                            $0.strokeEllipse(in: ring.rect.insetBy(dx: ring.stroke / 2, dy: ring.stroke / 2))
                        }
                    }
                    ProvisionalMark.drawTipRing(in: context, at: start, lineWidth: stroke, color: color.cgColor, hole: nil)
                } else {
                    let fill = palette.highContrast ? contrastInk : palette.dark ? RGBColor.white : palette.pearl[2]
                    if glows {
                        BoardGlow.fill(palette.pearl[2], blur: 3, in: context) { $0.fillEllipse(in: rect) }
                    }
                    BoardDrawing.fillCircle(rect, color: fill, in: context)
                }
                if faded { context.endTransparencyLayer() }
                context.restoreGState()
            }
            dot.group = .hero
            dot.turn = Double(Self.arcSweep) * fraction
            if let fade = motion.shapeFade { dot.accents.append(.crossfade(fade)) }
            items.append(dot)
        }

        let labelFont = BoardFont(size: CGFloat(metrics.mainLabelSize), weight: .semibold, tracking: CGFloat(metrics.mainLabelTracking))
        let label = BoardLabel(l10n("intel.glyph"), labelFont, color: palette.ink3)
        let labelBaseline = top + CGFloat(metrics.mainLabelBaseline)
        let labelX = label.centredX(center, tracking: labelFont.tracking)
        var caption = BoardContentItem.part("gauge.iq.label", of: CGRect(x: 0, y: 0, width: CGFloat(size.side), height: CGFloat(size.side)),
                                            rect: label.box(x: labelX, baseline: labelBaseline),
                                            revision: AnyHashable([AnyHashable(label.text), theme])) { context in
            label.draw(in: context, x: labelX, baseline: labelBaseline)
        }
        caption.motion = .glide(fade: 0.25)
        caption.group = .hero
        items.append(caption)

        let numberFont = BoardFont(size: CGFloat(metrics.mainNumberSize), weight: NSFont.Weight(rawValue: 0.35), tracking: -0.02,
                                   tabular: true)
        let number = BoardScoreNumber(key: "gauge.iq.n", shown: shown, form: .compact, font: numberFont,
                                      color: reading.form == .live ? palette.ink : palette.quiet, anchor: .center(center),
                                      baseline: top + CGFloat(metrics.mainNumberBaseline), provisional: provisional,
                                      highContrast: palette.highContrast)
        items += number.items(fade: motion.numberFade)
        return items
    }
}
