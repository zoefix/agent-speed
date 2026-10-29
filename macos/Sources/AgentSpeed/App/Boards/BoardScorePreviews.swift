import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import SwiftUI

/// `--board-frames DIR` with `AGENT_SPEED_BOARD_SCORES=1`: the boards' 性能 (DESIGN 2c Live metrics; DESIGN 12) offscreen
/// — no windows, no screen capture. For each style: `scores-<style>-<theme>-<lang>[-contrast].png`, the sizes S, M and L
/// (rows) in every scenario of `BoardScorePreviewData` (columns); `board-<style>-M-<scenario>-<theme>.png`, one board
/// at 3× for detail; `detail-<style>[-<size>]-<theme>[-contrast].png`, the sub-gauge and the rows' 性能 column of every
/// scenario at 4×; and `card-<style>-<target>-<theme>[-en].png`, the score card beside an M board where it opens
/// (the hero's 性能, a group row, a sub row), on its solid surface (the material itself needs a real window). The
/// effects layer is left out; the glass backdrop is its tint without the system blur.
@MainActor
enum BoardScorePreviews {
    static func run(into directory: URL) {
        let environment = ProcessInfo.processInfo.environment
        let styles = environment["AGENT_SPEED_BOARD_STYLES"].map { $0.split(separator: ",").compactMap { BoardStyle(rawValue: String($0)) } }
            ?? BoardStyle.allCases
        for style in styles {
            for dark in [true, false] {
                let theme = dark ? "dark" : "light"
                for language in [LanguagePreference.zhHant, .en] {
                    let l10n = Localizer(preference: language)
                    write(grid(style: style, dark: dark, contrast: false, l10n: l10n),
                          to: directory, "scores-\(style.rawValue)-\(theme)-\(language.rawValue)")
                }
                let l10n = Localizer(preference: .zhHant)
                write(grid(style: style, dark: dark, contrast: true, l10n: l10n), to: directory,
                      "scores-\(style.rawValue)-\(theme)-zh-Hant-contrast")
                for scenario in BoardScorePreviewData.Scenario.allCases {
                    let board = Board(style: style, size: .medium, scenario: scenario, dark: dark, contrast: false, l10n: l10n)
                    write(board.image(scale: 3), to: directory, "board-\(style.rawValue)-M-\(scenario.rawValue)-\(theme)")
                }
                for contrast in [false, true] {
                    write(details(style: style, size: .medium, dark: dark, contrast: contrast, l10n: l10n), to: directory,
                          "detail-\(style.rawValue)-\(theme)\(contrast ? "-contrast" : "")")
                }
                for size in [BoardSize.small, .large] {
                    write(details(style: style, size: size, dark: dark, contrast: false, l10n: l10n), to: directory,
                          "detail-\(style.rawValue)-\(size.rawValue)-\(theme)")
                }
                for (target, english) in [(Target.hero, false), (.group, false), (.sub, false), (.group, true)] {
                    let l10n = Localizer(preference: english ? .en : .zhHant)
                    let board = Board(style: style, size: .medium, scenario: .live, dark: dark, contrast: false, l10n: l10n)
                    guard let shot = board.card(target) else { continue }
                    let url = directory.appendingPathComponent("card-\(style.rawValue)-\(target)-\(theme)\(english ? "-en" : "").png")
                    PreviewSnapshot.writeHosted(CardShot(shot: shot, l10n: l10n, dark: dark), dark: dark,
                                                background: Board.desk(dark: dark), to: url)
                    print(url.path)
                }
            }
        }
    }

    private static func write(_ image: CGImage?, to directory: URL, _ name: String) {
        guard let image else { return }
        let url = directory.appendingPathComponent(name + ".png")
        PreviewSnapshot.write(image, to: url)
        print(url.path)
    }

