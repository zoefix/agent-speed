import AgentSpeedCore
import AppKit
import SwiftUI

/// The daily trend of the selected row (DESIGN 12, *Trend*): the log scale from 250 with gridlines from 500 and the
/// reference 1000, the daily pooled scores as a line over a fading area with gaps below 20 timed requests, the usual
/// line, and a crosshair with a tooltip card under the pointer or the keyboard's arrows.
struct PerfTrendChart: View {
    let row: PerfHistoryRow
    let days: DayRange
    let singleDay: DayKey?
    let height: CGFloat
    let l10n: Localizer

    /// `hovered`: a day under the pointer from the start (previews).
    init(row: PerfHistoryRow, days: DayRange, singleDay: DayKey?, height: CGFloat, l10n: Localizer, hovered: Int? = nil) {
        self.row = row
        self.days = days
        self.singleDay = singleDay
        self.height = height
        self.l10n = l10n
        _hovered = State(initialValue: hovered)
    }

    @State private var width: CGFloat = 600
    @State private var hovered: Int?
    @State private var keyboard: Int?
    @State private var tipSize = CGSize(width: 160, height: 60)
    @FocusState private var focused: Bool
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        let geometry = PerfTrendGeometry(size: CGSize(width: width, height: height), days: days.count,
                                         maxScore: row.trend.compactMap(\.score).max())
        let active = hovered ?? (focused ? keyboard ?? lastScored : nil)
        ZStack(alignment: .topLeading) {
            PerfTrendCanvas(row: row, days: days, singleDay: singleDay, geometry: geometry, l10n: l10n,
                            dark: colorScheme == .dark, increasedContrast: contrast == .increased)
            if let active, row.trend.indices.contains(active) {
                crosshair(geometry, index: active)
                tooltip(geometry, index: active)
            }
        }
        .frame(maxWidth: .infinity, minHeight: height, maxHeight: height, alignment: .topLeading)
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { measured in
            if abs(measured - width) > 0.5 { width = measured }
        }
        .contentShape(Rectangle())
        .onContinuousHover { phase in
            switch phase {
            case .active(let location):
                let index = location.y >= 0 && location.y <= height ? geometry.index(atX: location.x) : nil
                if index != hovered { hovered = index }
            case .ended:
                hovered = nil
            }
        }
        .focusable()
        .focused($focused)
        .focusEffectDisabled()
        .onKeyPress(keys: [.leftArrow, .rightArrow]) { press in
            guard !row.trend.isEmpty else { return .ignored }
            let current = keyboard ?? lastScored ?? row.trend.count - 1
            let step = press.key == .leftArrow ? -1 : 1
            keyboard = min(max(current + step, 0), row.trend.count - 1)
            return .handled
        }
        .onChange(of: row.id) { _, _ in keyboard = nil }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(PerfHistoryText.chartLabel(row, l10n))
        .accessibilityAddTraits(.isImage)
    }

    private var lastScored: Int? { row.trend.lastIndex { $0.score != nil } }

    private func crosshair(_ geometry: PerfTrendGeometry, index: Int) -> some View {
        let plot = geometry.plot
        return Rectangle()
            .fill(PerfHistoryPalette.crosshair(increasedContrast: contrast == .increased))
            .frame(width: 1, height: plot.height)
            .offset(x: geometry.x(index).rounded() - 0.5, y: plot.minY)
            .allowsHitTesting(false)
            .accessibilityHidden(true)
    }

    private func tooltip(_ geometry: PerfTrendGeometry, index: Int) -> some View {
        let point = row.trend[index]
        let x = geometry.x(index)
        let anchorY = point.score.map(geometry.y) ?? geometry.plot.midY
        let gap: CGFloat = 12
        let left = x + gap + tipSize.width <= width ? x + gap : max(0, x - gap - tipSize.width)
        let top = min(max(anchorY - tipSize.height / 2, 0), max(height - tipSize.height, 0))
        return PerfTrendTooltip(point: point, l10n: l10n)
            .fixedSize()
            .onGeometryChange(for: CGSize.self) { $0.size } action: { size in
                if abs(size.width - tipSize.width) > 0.5 || abs(size.height - tipSize.height) > 0.5 { tipSize = size }
            }
            .offset(x: left, y: top)
            .allowsHitTesting(false)
            .accessibilityHidden(true)
    }
}

/// The chart's static layers.
struct PerfTrendCanvas: View {
    let row: PerfHistoryRow
    let days: DayRange
    let singleDay: DayKey?
    let geometry: PerfTrendGeometry
    let l10n: Localizer
    let dark: Bool
    let increasedContrast: Bool