    /// Sizes in rows, scenarios in columns, each column titled.
    private static func grid(style: BoardStyle, dark: Bool, contrast: Bool, l10n: Localizer) -> CGImage? {
        let scenarios = BoardScorePreviewData.Scenario.allCases
        let sizes: [BoardSize] = [.small, .medium, .large]
        let margin: CGFloat = 28, gap: CGFloat = 28, title: CGFloat = 22
        let column = CGFloat(BoardSize.large.side)
        let width = margin * 2 + CGFloat(scenarios.count) * column + CGFloat(scenarios.count - 1) * gap
        let height = margin * 2 + sizes.reduce(0) { $0 + CGFloat($1.side) + title } + CGFloat(sizes.count - 1) * gap
        guard let canvas = PreviewCanvas(size: CGSize(width: width, height: height), background: Board.desk(dark: dark)) else { return nil }
        let ink = dark ? NSColor(white: 1, alpha: 0.55) : NSColor(white: 0, alpha: 0.5)
        var y = margin
        for size in sizes {
            for (index, scenario) in scenarios.enumerated() {
                let x = margin + CGFloat(index) * (column + gap)
                canvas.text("\(scenario.rawValue.uppercased())  ·  \(size.rawValue)", at: CGPoint(x: x, y: y + 13), size: 11,
                            weight: .semibold, color: ink)
                let board = Board(style: style, size: size, scenario: scenario, dark: dark, contrast: contrast, l10n: l10n)
                board.draw(in: canvas.context, at: CGPoint(x: x, y: y + title))
            }
            y += title + CGFloat(size.side) + gap
        }
        return canvas.image
    }

    /// The sub-gauge (top) and the rows' 性能 column (bottom) of a board in every scenario, at 4×.
    private static func details(style: BoardStyle, size: BoardSize, dark: Bool, contrast: Bool, l10n: Localizer) -> CGImage? {
        let scenarios = BoardScorePreviewData.Scenario.allCases
        let scale: CGFloat = 4, side = CGFloat(size.side)
        let geometry = BoardGeometry(style, size, readouts: .all)
        let hero = CGRect(x: side * 0.56, y: 0, width: side * 0.44, height: CGFloat(geometry.rankingTop))
        let column = CGRect(x: side * 0.56, y: CGFloat(geometry.rankingTop), width: side * 0.44,
                            height: side - 34 - CGFloat(geometry.rankingTop))
        let gap: CGFloat = 12
        let sheet = CGSize(width: CGFloat(scenarios.count) * (hero.width + gap) + gap, height: hero.height + column.height + 3 * gap)
        guard let canvas = PreviewCanvas(size: sheet, scale: scale, background: Board.desk(dark: dark)) else { return nil }
        for (index, scenario) in scenarios.enumerated() {
            let board = Board(style: style, size: size, scenario: scenario, dark: dark, contrast: contrast, l10n: l10n)
            guard let image = board.image(scale: scale) else { continue }
            let x = gap + CGFloat(index) * (hero.width + gap)
            for (part, y) in [(hero, gap), (column, 2 * gap + hero.height)] {
                let pixels = CGRect(x: part.minX * scale, y: part.minY * scale, width: part.width * scale, height: part.height * scale)
                if let crop = image.cropping(to: pixels) { canvas.draw(crop, at: CGPoint(x: x, y: y), size: part.size) }
            }
        }
        return canvas.image
    }

    /// `--board-frames DIR` with `AGENT_SPEED_BOARD_FOCUS=1`: a row with the keyboard focus (DESIGN 12 *Keyboard*) in
    /// every style, dark and light, with Increase Contrast: `focus-<style>-group-<theme>[-contrast].png` (the second
    /// group row at 3×) and `focus-<style>-sub-<theme>.png` (a sub row with its card open, as Space shows it).
    static func runFocus(into directory: URL) {
        let l10n = Localizer(preference: .zhHant)
        for style in BoardStyle.allCases {
            for dark in [true, false] {
                let theme = dark ? "dark" : "light"
                for contrast in [false, true] {
                    let board = Board(style: style, size: .medium, scenario: .live, dark: dark, contrast: contrast, l10n: l10n)
                    let group = board.tick.groups.count > 1 ? board.tick.groups[1].id : board.tick.groups.first?.id
                    write(board.image(scale: 3, focused: group), to: directory,
                          "focus-\(style.rawValue)-group-\(theme)\(contrast ? "-contrast" : "")")
                }
                let board = Board(style: style, size: .medium, scenario: .live, dark: dark, contrast: false, l10n: l10n)
                guard let (key, row) = board.layout.subRows.min(by: { $0.value.minY < $1.value.minY }),
                      let model = BoardScoreCards.row(key, in: board.tick, l10n: l10n) else { continue }
                let shot = CardShot.Shot(board: board.image(focused: key), side: board.side, model: model, top: row.minY)
                let url = directory.appendingPathComponent("focus-\(style.rawValue)-sub-\(theme).png")
                PreviewSnapshot.writeHosted(CardShot(shot: shot, l10n: l10n, dark: dark), dark: dark,
                                            background: Board.desk(dark: dark), to: url)
                print(url.path)
            }
        }
    }

    /// `--board-frames DIR` with `AGENT_SPEED_BOARD_CENTRE=1`: two-line group rows with their figures centred and
    /// 2 pt larger (DESIGN 2c *The second line*), M and L, dark and light, every style: `centre-<style>-<size>-<theme>.png`
    /// (the board at 3×, a red hairline on each group row's centre line) and `centre-detail-<style>-<size>-<theme>.png`
    /// (the sub-gauge and the figures' columns of every scenario at 4×); `centre-timing-interval-<size>-<theme>[-en].png`
    /// (timing's interval centred with the other figures) and `line2-neon-<size>-<lang>.png` (line 2 up to the 性能
    /// column), at 3×.
    static func runCentre(into directory: URL) {
        let l10n = Localizer(preference: .zhHant)
        let english = Localizer(preference: .en)
        for size in [BoardSize.medium, .large] {
            for dark in [true, false] {
                let board = Board(style: .timing, size: size, scenario: .live, dark: dark, contrast: false, l10n: l10n)
                write(board.image(scale: 3, centreLines: true), to: directory,
                      "centre-timing-interval-\(size.rawValue)-\(dark ? "dark" : "light")")
            }
            let board = Board(style: .timing, size: size, scenario: .live, dark: true, contrast: false, l10n: english)
            write(board.image(scale: 3, centreLines: true), to: directory, "centre-timing-interval-\(size.rawValue)-dark-en")
            for (language, localizer) in [("zh-Hant", l10n), ("en", english)] {
                let neon = Board(style: .neon, size: size, scenario: .live, dark: true, contrast: false, l10n: localizer)
                write(neon.image(scale: 3), to: directory, "line2-neon-\(size.rawValue)-\(language)")
            }
        }
        for style in BoardStyle.allCases {
            for size in [BoardSize.medium, .large] {
                for dark in [true, false] {
                    let theme = dark ? "dark" : "light"
                    let board = Board(style: style, size: size, scenario: .live, dark: dark, contrast: false, l10n: l10n)
                    write(board.image(scale: 3, centreLines: true), to: directory, "centre-\(style.rawValue)-\(size.rawValue)-\(theme)")
                    write(details(style: style, size: size, dark: dark, contrast: false, l10n: l10n), to: directory,
                          "centre-detail-\(style.rawValue)-\(size.rawValue)-\(theme)")
                }
            }
        }
    }

    enum Target: String {
        case hero, group, sub
    }

    /// One board of a scenario, laid out by its style's content builder.
    @MainActor
    private struct Board {
        let style: BoardStyle
        let size: BoardSize
        let dark: Bool
        let contrast: Bool
        let l10n: Localizer
        let tick: BoardTick
        let layout: BoardContentLayout

        init(style: BoardStyle, size: BoardSize, scenario: BoardScorePreviewData.Scenario, dark: Bool, contrast: Bool, l10n: Localizer) {
            self.style = style
            self.size = size
            self.dark = dark
            self.contrast = contrast
            self.l10n = l10n
            let feed = BoardSimFeed(simulation: BoardSimulation(scenario: "team"), style: style, size: size, dark: dark, l10n: l10n,
                                    highContrast: contrast, metrics: .init())
            for _ in 0..<40 { feed.step() }
            var tick = BoardScorePreviewData.tick(feed.tick, scenario: scenario)
            if scenario == .live, let first = tick.groups.first {
                // Three groups, the first expanded, so its sub rows (one unverified) show at M and L.
                let geometry = BoardGeometry(style, size, readouts: tick.metrics.readouts)
                tick.rows = BoardLayoutPlanner.plan(tick.groups.prefix(3).map { group in
                    BoardPlanGroup(group) { id in group.subs.first { $0.id == id }?.live.output ?? 0 }
                }, metrics: BoardLayoutMetrics(geometry), expanded: first.id)
            }
            self.tick = tick
            let builder = BoardContentBuilders.make(style: style, size: size, dark: dark, l10n: l10n, capsule: nil,
                                                    highContrast: contrast)
            layout = builder.layout(tick)
        }

        static func desk(dark: Bool) -> NSColor {
            dark ? NSColor(srgbRed: 0x12 / 255, green: 0x13 / 255, blue: 0x18 / 255, alpha: 1)
                : NSColor(srgbRed: 0xE6 / 255, green: 0xE8 / 255, blue: 0xEE / 255, alpha: 1)
        }

        var side: CGFloat { CGFloat(size.side) }