    var body: some View {
        let marks = HistoryChartMath.marks(days, slot: Double(geometry.slot))
        let scores = row.trend.map(\.score)
        let tint = row.source.tint(dark: dark)
        Canvas { context, _ in
            let plot = geometry.plot
            if let singleDay, days.contains(singleDay) {
                let index = days.index(of: singleDay)
                let slot = CGRect(x: plot.minX + CGFloat(index) * geometry.slot, y: plot.minY, width: geometry.slot,
                                  height: plot.height)
                context.fill(Path(roundedRect: slot.insetBy(dx: 1, dy: 0), cornerRadius: 3),
                             with: .color(PerfHistoryPalette.daySlot(increasedContrast: increasedContrast)))
            }
            drawGrid(&context, plot)
            drawMonths(&context, plot, marks)
            drawDays(&context, plot, marks)
            drawSeries(&context, plot, scores, tint)
            if scores.allSatisfy({ $0 == nil }) {
                let text = Text(PerfHistoryText.chartLabel(row, l10n))
                    .font(PerfHistoryFont.emptyChart)
                    .foregroundStyle(tertiary)
                context.draw(text, at: CGPoint(x: plot.midX, y: plot.midY), anchor: .center)
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    private var tertiary: Color { PerfHistoryPalette.tertiary(dark: dark, increasedContrast: increasedContrast) }

    private func drawGrid(_ context: inout GraphicsContext, _ plot: CGRect) {
        let grid = HistoryPalette.grid(dark: dark, increasedContrast: increasedContrast)
        let secondary = HistoryPalette.secondary(dark: dark, increasedContrast: increasedContrast)
        for value in geometry.gridValues {
            let reference = value == 1000
            let y = geometry.y(value).rounded() + 0.5
            var line = Path()
            line.move(to: CGPoint(x: plot.minX, y: y))
            line.addLine(to: CGPoint(x: plot.maxX, y: y))
            context.stroke(line, with: .color(reference ? PerfHistoryPalette.reference(increasedContrast: increasedContrast) : grid),
                           lineWidth: 1)
            let label = Text(PerfFormat.full(Int(value)))
                .font(reference ? PerfHistoryFont.axisReference : PerfHistoryFont.axis)
                .foregroundStyle(reference ? secondary : tertiary)
            context.draw(label, at: CGPoint(x: plot.minX - 6, y: y), anchor: .trailing)
        }
    }

    private func drawMonths(_ context: inout GraphicsContext, _ plot: CGRect, _ marks: ChartAxisMarks) {
        let rule = HistoryPalette.monthRule(dark: dark, increasedContrast: increasedContrast)
        for index in marks.rules {
            let x = (plot.minX + CGFloat(index) * geometry.slot).rounded() + 0.5
            var path = Path()
            path.move(to: CGPoint(x: x, y: plot.minY - 12))
            path.addLine(to: CGPoint(x: x, y: plot.maxY))
            context.stroke(path, with: .color(rule), style: StrokeStyle(lineWidth: 1, dash: [2, 3]))
        }
        for label in marks.months {
            let x = label.index == 0 ? plot.minX : plot.minX + CGFloat(label.index) * geometry.slot + 4
            let text = Text(HistoryText.month(label.day, withYear: label.kind == .year, l10n))
                .font(PerfHistoryFont.axis.weight(.medium))
                .foregroundStyle(tertiary)
            context.draw(text, at: CGPoint(x: x, y: 0), anchor: .topLeading)
        }
    }

    private func drawDays(_ context: inout GraphicsContext, _ plot: CGRect, _ marks: ChartAxisMarks) {
        for index in marks.days {
            let text = context.resolve(Text(HistoryText.axisDay(days.day(at: index), l10n))
                .font(PerfHistoryFont.axis)
                .foregroundStyle(tertiary))
            let size = text.measure(in: CGSize(width: 200, height: 20))
            let centre = geometry.x(index)
            let left = min(max(centre - size.width / 2, plot.minX - 4), plot.maxX + 4 - size.width)
            context.draw(text, at: CGPoint(x: left, y: plot.maxY + 5), anchor: .topLeading)
        }
    }

    private func drawSeries(_ context: inout GraphicsContext, _ plot: CGRect, _ scores: [Double?], _ tint: Color) {
        let runs = PerfTrendGeometry.runs(scores)
        let points = scores.enumerated().map { index, score in score.map { CGPoint(x: geometry.x(index), y: geometry.y($0)) } }
        for run in runs where run.count > 1 {
            let line = Path { path in
                for index in run {
                    guard let point = points[index] else { continue }
                    if index == run.lowerBound { path.move(to: point) } else { path.addLine(to: point) }
                }
            }
            var area = line
            area.addLine(to: CGPoint(x: geometry.x(run.upperBound - 1), y: plot.maxY))
            area.addLine(to: CGPoint(x: geometry.x(run.lowerBound), y: plot.maxY))
            area.closeSubpath()
            let top = run.compactMap { points[$0]?.y }.min() ?? plot.minY
            context.fill(area, with: .linearGradient(Gradient(colors: [tint.opacity(0.12), tint.opacity(0)]),
                                                     startPoint: CGPoint(x: 0, y: top), endPoint: CGPoint(x: 0, y: plot.maxY)))
            context.stroke(line, with: .color(tint), style: StrokeStyle(lineWidth: 2, lineCap: .round, lineJoin: .round))
        }
        if let usual = row.usual {
            let y = geometry.y(usual)
            var path = Path()
            path.move(to: CGPoint(x: plot.minX, y: y))
            path.addLine(to: CGPoint(x: plot.maxX, y: y))
            context.stroke(path, with: .color(tint.opacity(increasedContrast ? 1 : 0.6)),
                           style: StrokeStyle(lineWidth: 1, dash: [4, 3]))
        }
        let latest = scores.lastIndex { $0 != nil }
        let background = Color(nsColor: .windowBackgroundColor)
        let card = HistoryPalette.cardFill(dark: dark)
        for (index, point) in points.enumerated() {
            guard let point else { continue }
            let radius: CGFloat = index == latest ? 3 : 2.25
            let ring = Path(ellipseIn: CGRect(x: point.x - radius - 0.75, y: point.y - radius - 0.75,
                                              width: (radius + 0.75) * 2, height: (radius + 0.75) * 2))
            context.fill(ring, with: .color(background))
            context.fill(ring, with: .color(card))
            context.fill(Path(ellipseIn: CGRect(x: point.x - radius + 0.75, y: point.y - radius + 0.75,
                                                width: (radius - 0.75) * 2, height: (radius - 0.75) * 2)),
                         with: .color(tint))
        }
    }
}