        /// The board alone on the desk.
        func image(scale: CGFloat = 2, lifted: String? = nil, focused: String? = nil, centreLines: Bool = false) -> CGImage? {
            guard let canvas = PreviewCanvas(size: CGSize(width: side, height: side), scale: scale,
                                             background: Self.desk(dark: dark)) else { return nil }
            draw(in: canvas.context, at: .zero, lifted: lifted, focused: focused)
            if centreLines {
                // A hairline on each two-line group row's centre line, over the figures' columns only.
                let geometry = BoardGeometry(style, size, readouts: tick.metrics.readouts)
                canvas.context.setFillColor(CGColor(srgbRed: 1, green: 0.2, blue: 0.2, alpha: 0.85))
                let reach: CGFloat = style == .timing ? 140 : 92
                for (_, anchor) in layout.groupRows where geometry.twoLine {
                    let y = anchor.row.minY + CGFloat(geometry.figureCentre)
                    canvas.context.fill(CGRect(x: anchor.row.maxX - reach, y: y - 0.125, width: reach, height: 0.25))
                }
            }
            return canvas.image
        }

        /// Composites the cached items as the board's layers show them (y down, board points from `origin`).
        func draw(in context: CGContext, at origin: CGPoint, lifted: String? = nil, focused: String? = nil) {
            let corner = CGFloat(style.cornerRadius)
            let board = CGRect(origin: origin, size: CGSize(width: side, height: side))
            let path = CGPath(roundedRect: board, cornerWidth: corner, cornerHeight: corner, transform: nil)
            context.saveGState()
            context.addPath(path)
            context.setFillColor(BoardHostView.slabColor(style, dark: dark).cgColor)
            context.fillPath()
            context.addPath(path)
            context.clip()
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
            var items = layout.items
            if let row = items.first(where: { $0.key == focused }) {
                items.append(BoardFocusRing.item(around: row, style: style, dark: dark, highContrast: contrast))
            }
            for item in items {
                context.saveGState()
                let shown = item.shownFrame
                context.translateBy(x: origin.x + shown.minX, y: origin.y + shown.minY)
                context.scaleBy(x: item.frame.width > 0 ? shown.width / item.frame.width : 1, y: 1)
                let plate = item.key == lifted ? BoardPresenter.lifted(item.plate, dark: dark) : item.plate
                if let plate {
                    context.addPath(CGPath(roundedRect: CGRect(origin: .zero, size: item.frame.size), cornerWidth: plate.cornerRadius,
                                           cornerHeight: plate.cornerRadius, transform: nil))
                    context.setFillColor(plate.color)
                    context.fillPath()
                }
                item.composited(context, item.frame.size)
                context.restoreGState()
            }
            NSGraphicsContext.restoreGraphicsState()
            context.restoreGState()
        }

        /// The card where `target` opens it: its model, the row it is level with and the board with that row lifted.
        func card(_ target: Target) -> CardShot.Shot? {
            switch target {
            case .hero:
                guard let model = BoardScoreCards.hero(tick, l10n: l10n), let area = layout.scoreArea else { return nil }
                return CardShot.Shot(board: image(), side: side, model: model, top: area.minY)
            case .group:
                guard let group = tick.groups.first, let row = layout.groupRows[group.id]?.row else { return nil }
                return CardShot.Shot(board: image(lifted: group.id), side: side, model: BoardScoreCards.group(group, l10n: l10n),
                                     top: row.minY)
            case .sub:
                guard let (key, row) = layout.subRows.min(by: { $0.value.minY < $1.value.minY }),
                      let model = BoardScoreCards.row(key, in: tick, l10n: l10n) else { return nil }
                return CardShot.Shot(board: image(lifted: key), side: side, model: model, top: row.minY)
            }
        }
    }

    /// The board and its card 8 pt beside it, the card's top level with its row.
    @MainActor
    private struct CardShot: View {
        struct Shot {
            var board: CGImage?
            var side: CGFloat
            var model: PerfCardModel
            var top: CGFloat
        }

        let shot: Shot
        let l10n: Localizer
        let dark: Bool

        var body: some View {
            HStack(alignment: .top, spacing: PerfCardPlacement.gap) {
                if let board = shot.board {
                    Image(decorative: board, scale: 2)
                        .frame(width: shot.side, height: shot.side)
                }
                PerfCardView(model: shot.model, l10n: l10n)
                    .modifier(PerfCardSolidSurface())
                    .shadow(color: .black.opacity(dark ? 0.5 : 0.16), radius: 18, y: 10)
                    .shadow(color: .black.opacity(dark ? 0.3 : 0.08), radius: 3, y: 1)
                    .padding(.top, shot.top)
            }
            .padding(28)
            .environment(\.locale, l10n.locale)
        }
    }
}
